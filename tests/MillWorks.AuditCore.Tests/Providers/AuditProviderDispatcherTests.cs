using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Providers.Base;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Providers;

[TestFixture]
[Category("Unit")]
public class AuditProviderDispatcherTests
{
    private AuditProviderTypeMap _map;
    private Mock<IServiceProvider> _mockServiceProvider;
    private Mock<IAuditLogger> _mockLogger;
    private Mock<ILogger<AuditProviderDispatcher>> _mockLog;
    private AuditProviderDispatcher _dispatcher;

    [SetUp]
    public void Setup()
    {
        _map = new AuditProviderTypeMap();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockLogger = new Mock<IAuditLogger>();
        _mockLog = new Mock<ILogger<AuditProviderDispatcher>>();

        _dispatcher = new AuditProviderDispatcher(
            _map,
            _mockServiceProvider.Object,
            _mockLogger.Object,
            _mockLog.Object);
    }

    [Test]
    public async Task DispatchAsync_RegisteredProvider_CallsProviderAndLogs()
    {
        var mockProvider = new Mock<IAuditProvider>();
        mockProvider.Setup(static p => p.ShouldAuditAsync("Create", It.IsAny<object>()))
            .ReturnsAsync(true);
        mockProvider.Setup(static p => p.CreateAuditEventAsync("Create", It.IsAny<object>(), null))
            .ReturnsAsync(new AuditEvent { EventType = "Order.Create" });

        _map.Register("Order", typeof(IAuditProvider));
        _mockServiceProvider.Setup(static sp => sp.GetService(typeof(IAuditProvider)))
            .Returns(mockProvider.Object);

        var dispatches = new List<PendingProviderDispatch>
        {
            new("Order", "Create", new { Id = 1 }, null)
        };

        await _dispatcher.DispatchAsync(dispatches, CancellationToken.None);

        mockProvider.Verify(static p => p.ShouldAuditAsync("Create", It.IsAny<object>()), Times.Once);
        mockProvider.Verify(static p => p.CreateAuditEventAsync("Create", It.IsAny<object>(), null), Times.Once);
        _mockLogger.Verify(static l => l.LogBatchAsync(It.Is<IReadOnlyList<AuditEvent>>(static e => e.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task DispatchAsync_NoProviderRegistered_SkipsSilently()
    {
        var dispatches = new List<PendingProviderDispatch>
        {
            new("UnknownEntity", "Create", new { Id = 1 }, null)
        };

        await _dispatcher.DispatchAsync(dispatches, CancellationToken.None);

        _mockLogger.Verify(static l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DispatchAsync_ProviderSaysNoAudit_SkipsLogging()
    {
        var mockProvider = new Mock<IAuditProvider>();
        mockProvider.Setup(static p => p.ShouldAuditAsync("Delete", It.IsAny<object>()))
            .ReturnsAsync(false);

        _map.Register("Order", typeof(IAuditProvider));
        _mockServiceProvider.Setup(static sp => sp.GetService(typeof(IAuditProvider)))
            .Returns(mockProvider.Object);

        var dispatches = new List<PendingProviderDispatch>
        {
            new("Order", "Delete", new { Id = 1 }, null)
        };

        await _dispatcher.DispatchAsync(dispatches, CancellationToken.None);

        mockProvider.Verify(static p => p.CreateAuditEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<object>()), Times.Never);
        _mockLogger.Verify(static l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task DispatchAsync_ProviderThrows_LogsErrorAndContinues()
    {
        var mockProvider = new Mock<IAuditProvider>();
        mockProvider.Setup(static p => p.ShouldAuditAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ThrowsAsync(new InvalidOperationException("Provider error"));

        _map.Register("Order", typeof(IAuditProvider));
        _mockServiceProvider.Setup(static sp => sp.GetService(typeof(IAuditProvider)))
            .Returns(mockProvider.Object);

        var dispatches = new List<PendingProviderDispatch>
        {
            new("Order", "Create", new { Id = 1 }, null),
            new("Order", "Create", new { Id = 2 }, null)
        };

        // Should not throw - errors are caught and logged
        Assert.DoesNotThrowAsync(() => _dispatcher.DispatchAsync(dispatches, CancellationToken.None));
    }

    [Test]
    public async Task DispatchAsync_MultipleDispatches_ProcessesAll()
    {
        var mockProvider = new Mock<IAuditProvider>();
        mockProvider.Setup(static p => p.ShouldAuditAsync(It.IsAny<string>(), It.IsAny<object>()))
            .ReturnsAsync(true);
        mockProvider.Setup(static p => p.CreateAuditEventAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<object?>()))
            .ReturnsAsync(new AuditEvent());

        _map.Register("Order", typeof(IAuditProvider));
        _mockServiceProvider.Setup(static sp => sp.GetService(typeof(IAuditProvider)))
            .Returns(mockProvider.Object);

        var dispatches = new List<PendingProviderDispatch>
        {
            new("Order", "Create", new { Id = 1 }, null),
            new("Order", "Update", new { Id = 2 }, new { Id = 2 }),
            new("Order", "Delete", new { Id = 3 }, null)
        };

        await _dispatcher.DispatchAsync(dispatches, CancellationToken.None);

        _mockLogger.Verify(static l => l.LogBatchAsync(It.Is<IReadOnlyList<AuditEvent>>(static e => e.Count == 3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task DispatchAsync_EmptyDispatches_DoesNothing()
    {
        var dispatches = new List<PendingProviderDispatch>();

        await _dispatcher.DispatchAsync(dispatches, CancellationToken.None);

        _mockServiceProvider.Verify(static sp => sp.GetService(It.IsAny<Type>()), Times.Never);
    }
}

[TestFixture]
[Category("Unit")]
public class AuditProviderTypeMapTests
{
    private AuditProviderTypeMap _map;

    [SetUp]
    public void Setup()
    {
        _map = new AuditProviderTypeMap();
    }

    [Test]
    public void Register_And_GetProviderType_ReturnsRegisteredType()
    {
        _map.Register("Order", typeof(IAuditProvider));

        var result = _map.GetProviderType("Order");

        Assert.That(result, Is.EqualTo(typeof(IAuditProvider)));
    }

    [Test]
    public void GetProviderType_NonExistent_ReturnsNull()
    {
        var result = _map.GetProviderType("Unknown");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void HasProvider_Registered_ReturnsTrue()
    {
        _map.Register("Order", typeof(IAuditProvider));

        Assert.That(_map.HasProvider("Order"), Is.True);
    }

    [Test]
    public void HasProvider_NotRegistered_ReturnsFalse()
    {
        Assert.That(_map.HasProvider("Unknown"), Is.False);
    }

    [Test]
    public void Register_CaseInsensitive_FindsByDifferentCase()
    {
        _map.Register("Order", typeof(IAuditProvider));

        Assert.That(_map.HasProvider("order"), Is.True);
        Assert.That(_map.HasProvider("ORDER"), Is.True);
        Assert.That(_map.GetProviderType("oRdEr"), Is.EqualTo(typeof(IAuditProvider)));
    }

    [Test]
    public void Register_SameKey_OverwritesPrevious()
    {
        _map.Register("Order", typeof(IAuditProvider));
        _map.Register("Order", typeof(IDisposable));

        Assert.That(_map.GetProviderType("Order"), Is.EqualTo(typeof(IDisposable)));
    }
}
