using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// ISO 27001 Validator tests
/// </summary>
[TestFixture]
public class Iso27001ValidatorTests
{
    /// <summary>
    /// Validator instance
    /// </summary>
    private Iso27001Validator _validator;

    /// <summary>
    /// Setup
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _validator = new Iso27001Validator();
    }

    /// <summary>
    /// ValidateAsync_WithEventLogging_PassesEventLogging
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithEventLogging_PassesEventLogging()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var loggingResult = results.FirstOrDefault(static r => r.RuleName.Contains("Event Logging"));
        Assert.That(loggingResult, Is.Not.Null);
        Assert.That(loggingResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithIntegrityProtection_PassesLogProtection()
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithIntegrityProtection_PassesLogProtection()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "user",
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var protectionResult = results.FirstOrDefault(static r => r.RuleName.Contains("Log Protection"));
        Assert.That(protectionResult, Is.Not.Null);
        Assert.That(protectionResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithGoodRetention_PassesRetention()
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithSecurityEvents_PassesSecurityEventReporting()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Security.Incident",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "system"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var securityResult = results.FirstOrDefault(static r => r.RuleName.Contains("Security Event Reporting"));
        Assert.That(securityResult, Is.Not.Null);
        Assert.That(securityResult.Passed, Is.True);
    }
}