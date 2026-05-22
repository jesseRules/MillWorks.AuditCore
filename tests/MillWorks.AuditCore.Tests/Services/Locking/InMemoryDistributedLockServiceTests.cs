using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DistributedLocking.Implementations;

namespace MillWorks.AuditCore.Tests.Services.Locking;

/// <summary>
/// Tests for InMemoryDistributedLockService
/// </summary>
[TestFixture]
[Category("Unit")]
public class InMemoryDistributedLockServiceTests
{
    private Mock<ILogger<InMemoryDistributedLockService>> _mockLogger;
    private InMemoryDistributedLockService _service;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<InMemoryDistributedLockService>>();
        _service = new InMemoryDistributedLockService(_mockLogger.Object);
    }

    /// <summary>
    /// AcquireLockAsync returns a non-null disposable when lock is uncontested
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_UncontestedLock_Succeeds()
    {
        // Act
        using var lockHandle = await _service.AcquireLockAsync(
            "test-resource", TimeSpan.FromSeconds(30));

        // Assert
        Assert.That(lockHandle, Is.Not.Null);
        Assert.That(lockHandle, Is.InstanceOf<IDisposable>());
    }

    /// <summary>
    /// AcquireLockAsync throws when the same resource is already locked.
    /// Uses a CancellationToken to abort retries quickly rather than waiting for all 50 attempts.
    /// TaskCanceledException (subclass of OperationCanceledException) is thrown from Task.Delay.
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_ContestedLock_ThrowsOnCancellation()
    {
        // Arrange - acquire the lock first
        using var firstLock = await _service.AcquireLockAsync(
            "contested-resource", TimeSpan.FromMinutes(5));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act & Assert - second acquisition should throw TaskCanceledException
        // (Task.Delay throws TaskCanceledException when token is cancelled)
        var ex = Assert.CatchAsync<OperationCanceledException>(async () =>
            await _service.AcquireLockAsync(
                "contested-resource", TimeSpan.FromMinutes(5), cts.Token));

        Assert.That(ex, Is.Not.Null);
    }

    /// <summary>
    /// A held lock is NOT auto-released when the nominal expiry passes — the in-process
    /// implementation deliberately ignores TTL for held entries (see class header on
    /// InMemoryDistributedLockService). A second acquirer must wait for Dispose, or be
    /// cancelled, or hit the retry budget. Phase 6.5 finding: TTL-based cleanup let two
    /// writers into the integrity-chain critical section simultaneously, causing SQL
    /// Server deadlocks.
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_HeldLockPastNominalExpiry_CannotReacquireWithoutDispose()
    {
        // Arrange - acquire a lock with a very short nominal expiry, but DO NOT dispose it
        using var firstLock = await _service.AcquireLockAsync(
            "expiring-resource", TimeSpan.FromMilliseconds(100));

        // Wait well past the nominal expiry
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Act & Assert - second acquisition must NOT succeed while firstLock is alive,
        // even though the nominal expiry has elapsed
        var ex = Assert.CatchAsync<OperationCanceledException>(async () =>
            await _service.AcquireLockAsync(
                "expiring-resource", TimeSpan.FromSeconds(30), cts.Token));

        Assert.That(ex, Is.Not.Null);
    }

    /// <summary>
    /// Disposing a lock releases it so the same resource can be reacquired
    /// </summary>
    [Test]
    public async Task DisposeLock_ReleasesLock()
    {
        // Arrange - acquire and then dispose the lock
        var firstLock = await _service.AcquireLockAsync(
            "disposable-resource", TimeSpan.FromMinutes(5));
        firstLock.Dispose();

        // Act - reacquire the same resource
        using var secondLock = await _service.AcquireLockAsync(
            "disposable-resource", TimeSpan.FromMinutes(5));

        // Assert
        Assert.That(secondLock, Is.Not.Null);
    }

    /// <summary>
    /// Concurrent locks on different keys both succeed
    /// </summary>
    [Test]
    public async Task ConcurrentLocks_DifferentKeys_BothSucceed()
    {
        // Act - acquire locks on two different resources simultaneously
        var task1 = _service.AcquireLockAsync("key1", TimeSpan.FromSeconds(30));
        var task2 = _service.AcquireLockAsync("key2", TimeSpan.FromSeconds(30));

        var results = await Task.WhenAll(task1, task2);

        // Assert
        Assert.That(results[0], Is.Not.Null);
        Assert.That(results[1], Is.Not.Null);

        // Cleanup
        results[0].Dispose();
        results[1].Dispose();
    }

    /// <summary>
    /// Constructing the service logs a warning about process-local semantics
    /// </summary>
    [Test]
    public void Constructor_LogsMultiNodeWarning()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<InMemoryDistributedLockService>>();

        // Act
        _ = new InMemoryDistributedLockService(mockLogger.Object);

        // Assert — verify a warning was logged about process-local semantics
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("PROCESS-LOCAL ONLY")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
