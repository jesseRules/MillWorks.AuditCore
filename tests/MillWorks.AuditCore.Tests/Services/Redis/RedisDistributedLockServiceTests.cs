using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.Redis;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Tests.Services.Redis;

/// <summary>
/// Unit tests for RedisDistributedLockService
/// </summary>
[TestFixture]
public class RedisDistributedLockServiceTests
{
    /// <summary>
    /// Mock Redis connection multiplexer
    /// </summary>
    private Mock<IConnectionMultiplexer> _mockRedis;

    /// <summary>
    /// Mock Redis database
    /// </summary>
    private Mock<IDatabase> _mockDatabase;

    /// <summary>
    /// Mock logger
    /// </summary>
    private Mock<ILogger<RedisDistributedLockService>> _mockLogger;

    /// <summary>
    /// Lock service under test
    /// </summary>
    private RedisDistributedLockService _lockService;

    /// <summary>
    /// Setup method to initialize mocks and the service under test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();
        _mockLogger = new Mock<ILogger<RedisDistributedLockService>>();

        _mockRedis
            .Setup(static x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        _lockService = new RedisDistributedLockService(
            _mockRedis.Object,
            _mockLogger.Object,
            maxRetries: 3,
            baseDelay: TimeSpan.FromMilliseconds(1),
            useJitter: false);
    }

    #region AcquireLockAsync Tests

    /// <summary>
    /// AcquireLockAsync with valid parameters acquires the lock successfully
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_WithValidParameters_AcquiresLockSuccessfully()
    {
        // Arrange
        var resource = "test-resource";
        var expiry = TimeSpan.FromSeconds(30);

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(true);

        // Act
        var lockHandle = await _lockService.AcquireLockAsync(resource, expiry);

        // Assert
        Assert.That(lockHandle, Is.Not.Null);
        _mockDatabase.Verify(x => x.StringSetAsync(
            It.Is<RedisKey>(k => k.ToString().Contains(resource)),
            It.IsAny<RedisValue>(),
            expiry,
            When.NotExists), Times.Once);
    }

    /// <summary>
    /// AcquireLockAsync prefixes the lock key and uses a unique owner value
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_UsesPrefixedKeyAndUniqueOwnerValue()
    {
        var capturedKeys = new List<string>();
        var capturedValues = new List<string>();
        var expiry = TimeSpan.FromSeconds(30);

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(true)
            .Callback<RedisKey, RedisValue, TimeSpan?, When>((key, value, _, _) =>
            {
                capturedKeys.Add(key!);
                capturedValues.Add(value!);
            });

        var first = await _lockService.AcquireLockAsync("resource-one", expiry);
        var second = await _lockService.AcquireLockAsync("resource-two", expiry);

        Assert.That(capturedKeys, Is.EqualTo(new[] { "lock:resource-one", "lock:resource-two" }));
        Assert.That(capturedValues, Has.Count.EqualTo(2));
        Assert.That(capturedValues[0], Is.Not.EqualTo(capturedValues[1]));

        first.Dispose();
        second.Dispose();
    }

