using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Redis;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Tests.Services.Redis;

/// <summary>
/// Unit tests for RedisJobQueue
/// </summary>
[TestFixture]
public class RedisJobQueueTests
{
    /// <summary>
    /// Mock Redis connection multiplexer
    /// </summary>
    private Mock<IConnectionMultiplexer> _mockRedis;

    /// <summary>
    /// Mock database for RedisJobQueue
    /// </summary>
    private Mock<IDatabase> _mockDatabase;

    /// <summary>
    /// Mock server for RedisJobQueue
    /// </summary>
    private Mock<IServer> _mockServer;

    /// <summary>
    /// Mock logger for RedisJobQueue
    /// </summary>
    private Mock<ILogger<RedisJobQueue>> _mockLogger;

    /// <summary>
    /// Options for JobQueueConfiguration
    /// </summary>
    private IOptions<JobQueueConfiguration> _options;

    /// <summary>
    /// Job queue instance under test
    /// </summary>
    private RedisJobQueue _jobQueue;

    /// <summary>
    /// Setup method to initialize mocks and the RedisJobQueue instance
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();
        _mockServer = new Mock<IServer>();
        _mockLogger = new Mock<ILogger<RedisJobQueue>>();

        var config = new JobQueueConfiguration
        {
            JobTimeout = TimeSpan.FromMinutes(5),
            MaxRetries = 3,
            RetryDelaySeconds = 30
        };
        _options = Options.Create(config);

        _mockRedis
            .Setup(static x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        _jobQueue = new RedisJobQueue(
            _mockRedis.Object,
            _options,
            _mockLogger.Object);
    }

