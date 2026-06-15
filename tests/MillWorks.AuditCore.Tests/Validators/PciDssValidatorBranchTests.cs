using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Validators.Interfaces;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// Tests for PciDssValidator covering untested requirement branches,
/// failed-path validation results, and GenerateRecommendations severity sections.
/// </summary>
[TestFixture]
[Category("Unit")]
public class PciDssValidatorBranchTests
{
    private PciDssValidator _validator;

    [SetUp]
    public void Setup()
    {
        _validator = new PciDssValidator();
    }

    private static AuditValidationResult? FindResult(List<AuditValidationResult> results, string ruleNamePart)
        => results.FirstOrDefault(r => r.RuleName.Contains(ruleNamePart, StringComparison.OrdinalIgnoreCase));

    #region Requirement 10.2 — Empty Events

    [Test]
    public async Task ValidateAsync_WithEmptyEvents_FailsAuditLogsImplementation()
    {
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents([]));

        var result = FindResult(results, "Audit Logs Implementation");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Critical));
        Assert.That(result.Recommendations, Is.Not.Empty);
    }

    [Test]
    public async Task ValidateAsync_WithEmptyEvents_FailsAuditLogInitialization()
    {
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents([]));

        var result = FindResult(results, "Audit Log Initialization");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Passed, Is.False);
    }

    #endregion

    #region Requirement 10.2.1 — CHD Access (false path)

    [Test]
    public async Task ValidateAsync_WithNonCardEvents_FailsChdAccessLogging()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Cardholder Data Access");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    [TestCase("CHD.Access")]
    [TestCase("PAN.View")]
    [TestCase("Payment.Process")]
    public async Task ValidateAsync_WithChdKeyword_PassesChdAccess(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Cardholder Data Access");
        Assert.That(result!.Passed, Is.True);
    }

    #endregion

    #region Requirement 10.2.2 — Privileged Users (false path)

    [Test]
    public async Task ValidateAsync_WithNonPrivilegedEvents_FailsPrivilegedUserActions()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "regularuser" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Privileged User Actions");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    [TestCase("Root.Command", "user1")]
    [TestCase("Sudo.Execute", "user1")]
    [TestCase("Privileged.Action", "user1")]
    [TestCase("Data.Read", "root")]
    public async Task ValidateAsync_WithPrivilegedKeyword_PassesPrivilegedActions(string eventType, string user)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = user }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Privileged User Actions");
        Assert.That(result!.Passed, Is.True);
    }

    #endregion

    #region Requirement 10.2.3 — Audit Trail Access

    [Test]
    public async Task ValidateAsync_WithAuditEvents_PassesAuditTrailAccess()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Audit.Query", InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Audit Trail Access");
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithLogEvents_PassesAuditTrailAccess()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Log.Export", InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        Assert.That(FindResult(results, "Audit Trail Access")!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithoutAuditOrLogEvents_FailsAuditTrailAccess()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Audit Trail Access");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    #endregion

    #region Requirement 10.2.4 — Invalid Access Attempts

    [TestCase("Login.Failed")]
    [TestCase("Access.Denied")]
    [TestCase("Request.Unauthorized")]
    [TestCase("Auth.Rejected")]
    public async Task ValidateAsync_WithFailedAccessKeyword_IncludesFailedAccessCount(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Invalid Access Attempts");
        Assert.That(result, Is.Not.Null);
        // Always passes (Passed = true unconditionally) but message differs
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Message, Does.Contain("1 events"));
    }

    [Test]
    public async Task ValidateAsync_WithNoFailedAccess_StillPasses()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Invalid Access Attempts");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Message, Does.Contain("Ensure"));
    }

    #endregion

    #region Requirement 10.2.5 — Authentication Changes

    [TestCase("Password.Changed")]
    [TestCase("Credential.Updated")]
    [TestCase("Authentication.Reset")]
    [TestCase("User.Created")]
    public async Task ValidateAsync_WithAuthChangeKeyword_PassesAuthChanges(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        Assert.That(FindResult(results, "Authentication Changes")!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithNoAuthChangeEvents_FailsAuthChanges()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Authentication Changes");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    #endregion

    #region Requirement 10.2.7 — System Object Changes

    [TestCase("Added")]
    [TestCase("Deleted")]
    public async Task ValidateAsync_WithActionCrud_PassesObjectChanges(string action)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Object.Change", Action = action, InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        Assert.That(FindResult(results, "System Object Changes")!.Passed, Is.True);
    }

    [TestCase("Entity.Create")]
    [TestCase("Record.Delete")]
    public async Task ValidateAsync_WithCreateDeleteEventType_PassesObjectChanges(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        Assert.That(FindResult(results, "System Object Changes")!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithNoObjectChanges_FailsObjectChanges()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", Action = "Modified", InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "System Object Changes");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    #endregion

    #region Requirement 10.3 — Detail Requirements (failure paths)

    [Test]
    public async Task ValidateAsync_WithMissingUser_FailsDetailRequirements()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow, User = null }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Audit Log Detail Requirements");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Critical));
        Assert.That(result.Message, Does.Contain("without user ID"));
    }

    [Test]
    public async Task ValidateAsync_WithMissingTimestamp_FailsDetailRequirements()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = null, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Audit Log Detail Requirements");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Message, Does.Contain("without timestamp"));
    }

    [Test]
    public async Task ValidateAsync_WithWhitespaceUser_FailsDetailRequirements()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow, User = "   " }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        Assert.That(FindResult(results, "Audit Log Detail Requirements")!.Passed, Is.False);
    }

    #endregion

    #region Requirement 10.3.4 — Time Synchronization

    [Test]
    public async Task ValidateAsync_WithRecentEvents_PassesTimeSynchronization()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-5), User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        Assert.That(FindResult(results, "Time Synchronization")!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithOnlyOldEvents_FailsTimeSynchronization()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddDays(-30), User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Time Synchronization");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    #endregion

    #region Requirement 10.4 — Log Protection (false path)

    [Test]
    public async Task ValidateAsync_WithNoIntegrityProtection_FailsLogProtection()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow, User = "user1", AuditIntegrity = null }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Audit Log Protection");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Critical));
    }

    #endregion

    #region Requirement 10.5 — Retention (middle path: 90-364 days)

    [Test]
    public async Task ValidateAsync_WithMediumRetention_FailsWithMediumSeverity()
    {
        // 200 days — meets immediate availability (90+) but not minimum retention (365)
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddDays(-200), User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Audit Log Retention");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
        Assert.That(result.Message, Does.Contain("3-month immediate availability"));
    }

    #endregion

    #region Requirement 10.6 — Regular Log Review

    [Test]
    public async Task ValidateAsync_WithRecentEvents_PassesRegularLogReview()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-10), User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        Assert.That(FindResult(results, "Regular Log Review")!.Passed, Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithOnlyOldEvents_FailsRegularLogReview()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddDays(-7), User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        Assert.That(FindResult(results, "Regular Log Review")!.Passed, Is.False);
    }

    #endregion

    #region Requirement 10.7 — Security Control Failure Detection

    [TestCase("Security.Alert")]
    [TestCase("System.Failure")]
    [TestCase("Service.Error")]
    public async Task ValidateAsync_WithAlertEvents_DetectsSecurityControlFailures(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "system" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Security Control Failure Detection");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Message, Does.Contain("1 events"));
    }

    [Test]
    public async Task ValidateAsync_WithNoAlertEvents_StillPassesWithWarningMessage()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "user1" }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        var result = FindResult(results, "Security Control Failure Detection");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Message, Does.Contain("Ensure"));
    }

    #endregion

    #region GenerateRecommendations — High and Medium Severity Sections

    [Test]
    public void GenerateRecommendations_WithHighSeverityFailures_IncludesHighSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "High Issue",
                Passed = false,
                Severity = ValidationSeverity.High,
                Message = "High severity issue",
                RegulationReference = "Req 10.2.2",
                Recommendations = ["Fix privileged user logging"]
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations.Any(static r => r.Contains("HIGH PRIORITY")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("High Issue")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("Fix privileged user logging")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithMediumSeverityFailures_IncludesMediumSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Medium Issue",
                Passed = false,
                Severity = ValidationSeverity.Medium,
                Message = "Medium severity issue"
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations.Any(static r => r.Contains("MEDIUM PRIORITY")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("Medium Issue")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithMixedSeverities_IncludesAllSections()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Critical Issue",
                Passed = false,
                Severity = ValidationSeverity.Critical,
                Message = "Critical failure",
                RegulationReference = "Req 10.2",
                Recommendations = ["Fix immediately"]
            },
            new()
            {
                RuleName = "High Issue",
                Passed = false,
                Severity = ValidationSeverity.High,
                Message = "High failure",
                RegulationReference = "Req 10.2.2",
                Recommendations = ["Address soon"]
            },
            new()
            {
                RuleName = "Medium Issue",
                Passed = false,
                Severity = ValidationSeverity.Medium,
                Message = "Medium failure"
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations.Any(static r => r.Contains("CRITICAL")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("HIGH PRIORITY")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("MEDIUM PRIORITY")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("PCI DSS Resources")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("Non-Compliance Consequences")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithFailureWithoutRecommendations_SkipsActionsSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Critical Issue",
                Passed = false,
                Severity = ValidationSeverity.Critical,
                Message = "Critical failure",
                RegulationReference = "Req 10.2",
                Recommendations = [] // Empty
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations.Any(static r => r.Contains("CRITICAL")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("REQUIRED ACTIONS")), Is.False);
    }

    #endregion
}
