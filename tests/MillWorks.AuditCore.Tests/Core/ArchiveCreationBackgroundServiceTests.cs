using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Core;

[TestFixture]
[Category("Unit")]
public sealed class ArchiveCreationBackgroundServiceTests
{
    private Mock<IServiceProvider> _mockServiceProvider = null!;
    private Mock<IServiceScopeFactory> _mockScopeFactory = null!;
    private Mock<IServiceScope> _mockScope = null!;
    private Mock<IServiceProvider> _mockScopeProvider = null!;
    private Mock<IAuditArchivalService> _mockArchivalService = null!;
    private Mock<ILogger<ArchiveCreationBackgroundService>> _mockLogger = null!;

    [SetUp]
    public void SetUp()
    {
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopeProvider = new Mock<IServiceProvider>();
        _mockArchivalService = new Mock<IAuditArchivalService>();
        _mockLogger = new Mock<ILogger<ArchiveCreationBackgroundService>>();

        _mockServiceProvider.Setup(p => p.GetService(typeof(IServiceScopeFactory)))
            .Returns(_mockScopeFactory.Object);
        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(IAuditArchivalService)))
            .Returns(_mockArchivalService.Object);
    }

    [Test]
    public async Task ExecuteAsync_Disabled_ReturnsImmediately()
    {
        var options = new ArchivalOptions { EnableBackgroundArchival = false };
        var service = CreateService(options);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);

        _mockArchivalService.Verify(
            a => a.ArchiveAuditEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task ExecuteAsync_UsesConfiguredStartupDelay()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new ArchivalOptions
        {
            EnableBackgroundArchival = true,
            StartupDelaySeconds = 5,
            ArchivalIntervalHours = 24,
            RetentionDays = 30
        };

        _mockArchivalService
            .Setup(a => a.ArchiveAuditEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchivalResult { Success = true });

        var service = CreateService(options, fakeTime);
        using var cts = new CancellationTokenSource();

        var executeTask = service.StartAsync(cts.Token);

        await Task.Delay(50);
        _mockArchivalService.Verify(
            a => a.ArchiveAuditEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Should not archive before startup delay");

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task ExecuteAsync_UsesTimeProviderForArchiveDate()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 22, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(fixedTime);
        var retentionDays = 30;
        var expectedArchiveBefore = fixedTime.AddDays(-retentionDays);
        DateTimeOffset? capturedArchiveBefore = null;

        var options = new ArchivalOptions
        {
            EnableBackgroundArchival = true,
            StartupDelaySeconds = 0,
            ArchivalIntervalHours = 24,
            RetentionDays = retentionDays
        };

        _mockArchivalService
            .Setup(a => a.ArchiveAuditEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<DateTimeOffset, string?, CancellationToken>((archiveBefore, _, _) => capturedArchiveBefore = archiveBefore)
            .ReturnsAsync(new AuditArchivalResult { Success = true });

        var service = CreateService(options, fakeTime);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        await Task.Delay(200);

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        Assert.That(capturedArchiveBefore, Is.Not.Null);
        Assert.That(capturedArchiveBefore!.Value.Date, Is.EqualTo(expectedArchiveBefore.Date));
    }

    private ArchiveCreationBackgroundService CreateService(ArchivalOptions options, TimeProvider? timeProvider = null)
    {
        return new ArchiveCreationBackgroundService(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            Options.Create(options),
            timeProvider);
    }

    private sealed class FakeTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => initialTime;
    }
}
