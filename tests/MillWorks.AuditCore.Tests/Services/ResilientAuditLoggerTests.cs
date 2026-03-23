using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Services;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Unit tests for ResilientAuditLogger
/// </summary>
[TestFixture]
public class ResilientAuditLoggerTests
{
    /// <summary>
    /// Mock for the inner audit logger
    /// </summary>
    private Mock<IAuditLogger> _mockInnerLogger;

    /// <summary>
    /// Mock for the dead letter queue
    /// </summary>
    private Mock<IAuditDeadLetterQueue> _mockDeadLetterQueue;

    /// <summary>
    /// Mock for the event factory
    /// </summary>
    private Mock<IAuditEventFactory> _mockEventFactory;

    /// <summary>
    /// Mock for the field redactor
    /// </summary>
    private Mock<IAuditFieldRedactor> _mockFieldRedactor;

    /// <summary>
    /// Mock for the logger
    /// </summary>
    private Mock<ILogger<ResilientAuditLogger>> _mockLogger;

    /// <summary>
    /// Resilient audit logger under test
    /// </summary>
    private ResilientAuditLogger _resilientLogger;

    /// <summary>
    /// Setup method to initialize mocks and the resilient logger
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockInnerLogger = new Mock<IAuditLogger>();
        _mockDeadLetterQueue = new Mock<IAuditDeadLetterQueue>();
        _mockEventFactory = new Mock<IAuditEventFactory>();
        _mockFieldRedactor = new Mock<IAuditFieldRedactor>();
        _mockLogger = new Mock<ILogger<ResilientAuditLogger>>();

        // Default pass-through behavior for redactor
        _mockFieldRedactor.Setup(r => r.RedactFields(It.IsAny<Dictionary<string, object?>>()))
            .Returns<Dictionary<string, object?>>(f => f);
        _mockFieldRedactor.Setup(r => r.RedactTarget(It.IsAny<AuditTarget?>()))
            .Returns<AuditTarget?>(t => t);

