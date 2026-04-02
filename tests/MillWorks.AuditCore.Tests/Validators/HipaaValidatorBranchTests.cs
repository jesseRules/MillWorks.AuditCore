using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// Tests for HipaaValidator covering the ~7% uncovered branches: false paths for
/// activity review, automatic logoff, login monitoring, security incidents, authorization
/// tracking, emergency access, retention edge cases, and GenerateRecommendations severity sections.
/// </summary>
[TestFixture]
[Category("Unit")]
public class HipaaValidatorBranchTests
{
    private HipaaValidator _validator;

    [SetUp]
    public void Setup()
    {
        _validator = new HipaaValidator();
    }

    private static AuditValidationResult? Find(List<AuditValidationResult> results, string ruleNamePart)
        => results.FirstOrDefault(r => r.RuleName.Contains(ruleNamePart, StringComparison.OrdinalIgnoreCase));

    #region §164.308(a)(1)(ii)(D) — Information System Activity Review (false path)

    [Test]
    public async Task ValidateAsync_WithOnlyOldEvents_FailsActivityReview()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddDays(-60), User = "user" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Information System Activity Review");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
        Assert.That(result.Recommendations, Is.Not.Empty);
    }

    #endregion

    #region §164.312(a)(2)(iii) — Automatic Logoff (false path)

    [Test]
    public async Task ValidateAsync_WithNoLogoffEvents_FailsAutomaticLogoff()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Automatic Logoff");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
        Assert.That(result.Recommendations, Is.Not.Empty);
    }

    [TestCase("Session.Logoff")]
    [TestCase("User.Logout")]
    [TestCase("Session.SessionEnd")]
    [TestCase("Session.Timeout")]
    public async Task ValidateAsync_WithLogoffKeyword_PassesAutomaticLogoff(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);
        Assert.That(Find(results, "Automatic Logoff")!.Passed, Is.True);
    }

    #endregion

    #region §164.308(a)(5)(ii)(C) — Login Monitoring (false path + failed login count)

    [Test]
    public async Task ValidateAsync_WithNoLoginEvents_FailsLoginMonitoring()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Log-in Monitoring");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    [Test]
    public async Task ValidateAsync_WithFailedLoginEvents_ReportsFailedCount()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Login.Success", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Login.Failed", InsertedDate = DateTimeOffset.UtcNow, User = "attacker" },
            new() { EventType = "Login.Denied", InsertedDate = DateTimeOffset.UtcNow, User = "attacker" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Log-in Monitoring");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Message, Does.Contain("3 login events"));
        Assert.That(result.Message, Does.Contain("2 failed"));
    }

    #endregion

    #region §164.308(a)(6)(ii) — Security Incident Tracking (message branch)

    [TestCase("Security.Alert")]
    [TestCase("Incident.Report")]
    [TestCase("Data.Breach")]
    [TestCase("Policy.Violation")]
    public async Task ValidateAsync_WithSecurityIncidentKeyword_ReportsIncidentCount(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "system" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Security Incident Tracking");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Message, Does.Contain("1 events"));
    }

    #endregion

    #region §164.316(b)(1)(i) — Documentation Retention (zero retention path)

    [Test]
    public async Task ValidateAsync_WithEmptyEvents_ReportsNoRetentionPolicy()
    {
        var results = await _validator.ValidateAsync([]);

        var result = Find(results, "Documentation Retention");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Message, Does.Contain("No retention policy"));
    }

    #endregion

    #region §164.308(a)(3)(ii)(A) — Authorization Tracking (false path)

    [Test]
    public async Task ValidateAsync_WithNoAccessChangeEvents_FailsAuthorizationTracking()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Data.Read", InsertedDate = DateTimeOffset.UtcNow, User = "user" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Authorization Tracking");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
        Assert.That(result.Recommendations, Is.Not.Empty);
    }

    [TestCase("Authorization.Changed")]
    [TestCase("Permission.Grant")]
    [TestCase("Role.Assigned")]
    [TestCase("Access.Modified")]
    public async Task ValidateAsync_WithAuthorizationKeyword_PassesAuthorizationTracking(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "admin" }
        };

        var results = await _validator.ValidateAsync(events);
        Assert.That(Find(results, "Authorization Tracking")!.Passed, Is.True);
    }

    #endregion

    #region §164.312(a)(2)(ii) — Emergency Access (true message path)

    [TestCase("Emergency.Access")]
    [TestCase("Override.Performed")]
    [TestCase("BreakGlass.Used")]
    public async Task ValidateAsync_WithEmergencyAccessKeyword_ReportsEmergencyCount(string eventType)
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = eventType, InsertedDate = DateTimeOffset.UtcNow, User = "doctor" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Emergency Access");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Message, Does.Contain("1 events"));
    }

    #endregion

    #region §164.312(c)(2) — Authentication Mechanism (false path)

    [Test]
    public async Task ValidateAsync_WithNoIntegrity_FailsAuthenticationMechanism()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", InsertedDate = DateTimeOffset.UtcNow, User = "user", AuditIntegrity = null }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Authentication Mechanism");
        Assert.That(result!.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
        Assert.That(result.Recommendations, Is.Not.Empty);
    }

    #endregion

    #region GenerateRecommendations — High, Medium, Low severity sections

    [Test]
    public void GenerateRecommendations_WithHighFailure_IncludesHighSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Login Monitoring", Passed = false,
                Severity = ValidationSeverity.High,
                Message = "No login monitoring",
                RegulationReference = "§164.308(a)(5)(ii)(C)",
                Recommendations = ["Implement login monitoring"]
            }
        };

        var recs = _validator.GenerateRecommendations(results);
        Assert.That(recs.Any(static r => r.Contains("HIGH PRIORITY")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("Login Monitoring")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithMediumFailure_IncludesAddressableSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Automatic Logoff", Passed = false,
                Severity = ValidationSeverity.Medium,
                Message = "No logoff tracking",
                RegulationReference = "§164.312(a)(2)(iii)",
                Recommendations = ["Implement logoff logging"]
            }
        };

        var recs = _validator.GenerateRecommendations(results);
        Assert.That(recs.Any(static r => r.Contains("ADDRESSABLE")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithLowFailure_IncludesLowSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Best Practice", Passed = false,
                Severity = ValidationSeverity.Low,
                Message = "Consider improvement"
            }
        };

        var recs = _validator.GenerateRecommendations(results);
        Assert.That(recs.Any(static r => r.Contains("LOW PRIORITY")), Is.True);
    }

    #endregion
}
