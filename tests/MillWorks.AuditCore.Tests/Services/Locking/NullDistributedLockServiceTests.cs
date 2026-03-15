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
}
