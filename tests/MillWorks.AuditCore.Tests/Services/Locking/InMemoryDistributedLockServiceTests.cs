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
    /// AcquireLockAsync succeeds after lock with short TTL expires
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_LockExpires_CanReacquire()
    {
        // Arrange - acquire a lock with a very short expiry
        using var firstLock = await _service.AcquireLockAsync(
            "expiring-resource", TimeSpan.FromMilliseconds(100));

        // Wait for the lock to expire
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Act - reacquire should succeed because the lock expired
        using var secondLock = await _service.AcquireLockAsync(
            "expiring-resource", TimeSpan.FromSeconds(30));

        // Assert
        Assert.That(secondLock, Is.Not.Null);
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
}
