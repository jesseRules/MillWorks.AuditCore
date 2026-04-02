using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// Tests for Iso27001Validator covering all untested requirement branches,
/// false-path validation results, and GenerateRecommendations severity sections.
/// </summary>
[TestFixture]
[Category("Unit")]
public class Iso27001ValidatorBranchTests
{
    private Iso27001Validator _validator;

    [SetUp]
    public void Setup()
    {
        _validator = new Iso27001Validator();
    }

    private static AuditValidationResult? Find(List<AuditValidationResult> results, string ruleNamePart)
        => results.FirstOrDefault(r => r.RuleName.Contains(ruleNamePart, StringComparison.OrdinalIgnoreCase));

    #region A.12.4.1 — Event Logging (false path)

    [Test]
    public async Task ValidateAsync_WithEmptyEvents_FailsEventLogging()
    {
        var results = await _validator.ValidateAsync([]);

        var result = Find(results, "Event Logging");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Critical));
    }

    #endregion

    #region A.12.4.2 — Log Protection (false path)

    [Test]
    public async Task ValidateAsync_WithNoIntegrityProtection_FailsLogProtection()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow, User = "user", AuditIntegrity = null }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Log Protection");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
        Assert.That(result.Recommendations, Is.Not.Empty);
    }

    #endregion

    #region A.16.1.2 — Security Event Reporting (false path + keyword branches)

    [Test]
    public async Task ValidateAsync_WithNoSecurityEvents_FailsSecurityEventReporting()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Security Event Reporting");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
        Assert.That(result.Recommendations, Is.Not.Empty);
    }

    [TestCase("Security.Alert")]
    [TestCase("Incident.Report")]
    [TestCase("Data.Breach")]
    public async Task ValidateAsync_WithSecurityKeyword_PassesSecurityEventReporting(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);
        Assert.That(Find(results, "Security Event Reporting")!.Passed, Is.True);
    }

    #endregion

    #region A.9.2.1 — User Access Management (entirely untested)

    [TestCase("User.Created")]
    [TestCase("Login.Success")]
    [TestCase("Access.Granted")]
    public async Task ValidateAsync_WithUserAccessKeyword_PassesUserAccessManagement(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);
        Assert.That(Find(results, "User Access Management")!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithNoUserAccessEvents_FailsUserAccessManagement()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Export", InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "User Access Management");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    #endregion

    #region A.12.4.3 — Administrator Activity Logging (entirely untested)

    [Test]
    public async Task ValidateAsync_WithAdminEventType_PassesAdminLogging()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Admin.Config", InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(events);
        Assert.That(Find(results, "Administrator Activity")!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithAdminUser_PassesAdminLogging()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "admin" }
        };

        var results = await _validator.ValidateAsync(events);
        Assert.That(Find(results, "Administrator Activity")!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithNoAdminActivity_FailsAdminLogging()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "regularuser" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Administrator Activity");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    #endregion

    #region A.12.4.4 — Clock Synchronization (entirely untested)

    [Test]
    public async Task ValidateAsync_WithAllTimestamps_PassesClockSync()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Test2", InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);
        Assert.That(Find(results, "Clock Synchronization")!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithMissingTimestamp_FailsClockSync()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Test2", InsertedDate = null, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Clock Synchronization");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
        Assert.That(result.FailedCount, Is.EqualTo(1));
    }

    #endregion

    #region Log Retention (entirely untested)

    [Test]
    public async Task ValidateAsync_WithGoodRetention_PassesRetention()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddDays(-120), User = "user" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Log Retention");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Info));
    }

    [Test]
    public async Task ValidateAsync_WithShortRetention_FailsRetention()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddDays(-30), User = "user" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Log Retention");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Low));
        Assert.That(result.Recommendations, Is.Not.Empty);
    }

    [Test]
    public async Task ValidateAsync_WithEmptyEvents_RetentionIsZeroDays()
    {
        var results = await _validator.ValidateAsync([]);

        var result = Find(results, "Log Retention");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Message, Does.Contain("0 days"));
    }

    #endregion

    #region GenerateRecommendations (entirely untested)

    [Test]
    public void GenerateRecommendations_WithAllPassing_ReturnsSuccessMessage()
    {
        var results = new List<AuditValidationResult>
        {
            new() { RuleName = "Test", Passed = true, Message = "OK" }
        };

        var recs = _validator.GenerateRecommendations(results);

        Assert.That(recs, Has.Count.EqualTo(1));
        Assert.That(recs[0], Does.Contain("All validation checks passed"));
    }

    [Test]
    public void GenerateRecommendations_WithCriticalFailure_IncludesCriticalSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Event Logging", Passed = false,
                Severity = ValidationSeverity.Critical,
                Message = "No events", Recommendations = ["Enable logging"]
            }
        };

        var recs = _validator.GenerateRecommendations(results);

        Assert.That(recs.Any(static r => r.Contains("CRITICAL")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("Event Logging")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("Enable logging")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithHighFailure_IncludesHighSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Log Protection", Passed = false,
                Severity = ValidationSeverity.High,
                Message = "No integrity", Recommendations = ["Enable tamper detection"]
            }
        };

        var recs = _validator.GenerateRecommendations(results);

        Assert.That(recs.Any(static r => r.Contains("HIGH PRIORITY")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("Log Protection")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithMediumFailure_IncludesMediumSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Clock Sync", Passed = false,
                Severity = ValidationSeverity.Medium,
                Message = "Missing timestamps", Recommendations = ["Fix clocks"]
            }
        };

        var recs = _validator.GenerateRecommendations(results);

        Assert.That(recs.Any(static r => r.Contains("MEDIUM PRIORITY")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("Clock Sync")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithLowFailure_IncludesLowSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Retention", Passed = false,
                Severity = ValidationSeverity.Low,
                Message = "Short retention"
            }
        };

        var recs = _validator.GenerateRecommendations(results);

        Assert.That(recs.Any(static r => r.Contains("LOW PRIORITY")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("Retention")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithAllSeverities_IncludesAllSections()
    {
        var results = new List<AuditValidationResult>
        {
            new() { RuleName = "C", Passed = false, Severity = ValidationSeverity.Critical, Message = "c", Recommendations = ["fix c"] },
            new() { RuleName = "H", Passed = false, Severity = ValidationSeverity.High, Message = "h", Recommendations = ["fix h"] },
            new() { RuleName = "M", Passed = false, Severity = ValidationSeverity.Medium, Message = "m", Recommendations = ["fix m"] },
            new() { RuleName = "L", Passed = false, Severity = ValidationSeverity.Low, Message = "l" }
        };

        var recs = _validator.GenerateRecommendations(results);

        Assert.That(recs.Any(static r => r.Contains("CRITICAL")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("HIGH PRIORITY")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("MEDIUM PRIORITY")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("LOW PRIORITY")), Is.True);
    }

    #endregion
}