    /// <summary>
    /// AcquireLockAsync retries on lock contention and eventually succeeds
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_WithLockContention_RetriesAndSucceeds()
    {
        // Arrange
        var resource = "contended-resource";
        var expiry = TimeSpan.FromSeconds(30);
        var callCount = 0;

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(() => ++callCount >= 3); // Succeed on third attempt

        // Act
        var lockHandle = await _lockService.AcquireLockAsync(resource, expiry);

        // Assert
        Assert.That(lockHandle, Is.Not.Null);
        _mockDatabase.Verify(x => x.StringSetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            expiry,
            When.NotExists), Times.Exactly(3));
    }

    /// <summary>
    /// AcquireLockAsync exceeds max retries and throws TimeoutException
    /// </summary>
    [Test]
    public void AcquireLockAsync_WithMaxRetriesExceeded_ThrowsTimeoutException()
    {
        // Arrange
        var resource = "locked-resource";
        var expiry = TimeSpan.FromSeconds(30);

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(false); // Always fail

        // Act & Assert
        var ex = Assert.ThrowsAsync<TimeoutException>(async () =>
            await _lockService.AcquireLockAsync(resource, expiry));

        Assert.That(ex!.Message, Does.Contain("Failed to acquire distributed lock"));
        Assert.That(ex.Message, Does.Contain(resource));
    }

    /// <summary>
    /// AcquireLockAsync with null resource throws ArgumentNullException
    /// </summary>
    [Test]
    public void AcquireLockAsync_WithNullResource_ThrowsArgumentNullException()
    {
        // Arrange
        string? resource = null;
        var expiry = TimeSpan.FromSeconds(30);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _lockService.AcquireLockAsync(resource!, expiry));
    }

    /// <summary>
    /// AcquireLockAsync with empty resource throws ArgumentException
    /// </summary>
    [Test]
    public void AcquireLockAsync_WithZeroExpiry_ThrowsArgumentException()
    {
        // Arrange
        var resource = "test-resource";
        var expiry = TimeSpan.Zero;

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _lockService.AcquireLockAsync(resource, expiry));

        Assert.That(ex!.Message, Does.Contain("Expiry must be greater than zero"));
    }

    /// <summary>
    /// AcquireLockAsync with negative expiry throws ArgumentException
    /// </summary>
    [Test]
    public void AcquireLockAsync_WithNegativeExpiry_ThrowsArgumentException()
    {
        // Arrange
        var resource = "test-resource";
        var expiry = TimeSpan.FromSeconds(-10);

        // Act & Assert
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await _lockService.AcquireLockAsync(resource, expiry));

        Assert.That(ex!.Message, Does.Contain("Expiry must be greater than zero"));
    }

    /// <summary>
    /// AcquireLockAsync with cancellation token propagates cancellation
    /// </summary>
    [Test]
    public Task AcquireLockAsync_WithCancellation_PropagatesCancellation()
    {
        // Arrange
        var resource = "test-resource";
        var expiry = TimeSpan.FromSeconds(30);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(false);

        // Act & Assert
        Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await _lockService.AcquireLockAsync(resource, expiry, cts.Token));
        return Task.CompletedTask;
    }

    #endregion

    #region Lock Disposal Tests

    /// <summary>
    /// DisposeLock with valid lock releases the lock in Redis
    /// </summary>
    [Test]
    public async Task DisposeLock_WithValidLock_ReleasesLockInRedis()
    {
        // Arrange
        var resource = "test-resource";
        var expiry = TimeSpan.FromSeconds(30);

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(true);

        _mockDatabase
            .Setup(static x => x.ScriptEvaluate(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .Returns(RedisResult.Create(1));

        // Act
        var lockHandle = await _lockService.AcquireLockAsync(resource, expiry);
        lockHandle.Dispose();

        // Assert
        _mockDatabase.Verify(static x => x.ScriptEvaluate(
            It.Is<string>(static s => s.Contains("redis.call('get', KEYS[1])")),
            It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>()), Times.Once);
    }

    /// <summary>
    /// DisposeLock when lock already expired logs a warning
    /// </summary>
    [Test]
    public async Task DisposeLock_WhenLockAlreadyExpired_LogsWarning()
    {
        // Arrange
        var resource = "test-resource";
        var expiry = TimeSpan.FromSeconds(30);

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(true);

        _mockDatabase
            .Setup(static x => x.ScriptEvaluate(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .Returns(RedisResult.Create(0)); // Lock already released

        // Act
        var lockHandle = await _lockService.AcquireLockAsync(resource, expiry);
        lockHandle.Dispose();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("already released or expired")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// DisposeLock when script fails logs an error
    /// </summary>
    [Test]
    public async Task DisposeLock_WhenScriptFails_LogsError()
    {
        // Arrange
        var resource = "test-resource";
        var expiry = TimeSpan.FromSeconds(30);

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(true);

        _mockDatabase
            .Setup(static x => x.ScriptEvaluate(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .Throws(new RedisException("Redis error"));

        // Act
        var lockHandle = await _lockService.AcquireLockAsync(resource, expiry);
        lockHandle.Dispose(); // Should not throw

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Error releasing")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// DisposeLock called multiple times only releases the lock once
    /// </summary>
    [Test]
    public async Task DisposeLock_MultipleTimes_OnlyReleasesOnce()
    {
        // Arrange
        var resource = "test-resource";
        var expiry = TimeSpan.FromSeconds(30);

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(true);

        _mockDatabase
            .Setup(static x => x.ScriptEvaluate(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .Returns(RedisResult.Create(1));

        // Act
        var lockHandle = await _lockService.AcquireLockAsync(resource, expiry);
        lockHandle.Dispose();
        lockHandle.Dispose(); // Second dispose

        // Assert
        _mockDatabase.Verify(static x => x.ScriptEvaluate(
            It.IsAny<string>(),
            It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>()), Times.Once); // Only once
    }

    /// <summary>
    /// DisposeLock uses the acquired owner value in the atomic release script
    /// </summary>
    [Test]
    public async Task DisposeLock_UsesOwnerValueInAtomicReleaseScript()
    {
        var expiry = TimeSpan.FromSeconds(30);
        RedisValue capturedLockValue = RedisValue.Null;
        RedisValue[]? releaseArgs = null;

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(true)
            .Callback<RedisKey, RedisValue, TimeSpan?, When>((_, value, _, _) =>
            {
                capturedLockValue = value;
            });

        _mockDatabase
            .Setup(x => x.ScriptEvaluate(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .Returns(RedisResult.Create(1))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((_, _, args, _) => releaseArgs = args);

        var lockHandle = await _lockService.AcquireLockAsync("owner-check", expiry);
        lockHandle.Dispose();

        Assert.That(releaseArgs, Is.Not.Null);
        Assert.That(releaseArgs!, Has.Length.EqualTo(1));
        Assert.That(releaseArgs[0], Is.EqualTo(capturedLockValue));
    }

    #endregion

    #region Integration Pattern Tests

    /// <summary>
    /// AcquireLockAsync used within a using statement automatically releases the lock
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_WithUsingStatement_AutomaticallyReleasesLock()
    {
        // Arrange
        var resource = "test-resource";
        var expiry = TimeSpan.FromSeconds(30);

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(true);

        _mockDatabase
            .Setup(static x => x.ScriptEvaluate(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .Returns(RedisResult.Create(1));

        // Act
        using (var lockHandle = await _lockService.AcquireLockAsync(resource, expiry))
        {
            Assert.That(lockHandle, Is.Not.Null);
        }

        // Assert
        _mockDatabase.Verify(static x => x.ScriptEvaluate(
            It.IsAny<string>(),
            It.IsAny<RedisKey[]>(),
            It.IsAny<RedisValue[]>()), Times.Once);
    }

    /// <summary>
    /// AcquireLockAsync for same resource blocks until first lock is released
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_WithExistingLock_WaitsForRelease()
    {
        // Arrange
        var resource = "shared-resource";
        var expiry = TimeSpan.FromSeconds(30);
        var lockReleased = false;

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(() => lockReleased);

        _mockDatabase
            .Setup(static x => x.ScriptEvaluate(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()))
            .Returns(RedisResult.Create(1))
            .Callback(() => lockReleased = true);

        // Act & Assert
        // First lock should fail initially
        var lockTask = _lockService.AcquireLockAsync(resource, expiry);
    
        // Let the first acquisition attempt fail before simulating release.
        await Task.Delay(1);
    
        // Simulate lock being released by another process
        lockReleased = true;
    
        // Now it should succeed
        var lockHandle = await lockTask;
        Assert.That(lockHandle, Is.Not.Null);
    }
    
    /// <summary>
    /// AcquireLockAsync when lock is held by another process eventually times out
    /// </summary>
    [Test]
    public void AcquireLockAsync_WhenLockHeldByOther_TimesOut()
    {
        // Arrange
        var resource = "locked-resource";
        var expiry = TimeSpan.FromSeconds(30);

        // Simulate lock already held by another process
        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(false); // Always return false = lock held

        // Act & Assert
        var ex = Assert.ThrowsAsync<TimeoutException>(async () =>
            await _lockService.AcquireLockAsync(resource, expiry));

        Assert.That(ex!.Message, Does.Contain("Failed to acquire distributed lock"));
        Assert.That(ex.Message, Does.Contain(resource));
    }

    /// <summary>
    /// AcquireLockAsync called concurrently for the same resource only allows one to succeed immediately
    /// </summary>
    [Test]
    public async Task AcquireLockAsync_ConcurrentLocks_OnlyOneSucceedsImmediately()
    {
        // Arrange
        var resource = "shared-resource";
        var expiry = TimeSpan.FromSeconds(30);
        var lockAcquired = false;
        var lockObject = new object();

        _mockDatabase
            .Setup(x => x.StringSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                expiry,
                When.NotExists))
            .ReturnsAsync(() =>
            {
                lock (lockObject)
                {
                    if (!lockAcquired)
                    {
                        lockAcquired = true;
                        return true;
                    }
                    return false;
                }
            });

        // Act
        var lock1Task = _lockService.AcquireLockAsync(resource, expiry);
        var lock2Task = _lockService.AcquireLockAsync(resource, expiry);

        // Wait for both to complete (one succeeds, one times out)
        var tasks = new[] { lock1Task, lock2Task };
        var completedTask = await Task.WhenAny(tasks);
    
        // Assert - one should succeed quickly
        Assert.That(completedTask.IsCompletedSuccessfully, Is.True);
        var successfulLock = await completedTask;
        Assert.That(successfulLock, Is.Not.Null);

        // The other should eventually timeout
        var remainingTask = tasks.First(t => t != completedTask);
        Assert.ThrowsAsync<TimeoutException>(async () => await remainingTask);
    }

    #endregion

    #region Constructor Tests

    /// <summary>
    /// Constructor with null Redis throws ArgumentNullException
    /// </summary>
    [Test]
    public void Constructor_WithNullRedis_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new RedisDistributedLockService(null!, _mockLogger.Object));
    }

    /// <summary>
    /// C
    /// </summary>
    [Test]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new RedisDistributedLockService(_mockRedis.Object, null!));
    }

    [Test]
    public void Constructor_WithNonPositiveMaxRetries_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RedisDistributedLockService(_mockRedis.Object, _mockLogger.Object, maxRetries: 0));
    }

    [Test]
    public void Constructor_WithNonPositiveBaseDelay_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RedisDistributedLockService(
                _mockRedis.Object,
                _mockLogger.Object,
                baseDelay: TimeSpan.Zero));
    }

    #endregion
}