    /// <summary>
    /// Tear down method to dispose the job queue instance
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _jobQueue.Dispose();
    }

    #region EnqueueAsync Tests

    /// <summary>
    /// EnqueueAsync_WithValidParameters_EnqueuesJobSuccessfully
    /// </summary>
    [Test]
    public async Task EnqueueAsync_WithValidParameters_EnqueuesJobSuccessfully()
    {
        // Arrange
        var queueName = "audit-processing";
        var jobType = "AuditEvent.Process";
        var payload = JsonSerializer.Serialize(new { EventId = Guid.NewGuid() });
        var priority = 5;

        _mockDatabase
            .Setup(static x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                CommandFlags.None))
            .ReturnsAsync(true);

        // Act
        var jobId = await _jobQueue.EnqueueAsync(queueName, jobType, payload, priority);

        // Assert
        Assert.That(jobId, Is.Not.Null);
        Assert.That(Guid.TryParse(jobId, out _), Is.True);

        _mockDatabase.Verify(x => x.SortedSetAddAsync(
            It.Is<RedisKey>(k => k.ToString().Contains(queueName)),
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            CommandFlags.None), Times.Once);
    }

    /// <summary>
    /// EnqueueAsync_WithHighPriority_UsesCorrectScore
    /// </summary>
    [Test]
    public async Task EnqueueAsync_WithHighPriority_UsesCorrectScore()
    {
        // Arrange
        var queueName = "priority-queue";
        var jobType = "HighPriority.Job";
        var payload = "{}";
        var priority = 10;
        double? capturedScore = null;

        _mockDatabase
            .Setup(static x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                CommandFlags.None))
            .Callback<RedisKey, RedisValue, double, SortedSetWhen, CommandFlags>((_, _, s, _, _) => capturedScore = s)
            .ReturnsAsync(true);

        // Act
        await _jobQueue.EnqueueAsync(queueName, jobType, payload, priority);

        // Assert
        Assert.That(capturedScore, Is.Not.Null);
        // Higher priority results in lower score (timestamp - priority * 1000)
        // Score should be positive and less than the current timestamp
        var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.That(capturedScore.Value, Is.LessThan(currentTimestamp));
        Assert.That(capturedScore.Value, Is.GreaterThan(0));
    }

    /// <summary>
    /// EnqueueAsync_WithLowPriority_UsesCorrectScore
    /// </summary>
    [Test]
    public async Task EnqueueAsync_WithLowPriority_UsesCorrectScore()
    {
        // Arrange
        var queueName = "low-priority-queue";
        var jobType = "LowPriority.Job";
        var payload = "{}";
        var priority = 0;
        double? capturedScore = null;

        _mockDatabase
            .Setup(static x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                CommandFlags.None))
            .Callback<RedisKey, RedisValue, double, SortedSetWhen, CommandFlags>((_, _, s, _, _) => capturedScore = s)
            .ReturnsAsync(true);

        // Act
        await _jobQueue.EnqueueAsync(queueName, jobType, payload, priority);

        // Assert
        Assert.That(capturedScore, Is.Not.Null);
    }

    /// <summary>
    /// EnqueueAsync_CreatesJobWithCorrectProperties
    /// </summary>
    [Test]
    public async Task EnqueueAsync_CreatesJobWithCorrectProperties()
    {
        // Arrange
        var queueName = "test-queue";
        var jobType = "TestJob";
        var payload = "{\"data\":\"test\"}";
        var priority = 1;
        RedisValue? capturedJobJson = null;

        _mockDatabase
            .Setup(static x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                CommandFlags.None))
            .Callback<RedisKey, RedisValue, double, SortedSetWhen, CommandFlags>((_, v, _, _, _) => capturedJobJson = v)
            .ReturnsAsync(true);

        // Act
        var jobId = await _jobQueue.EnqueueAsync(queueName, jobType, payload, priority);

        // Assert
        Assert.That(capturedJobJson, Is.Not.Null);
        var job = JsonSerializer.Deserialize<RedisJob>(capturedJobJson?.ToString() ?? string.Empty);
        Assert.That(job, Is.Not.Null);
        Assert.That(job?.Id, Is.EqualTo(jobId));
        Assert.That(job.QueueName, Is.EqualTo(queueName));
        Assert.That(job.JobType, Is.EqualTo(jobType));
        Assert.That(job.Payload, Is.EqualTo(payload));
        Assert.That(job.Priority, Is.EqualTo(priority));
        Assert.That(job.MaxRetries, Is.EqualTo(3));
        Assert.That(job.RetryCount, Is.EqualTo(0));
    }

    #endregion

    #region DequeueAsync Tests

    /// <summary>
    /// DequeueAsync_WithAvailableJob_ReturnsJob
    /// </summary>
    [Test]
    public async Task DequeueAsync_WithAvailableJob_ReturnsJob()
    {
        // Arrange
        var queueName = "test-queue";
        var workerId = "worker-1";
        var job = new RedisJob
        {
            Id = Guid.NewGuid().ToString(),
            QueueName = queueName,
            JobType = "TestJob",
            Payload = "{}",
            Priority = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxRetries = 3,
            RetryCount = 0
        };
        var jobJson = JsonSerializer.Serialize(job);

        _mockDatabase
            .Setup(static x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                CommandFlags.None))
            .ReturnsAsync(RedisResult.Create((RedisValue)jobJson));

        // Act
        var result = await _jobQueue.DequeueAsync(queueName, workerId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result?.Id, Is.EqualTo(job.Id));
        Assert.That(result.QueueName, Is.EqualTo(queueName));
        Assert.That(result.AssignedTo, Is.EqualTo(workerId));
        Assert.That(result.AssignedAt, Is.Not.Null);
    }

    /// <summary>
    /// DequeueAsync_WithEmptyQueue_ReturnsNull
    /// </summary>
    [Test]
    public async Task DequeueAsync_WithEmptyQueue_ReturnsNull()
    {
        // Arrange
        var queueName = "empty-queue";
        var workerId = "worker-1";

        _mockDatabase
            .Setup(static x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                CommandFlags.None))
            .ReturnsAsync(RedisResult.Create(RedisValue.Null));

        // Act
        var result = await _jobQueue.DequeueAsync(queueName, workerId);

        // Assert
        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// DequeueAsync_WithEmptyQueue_ReturnsNull
    /// </summary>
    [Test]
    public async Task DequeueAsync_WithMalformedJob_ReturnsNull()
    {
        // Arrange
        var queueName = "test-queue";
        var workerId = "worker-1";

        _mockDatabase
            .Setup(static x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                CommandFlags.None))
            .ReturnsAsync(RedisResult.Create((RedisValue)"invalid json"));

        // Act
        var result = await _jobQueue.DequeueAsync(queueName, workerId);

        // Assert
        Assert.That(result, Is.Null);
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Failed to deserialize")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// DequeueAsync_UsesCorrectLuaScript
    /// </summary>
    [Test]
    public async Task DequeueAsync_UsesCorrectLuaScript()
    {
        // Arrange
        var queueName = "test-queue";
        var workerId = "worker-1";
        string? capturedScript = null;

        _mockDatabase
            .Setup(static x => x.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                CommandFlags.None))
            .Callback<string, RedisKey[], RedisValue[], CommandFlags>((s, _, _, _) => capturedScript = s)
            .ReturnsAsync(RedisResult.Create(RedisValue.Null));

        // Act
        await _jobQueue.DequeueAsync(queueName, workerId);

        // Assert
        Assert.That(capturedScript, Is.Not.Null);
        Assert.That(capturedScript, Does.Contain("zpopmin"));
        Assert.That(capturedScript, Does.Contain("hset"));
        Assert.That(capturedScript, Does.Contain("expire"));
    }

    #endregion

    #region CompleteAsync Tests

    /// <summary>
    /// CompleteAsync_WithSuccessfulJob_RemovesFromProcessing
    /// </summary>
    [Test]
    public async Task CompleteAsync_WithSuccessfulJob_RemovesFromProcessing()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var workerId = "worker-1";

        _mockDatabase
            .Setup(static x => x.HashDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                CommandFlags.None))
            .ReturnsAsync(true);

        // Act
        await _jobQueue.CompleteAsync(jobId, workerId, true);

        // Assert
        _mockDatabase.Verify(x => x.HashDeleteAsync(
            It.Is<RedisKey>(k => k.ToString().Contains($"processing:{workerId}")),
            It.Is<RedisValue>(v => v == jobId),
            CommandFlags.None), Times.Once);
    }

    /// <summary>
    /// CompleteAsync_WithFailedJob_LogsWarning
    /// </summary>
    [Test]
    public async Task CompleteAsync_WithFailedJob_LogsWarning()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var workerId = "worker-1";
        var error = "Processing failed";

        _mockDatabase
            .Setup(static x => x.HashDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                CommandFlags.None))
            .ReturnsAsync(true);

        // Act
        await _jobQueue.CompleteAsync(jobId, workerId, false, error);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("failed") && v.ToString()!.Contains(error)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// CompleteAsync_WithFailedJob_LogsWarning
    /// </summary>
    [Test]
    public async Task CompleteAsync_WithSuccessfulJob_LogsDebug()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var workerId = "worker-1";

        _mockDatabase
            .Setup(static x => x.HashDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                CommandFlags.None))
            .ReturnsAsync(true);

        // Act
        await _jobQueue.CompleteAsync(jobId, workerId, true);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("completed successfully")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region RecoverStuckJobsAsync Tests

    /// <summary>
    /// RecoverStuckJobsAsync_WithExpiredProcessingKey_RecoversJobs
    /// </summary>
    [Test]
    public async Task RecoverStuckJobsAsync_WithExpiredProcessingKey_RecoversJobs()
    {
        // Arrange
        var job = new RedisJob
        {
            Id = Guid.NewGuid().ToString(),
            QueueName = "test-queue",
            JobType = "TestJob",
            Payload = "{}",
            Priority = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxRetries = 3,
            RetryCount = 0
        };

        var endpoint = new Mock<EndPoint>();
        _mockRedis
            .Setup(static x => x.GetEndPoints(It.IsAny<bool>()))
            .Returns([endpoint.Object]);

        _mockRedis
            .Setup(static x => x.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>()))
            .Returns(_mockServer.Object);

        var processingKey = "jobs:processing:worker-1";
        _mockServer
            .Setup(static x => x.KeysAsync(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(ToAsyncEnumerable(new RedisKey(processingKey)));

        _mockDatabase
            .Setup(static x => x.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync((TimeSpan?)null); // Key expired

        _mockDatabase
            .Setup(static x => x.HashGetAllAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync([new HashEntry("job1", JsonSerializer.Serialize(job))]);

        _mockDatabase
            .Setup(static x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                CommandFlags.None))
            .ReturnsAsync(true);

        _mockDatabase
            .Setup(static x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync(true);

        // Act
        await _jobQueue.RecoverStuckJobsAsync();

        // Assert
        _mockDatabase.Verify(x => x.SortedSetAddAsync(
            It.Is<RedisKey>(k => k.ToString().Contains($"queue:{job.QueueName}")),
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            CommandFlags.None), Times.Once);

        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Recovered stuck job")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// RecoverStuckJobsAsync_WithMaxRetriesExceeded_MovesToDeadLetter
    /// </summary>
    [Test]
    public async Task RecoverStuckJobsAsync_WithMaxRetriesExceeded_MovesToDeadLetter()
    {
        // Arrange
        var job = new RedisJob
        {
            Id = Guid.NewGuid().ToString(),
            QueueName = "test-queue",
            JobType = "TestJob",
            Payload = "{}",
            Priority = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxRetries = 3,
            RetryCount = 3 // Already at max
        };

        var endpoint = new Mock<EndPoint>();
        _mockRedis
            .Setup(static x => x.GetEndPoints(It.IsAny<bool>()))
            .Returns([endpoint.Object]);

        _mockRedis
            .Setup(static x => x.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>()))
            .Returns(_mockServer.Object);

        var processingKey = "jobs:processing:worker-1";
        _mockServer
            .Setup(static x => x.KeysAsync(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(ToAsyncEnumerable(new RedisKey(processingKey)));

        _mockDatabase
            .Setup(static x => x.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync((TimeSpan?)null);

        _mockDatabase
            .Setup(static x => x.HashGetAllAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync([new HashEntry("job1", JsonSerializer.Serialize(job))]);

        _mockDatabase
            .Setup(static x => x.ListRightPushAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                CommandFlags.None))
            .ReturnsAsync(1);

        _mockDatabase
            .Setup(static x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync(true);

        // Act
        await _jobQueue.RecoverStuckJobsAsync();

        // Assert
        _mockDatabase.Verify(static x => x.ListRightPushAsync(
            It.Is<RedisKey>(static k => k.ToString().Contains("deadletter")),
            It.IsAny<RedisValue>(),
            It.IsAny<When>(),
            CommandFlags.None), Times.Once);

        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("dead letter")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// RecoverStuckJobsAsync_WithValidKey_SkipsRecovery
    /// </summary>
    [Test]
    public async Task RecoverStuckJobsAsync_WithValidKey_SkipsRecovery()
    {
        // Arrange
        var endpoint = new Mock<EndPoint>();
        _mockRedis
            .Setup(static x => x.GetEndPoints(It.IsAny<bool>()))
            .Returns([endpoint.Object]);

        _mockRedis
            .Setup(static x => x.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>()))
            .Returns(_mockServer.Object);

        var processingKey = "jobs:processing:worker-1";
        _mockServer
            .Setup(static x => x.KeysAsync(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(ToAsyncEnumerable(new RedisKey(processingKey)));

        _mockDatabase
            .Setup(static x => x.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync(TimeSpan.FromMinutes(5)); // Still valid

        // Act
        await _jobQueue.RecoverStuckJobsAsync();

        // Assert
        _mockDatabase.Verify(static x => x.HashGetAllAsync(
            It.IsAny<RedisKey>(),
            CommandFlags.None), Times.Never);
    }

    /// <summary>
    /// RecoverStuckJobsAsync_WithDeserializationError_LogsError
    /// </summary>
    [Test]
    public async Task RecoverStuckJobsAsync_WithDeserializationError_LogsError()
    {
        // Arrange
        var endpoint = new Mock<EndPoint>();
        _mockRedis
            .Setup(static x => x.GetEndPoints(It.IsAny<bool>()))
            .Returns([endpoint.Object]);

        _mockRedis
            .Setup(static x => x.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>()))
            .Returns(_mockServer.Object);

        var processingKey = "jobs:processing:worker-1";
        _mockServer
            .Setup(static x => x.KeysAsync(
                It.IsAny<int>(),
                It.IsAny<RedisValue>(),
                It.IsAny<int>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CommandFlags>()))
            .Returns(ToAsyncEnumerable(new RedisKey(processingKey)));

        _mockDatabase
            .Setup(static x => x.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync((TimeSpan?)null);

        _mockDatabase
            .Setup(static x => x.HashGetAllAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync([new HashEntry("job1", "invalid json")]);

        _mockDatabase
            .Setup(static x => x.KeyDeleteAsync(It.IsAny<RedisKey>(), CommandFlags.None))
            .ReturnsAsync(true);

        // Act
        await _jobQueue.RecoverStuckJobsAsync();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Failed to recover")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region Dispose Tests

    /// <summary>
    /// Dispose_CanBeCalledMultipleTimes
    /// </summary>
    [Test]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange & Act
        _jobQueue.Dispose();
        _jobQueue.Dispose();

        // Assert - No exception thrown
        Assert.Pass();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Helper method to create an async enumerable from items
    /// </summary>
    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    #endregion
}