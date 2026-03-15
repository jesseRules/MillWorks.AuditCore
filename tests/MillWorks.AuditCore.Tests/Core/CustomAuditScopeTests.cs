using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Core;

/// <summary>
/// CustomAuditScope tests
/// </summary>
[TestFixture]
public class CustomAuditScopeTests
{
    /// <summary>
    /// Mock audit logger
    /// </summary>
    private Mock<IAuditLogger> _mockAuditLogger;

    /// <summary>
    /// Mock logger for CustomAuditScope
    /// </summary>
    private Mock<ILogger> _mockLogger;

    /// <summary>
    /// Test audit event
    /// </summary>
    private AuditEvent _testEvent;

    /// <summary>
    /// Setup before each test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockAuditLogger = new Mock<IAuditLogger>();
        _mockLogger = new Mock<ILogger>();

        _testEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// SetCustomField adds field to event
    /// </summary>
    [Test]
    public void SetCustomField_AddsFieldToEvent()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        // Act
        scope.SetCustomField("TestField", "TestValue");

        // Assert
        Assert.That(_testEvent.CustomFields.ContainsKey("TestField"), Is.True);
        Assert.That(_testEvent.CustomFields["TestField"], Is.EqualTo("TestValue"));
    }

    /// <summary>
    /// SetTarget sets the event target
    /// </summary>
    [Test]
    public void SetTarget_SetsEventTarget()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);
        var targetObject = new { Id = 123, Name = "Test" };

        // Act
        scope.SetTarget(targetObject);

        // Assert
        Assert.That(_testEvent.Target, Is.Not.Null);
        Assert.That(_testEvent.Target.New, Is.EqualTo(targetObject));
    }

    /// <summary>
    /// SaveAsync logs the event
    /// </summary>
    [Test]
    public async Task SaveAsync_LogsEvent()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await scope.SaveAsync();

        // Assert
        Assert.That(_testEvent.EndDate, Is.Not.Null);
        _mockAuditLogger.Verify(
            x => x.LogAsync(_testEvent, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Sync Dispose is a no-op — does not attempt to save the event.
    /// All production callers use DisposeAsync.
    /// </summary>
    [Test]
    public void Dispose_DoesNotSaveEvent()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        // Act
        scope.Dispose();

        // Assert — sync Dispose should NOT call the logger
        _mockAuditLogger.Verify(
            x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// DisposeAsync saves the event
    /// </summary>
    [Test]
    public async Task DisposeAsync_SavesEvent()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await scope.DisposeAsync();

        // Assert
        _mockAuditLogger.Verify(
            x => x.LogAsync(_testEvent, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// SaveAsync multiple calls only logs once
    /// </summary>
    [Test]
    public async Task SaveAsync_MultipleCallsOnlyLogsOnce()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await scope.SaveAsync();
        await scope.SaveAsync();
        await scope.SaveAsync();

        // Assert - Should only log once
        _mockAuditLogger.Verify(
            x => x.LogAsync(_testEvent, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// SaveAsync sets the end date
    /// </summary>
    [Test]
    public async Task SaveAsync_SetsEndDate()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var startTime = DateTimeOffset.UtcNow;

        // Act
        await scope.SaveAsync();

        // Assert
        Assert.That(_testEvent.EndDate, Is.Not.Null);
        Assert.That(_testEvent.EndDate.Value, Is.GreaterThanOrEqualTo(startTime));
    }

    /// <summary>
    /// SetCustomField can overwrite existing field
    /// </summary>
    [Test]
    public void SetCustomField_CanOverwriteExistingField()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        // Act
        scope.SetCustomField("TestField", "FirstValue");
        scope.SetCustomField("TestField", "SecondValue");

        // Assert
        Assert.That(_testEvent.CustomFields["TestField"], Is.EqualTo("SecondValue"));
    }

    /// <summary>
    /// SetTarget can overwrite existing target
    /// </summary>
    [Test]
    public void SetTarget_CanOverwriteExistingTarget()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);
        var firstTarget = new { Id = 1, Name = "First" };
        var secondTarget = new { Id = 2, Name = "Second" };

        // Act
        scope.SetTarget(firstTarget);
        scope.SetTarget(secondTarget);

        // Assert
        Assert.That(_testEvent.Target!.New, Is.EqualTo(secondTarget));
    }

    /// <summary>
    /// Dispose after SaveAsync does not log again
    /// </summary>
    [Test]
    public async Task Dispose_AfterSaveAsync_DoesNotLogAgain()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await scope.SaveAsync();
        await scope.DisposeAsync();

        // Assert - Should only log once from SaveAsync
        _mockAuditLogger.Verify(
            x => x.LogAsync(_testEvent, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// DisposeAsync after SaveAsync does not log again
    /// </summary>
    [Test]
    public async Task DisposeAsync_AfterSaveAsync_DoesNotLogAgain()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await scope.SaveAsync();
        await scope.DisposeAsync();

        // Assert - Should only log once from SaveAsync
        _mockAuditLogger.Verify(
            x => x.LogAsync(_testEvent, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Constructor initializes with event
    /// </summary>
    [Test]
    public void Constructor_InitializesWithEvent()
    {
        // Act
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        // Assert
        Assert.That(scope, Is.Not.Null);
        Assert.That(_testEvent.EventId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(_testEvent.StartDate, Is.Not.EqualTo(default(DateTimeOffset)));
    }

    /// <summary>
    /// SaveAsync_WhenLoggerThrowsException_RethrowsException
    /// </summary>
    /// <returns></returns>
    [Test]
    public Task SaveAsync_WhenLoggerThrowsException_RethrowsException()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Logging failed"));

        // Act & Assert
        var ex = Assert.ThrowsAsync<Exception>(async () => await scope.SaveAsync());
        Assert.That(ex.Message, Is.EqualTo("Logging failed"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// SetCustomField_WithNullValue_AddsNullToCustomFields
    /// </summary>
    [Test]
    public void SetCustomField_WithNullValue_AddsNullToCustomFields()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        // Act
        scope.SetCustomField<object>("NullField", null!);

        // Assert
        Assert.That(_testEvent.CustomFields.ContainsKey("NullField"), Is.True);
        Assert.That(_testEvent.CustomFields["NullField"], Is.Null);
    }

    /// <summary>
    /// SetTarget_WithNullValue_SetsTargetToNull
    /// </summary>
    [Test]
    public void SetTarget_WithNullValue_SetsTargetToNull()
    {
        // Arrange
        var scope = new CustomAuditScope(_testEvent, _mockAuditLogger.Object, _mockLogger.Object);

        // First set a target
        scope.SetTarget(new { Id = 1 });

        // Act - Set to null
        scope.SetTarget(null!);

        // Assert
        Assert.That(_testEvent.Target, Is.Not.Null); // Target object exists
        Assert.That(_testEvent.Target.New, Is.Null); // But New property is null
    }
}