        _resilientLogger = new ResilientAuditLogger(
            _mockInnerLogger.Object,
            _mockDeadLetterQueue.Object,
            _mockEventFactory.Object,
            _mockFieldRedactor.Object,
            _mockLogger.Object);
    }

    /// <summary>
    /// LogAsync succeeds on first attempt and calls inner logger once
    /// </summary>
    [Test]
    public async Task LogAsync_SuccessfulFirstAttempt_CallsInnerLoggerOnce()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _resilientLogger.LogAsync(auditEvent);

        // Assert
        _mockInnerLogger.Verify(
            x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()),
            Times.Once);

        _mockDeadLetterQueue.Verify(
            static x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// LogAsync fails initially but succeeds on retry
    /// </summary>
    /// <exception cref="Exception"></exception>
    [Test]
    public async Task LogAsync_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        var callCount = 0;
        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount < 2)
                    throw new Exception("Transient error");
                return Task.CompletedTask;
            });

        // Act
        await _resilientLogger.LogAsync(auditEvent);

        // Assert
        Assert.That(callCount, Is.EqualTo(2));
        _mockInnerLogger.Verify(
            x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        _mockDeadLetterQueue.Verify(
            static x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// LogAsync fails all retries and sends the event to the dead letter queue
    /// </summary>
    [Test]
    public async Task LogAsync_AllRetriesFail_SendsToDeadLetterQueue()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        var exception = new Exception("Persistent error");
        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);

        _mockDeadLetterQueue
            .Setup(x => x.StoreFailedEventAsync(auditEvent, It.IsAny<Exception>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await _resilientLogger.LogAsync(auditEvent);

        // Assert
        _mockInnerLogger.Verify(
            x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()),
            Times.Exactly(4)); // 1 initial + 3 retries

        _mockDeadLetterQueue.Verify(
            x => x.StoreFailedEventAsync(auditEvent, It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Once);
    }

    /// <summary>
    /// LogAsync with string event type calls inner logger
    /// </summary>
    [Test]
    public async Task LogAsync_WithStringEventType_CallsInnerLogger()
    {
        // Arrange
        var eventType = "Test.Event";
        var data = new { UserId = 123 };
        var auditEvent = new AuditEvent { EventType = eventType };

        _mockEventFactory
            .Setup(x => x.CreateEvent(eventType, data, It.IsAny<string>()))
            .Returns(auditEvent);

        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _resilientLogger.LogAsync(eventType, data);

        // Assert
        _mockEventFactory.Verify(x => x.CreateEvent(eventType, data, It.IsAny<string>()), Times.Once);
        _mockInnerLogger.Verify(
            x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// LogAsync with message calls inner logger
    /// </summary>
    [Test]
    public async Task LogAsync_WithMessage_CallsInnerLogger()
    {
        // Arrange
        var eventType = "Test.Event";
        var message = "Test message";
        var data = new Dictionary<string, object?> { ["key"] = "value" };
        var auditEvent = new AuditEvent { EventType = eventType, CustomFields = new Dictionary<string, object?>() };

        _mockEventFactory
            .Setup(x => x.CreateEvent(eventType, data, It.IsAny<string>()))
            .Returns(auditEvent);

        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _resilientLogger.LogAsync(eventType, message, data, CancellationToken.None);

        // Assert
        _mockEventFactory.Verify(x => x.CreateEvent(eventType, data, It.IsAny<string>()), Times.Once);
        _mockInnerLogger.Verify(
            x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(auditEvent.CustomFields["Message"], Is.EqualTo(message));
    }

    /// <summary>
    /// BeginOperationAsync succeeds and returns operation ID
    /// </summary>
    [Test]
    public async Task BeginOperationAsync_Success_ReturnsOperationId()
    {
        // Arrange
        var operationType = "DataProcessing";
        var metadata = new { BatchSize = 100 };
        var expectedOperationId = Guid.NewGuid();

        _mockInnerLogger
            .Setup(x => x.BeginOperationAsync(operationType, metadata))
            .ReturnsAsync(expectedOperationId);

        // Act
        var operationId = await _resilientLogger.BeginOperationAsync(operationType, metadata);

        // Assert
        Assert.That(operationId, Is.EqualTo(expectedOperationId));
        _mockInnerLogger.Verify(
            x => x.BeginOperationAsync(operationType, metadata),
            Times.Once);
    }

    /// <summary>
    /// BeginOperationAsync fails and sends event to dead letter queue
    /// </summary>
    [Test]
    public async Task BeginOperationAsync_Failure_SendsToDLQAndReturnsId()
    {
        // Arrange
        var operationType = "DataProcessing";
        var exception = new Exception("Database error");

        _mockInnerLogger
            .Setup(x => x.BeginOperationAsync(operationType, null))
            .ThrowsAsync(exception);

        _mockDeadLetterQueue
            .Setup(static x =>
                x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        var operationId = await _resilientLogger.BeginOperationAsync(operationType);

        // Assert
        Assert.That(operationId, Is.Not.EqualTo(Guid.Empty));
        _mockDeadLetterQueue.Verify(
            x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), exception, "Failed to begin operation"),
            Times.Once);
    }

    /// <summary>
    /// EndOperationAsync succeeds and calls inner logger
    /// </summary>
    [Test]
    public async Task EndOperationAsync_Success_CallsInnerLogger()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        var result = new { Count = 100 };

        _mockInnerLogger
            .Setup(x => x.EndOperationAsync(operationId, true, result))
            .Returns(Task.CompletedTask);

        // Act
        await _resilientLogger.EndOperationAsync(operationId, true, result);

        // Assert
        _mockInnerLogger.Verify(
            x => x.EndOperationAsync(operationId, true, result),
            Times.Once);
    }

    /// <summary>
    /// EndOperationAsync fails and sends event to dead letter queue
    /// </summary>
    [Test]
    public async Task EndOperationAsync_Failure_SendsToDLQ()
    {
        // Arrange
        var operationId = Guid.NewGuid();
        var exception = new Exception("Database error");

        _mockInnerLogger
            .Setup(x => x.EndOperationAsync(operationId, true, null))
            .ThrowsAsync(exception);

        _mockDeadLetterQueue
            .Setup(static x =>
                x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await _resilientLogger.EndOperationAsync(operationId);

        // Assert
        _mockDeadLetterQueue.Verify(
            x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), exception, "Failed to end operation"),
            Times.Once);
    }

    /// <summary>
    /// CreateScope succeeds and returns wrapped scope
    /// </summary>
    [Test]
    public void CreateScope_Success_ReturnsWrappedScope()
    {
        // Arrange
        var eventType = "Test.Event";
        var target = new { Id = 123 };
        var mockInnerScope = new Mock<ICustomAuditScope>();

        _mockInnerLogger
            .Setup(x => x.CreateScope(eventType, target))
            .Returns(mockInnerScope.Object);

        // Act
        var scope = _resilientLogger.CreateScope(eventType, target);

        // Assert
        Assert.That(scope, Is.Not.Null);
        _mockInnerLogger.Verify(
            x => x.CreateScope(eventType, target),
            Times.Once);
    }

    /// <summary>
    /// LogAsync with cancellation requested propagates immediately
    /// </summary>
    [Test]
    public Task LogAsync_CancellationRequested_PropagatesImmediately()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _resilientLogger.LogAsync(auditEvent, cts.Token));

        // Should not call inner logger or send to DLQ when token is pre-cancelled
        _mockInnerLogger.Verify(
            static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _mockDeadLetterQueue.Verify(
            static x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never);
        return Task.CompletedTask;
    }

    /// <summary>
    /// LogAsync with cancellation during operation propagates and does not retry
    /// </summary>
    /// <returns></returns>
    [Test]
    public Task LogAsync_CancellationDuringOperation_PropagatesAndDoesNotRetry()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        using var cts = new CancellationTokenSource();

        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _resilientLogger.LogAsync(auditEvent, cts.Token));

        // Should call once but not retry or send to DLQ
        _mockInnerLogger.Verify(
            x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()),
            Times.Once);

        _mockDeadLetterQueue.Verify(
            static x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never);
        return Task.CompletedTask;
    }

    /// <summary>
    /// LogAsync with cancellation during retry stops retrying
    /// </summary>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    [Test]
    public Task LogAsync_CancellationDuringRetry_StopsRetrying()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        using var cts = new CancellationTokenSource();

        var callCount = 0;
        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // Fail first time
                    throw new Exception("Transient error");
                }

                // This shouldn't be reached because cancellation will happen during delay
                return Task.CompletedTask;
            });

        // Cancel after a short delay (during the retry delay)
        _ = Task.Run(async () =>
        {
            await Task.Delay(50, cts.Token);
            await cts.CancelAsync();
        }, cts.Token);

        // Act & Assert - Accept TaskCanceledException (subclass of OperationCanceledException)
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await _resilientLogger.LogAsync(auditEvent, cts.Token));

        // Should fail once, then get cancelled during retry delay
        Assert.That(callCount, Is.EqualTo(1));

        _mockDeadLetterQueue.Verify(
            static x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never);
        return Task.CompletedTask;
    }

    /// <summary>
    /// LogAsync with null event throws ArgumentNullException
    /// </summary>
    /// <returns></returns>
    [Test]
    public Task LogAsync_NullEvent_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _resilientLogger.LogAsync((AuditEvent)null!));
        return Task.CompletedTask;
    }

    /// <summary>
    /// CreateScope fails and returns dead letter scope
    /// </summary>
    [Test]
    public void CreateScope_Failure_ReturnsDeadLetterScope()
    {
        // Arrange
        var eventType = "Test.Event";
        var target = new { Id = 123 };

        _mockInnerLogger
            .Setup(x => x.CreateScope(eventType, target))
            .Throws(new Exception("Scope creation failed"));

        // Act
        var scope = _resilientLogger.CreateScope(eventType, target);

        // Assert
        Assert.That(scope, Is.Not.Null);
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Failed to create audit scope")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// LogAsync with cancellation requested propagates token
    /// </summary>
    /// <returns></returns>
    [Test]
    public Task LogAsync_CancellationRequested_PropagatesToken()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await _resilientLogger.LogAsync(auditEvent, cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>
    /// LogAsync fails and dead letter queue also fails, logs critical error
    /// </summary>
    [Test]
    public async Task LogAsync_DeadLetterQueueFails_LogsCritical()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Persistent error"));

        _mockDeadLetterQueue
            .Setup(x => x.StoreFailedEventAsync(auditEvent, It.IsAny<Exception>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("DLQ error"));

        // Act
        await _resilientLogger.LogAsync(auditEvent);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("CRITICAL")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// LogAsync encounters duplicate key error and treats it as success
    /// </summary>
    [Test]
    public async Task LogAsync_DuplicateKeyError_TreatsAsSuccess()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        var sqlException = CreateSqlException(2627); // Duplicate key error
        var dbUpdateException = new Microsoft.EntityFrameworkCore.DbUpdateException("Duplicate", sqlException);

        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .ThrowsAsync(dbUpdateException);

        // Act
        await _resilientLogger.LogAsync(auditEvent);

        // Assert
        _mockInnerLogger.Verify(
            x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()),
            Times.Once); // Should not retry

        _mockDeadLetterQueue.Verify(
            static x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never); // Should not send to DLQ

        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("already exists")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// LogAsync multiple events in parallel handles correctly
    /// </summary>
    [Test]
    public async Task LogAsync_MultipleEventsInParallel_HandlesCorrectly()
    {
        // Arrange
        var events = Enumerable.Range(1, 10)
            .Select(static i => new AuditEvent
            {
                EventId = Guid.NewGuid(),
                EventType = $"Test.Event{i}"
            })
            .ToList();

        _mockInnerLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var tasks = events.Select(e => _resilientLogger.LogAsync(e));
        await Task.WhenAll(tasks);

        // Assert
        _mockInnerLogger.Verify(
            static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Exactly(10));
    }

    /// <summary>
    /// LogAsync logs retry attempts
    /// </summary>
    /// <exception cref="Exception"></exception>
    [Test]
    public async Task LogAsync_LogsRetryAttempts()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        var callCount = 0;
        _mockInnerLogger
            .Setup(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount < 3)
                    throw new Exception("Transient error");
                return Task.CompletedTask;
            });

        // Act
        await _resilientLogger.LogAsync(auditEvent);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Retrying audit log")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Exactly(2)); // Two retry attempts

        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Successfully logged audit event")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Helper method to create SqlException via reflection since it has no public constructor
    /// </summary>
    private static Exception CreateSqlException(int errorNumber)
    {
        var sqlErrorType = typeof(Microsoft.Data.SqlClient.SqlException)
            .Assembly.GetType("Microsoft.Data.SqlClient.SqlError");

        var sqlErrorCtor = sqlErrorType!.GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            [
                typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string),
                typeof(string), typeof(int), typeof(int), typeof(Exception)
            ],
            null);

        var sqlError = sqlErrorCtor!.Invoke([
            errorNumber, (byte)0, (byte)0, "server", "error", "proc", 0, 0, null
        ]);

        var errorCollectionType = typeof(Microsoft.Data.SqlClient.SqlException)
            .Assembly.GetType("Microsoft.Data.SqlClient.SqlErrorCollection");

        var errorCollection = Activator.CreateInstance(
            errorCollectionType!,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, null, null);

        var addMethod = errorCollectionType!.GetMethod("Add",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        addMethod!.Invoke(errorCollection, [sqlError]);

        var sqlExceptionCtor = typeof(Microsoft.Data.SqlClient.SqlException).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            [typeof(string), errorCollectionType, typeof(Exception), typeof(Guid)],
            null);

        return (Exception)sqlExceptionCtor!.Invoke(["Duplicate key", errorCollection, null, Guid.Empty]);
    }
}
