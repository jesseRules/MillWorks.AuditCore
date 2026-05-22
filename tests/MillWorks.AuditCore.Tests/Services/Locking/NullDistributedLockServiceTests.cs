using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DistributedLocking.Implementations;

namespace MillWorks.AuditCore.Tests.Services.Locking;

/// <summary>
/// Tests for NullDistributedLockService (null object pattern)
/// </summary>
[TestFixture]
[Category("Unit")]
public class NullDistributedLockServiceTests
{
    private NullDistributedLockService _service;

    [SetUp]
    public void Setup()
    {
        _service = new NullDistributedLockService();
    }

    /// <summary>
    /// AcquireLockAsync always returns a non-null disposable
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_AlwaysSucceeds()
    {
        // Act
        using var lockHandle = await _service.AcquireLockAsync(
            "any-resource", TimeSpan.FromSeconds(30));

        // Assert
        Assert.That(lockHandle, Is.Not.Null);
        Assert.That(lockHandle, Is.InstanceOf<IDisposable>());
    }

    /// <summary>
    /// Disposing the returned lock does not throw
    /// </summary>
    [Test]
    public async Task Dispose_NoOp()
    {
        // Arrange
        var lockHandle = await _service.AcquireLockAsync(
            "some-resource", TimeSpan.FromSeconds(10));

        // Act & Assert - Dispose should not throw
        Assert.DoesNotThrow(() => lockHandle.Dispose());

        // Disposing a second time should also not throw
        Assert.DoesNotThrow(() => lockHandle.Dispose());
    }

    /// <summary>
    /// AcquireLockAsync logs a warning on first use
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_LogsWarningOnFirstUse()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<NullDistributedLockService>>();
        var serviceWithLogger = new NullDistributedLockService(mockLogger.Object);

        // Act
        using var _ = await serviceWithLogger.AcquireLockAsync("test", TimeSpan.FromSeconds(1));

        // Assert
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("NO mutual exclusion")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Warning is logged only once, not on every lock acquisition
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_LogsWarningOnlyOnce()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<NullDistributedLockService>>();
        var serviceWithLogger = new NullDistributedLockService(mockLogger.Object);

        // Act
        using var _ = await serviceWithLogger.AcquireLockAsync("test1", TimeSpan.FromSeconds(1));
        using var __ = await serviceWithLogger.AcquireLockAsync("test2", TimeSpan.FromSeconds(1));
        using var ___ = await serviceWithLogger.AcquireLockAsync("test3", TimeSpan.FromSeconds(1));

        // Assert — warning should be logged exactly once
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Service works without a logger (null logger)
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_WorksWithoutLogger()
    {
        // Arrange
        var serviceWithoutLogger = new NullDistributedLockService(null);

        // Act & Assert — should not throw
        using var lockHandle = await serviceWithoutLogger.AcquireLockAsync(
            "test", TimeSpan.FromSeconds(1));
        Assert.That(lockHandle, Is.Not.Null);
    }
}
