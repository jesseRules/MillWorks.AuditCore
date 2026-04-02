using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// Tests for StigValidator covering the ~9% uncovered branches: the 3-4 category
/// severity ternary in Auditable Events, BuildMissingCategoryRecommendations helper,
/// and GenerateRecommendations low-severity section.
/// </summary>
[TestFixture]
[Category("Unit")]
public class StigValidatorBranchTests
{
    private StigValidator _validator;

    [SetUp]
    public void Setup()
    {
        _validator = new StigValidator();
    }

    private static AuditValidationResult? Find(List<AuditValidationResult> results, string ruleNamePart)
        => results.FirstOrDefault(r => r.RuleName.Contains(ruleNamePart, StringComparison.OrdinalIgnoreCase));

    #region V-222569 / AU-2 — Auditable Events (3-category severity + BuildMissingCategoryRecommendations)

    [Test]
    public async Task ValidateAsync_WithThreeCategories_PassesWithMediumSeverity()
    {
        // 3 categories: logon + object access + privilege (missing logoff + policy)
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Login.Success", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Data.Access", Action = "Modified", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Role.Elevate", InsertedDate = DateTimeOffset.UtcNow, User = "admin" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Auditable Events");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
        Assert.That(result.Message, Does.Contain("3/5"));
    }

    [Test]
    public async Task ValidateAsync_WithFourCategories_PassesWithInfoSeverity()
    {
        // 4 categories: logon + logoff + object access + privilege (missing policy)
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Login.Success", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "User.Logout", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Data.Access", Action = "Added", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Permission.Changed", InsertedDate = DateTimeOffset.UtcNow, User = "admin" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Auditable Events");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Info));
        Assert.That(result.Message, Does.Contain("4/5"));
    }

    [Test]
    public async Task ValidateAsync_WithThreeCategories_RecommendsMissingLogoffAndPolicy()
    {
        // 3 categories: logon + object access + privilege (missing logoff + policy)
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Login.Success", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Object.Create", Action = "Added", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Sudo.Execute", InsertedDate = DateTimeOffset.UtcNow, User = "admin" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Auditable Events");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Recommendations, Is.Not.Empty);
        Assert.That(result.Recommendations.Any(static r => r.Contains("logoff")), Is.True);
        Assert.That(result.Recommendations.Any(static r => r.Contains("policy")), Is.True);
    }

    [Test]
    public async Task ValidateAsync_WithFourCategoriesMissingPrivilege_RecommendsPrivilege()
    {
        // 4 categories: logon + logoff + object access + policy (missing privilege)
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Login.Success", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Session.Logoff", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Data.Delete", Action = "Deleted", InsertedDate = DateTimeOffset.UtcNow, User = "user" },
            new() { EventType = "Policy.Updated", InsertedDate = DateTimeOffset.UtcNow, User = "admin" }
        };

        var results = await _validator.ValidateAsync(events);

        var result = Find(results, "Auditable Events");
        Assert.That(result!.Passed, Is.True);
        Assert.That(result.Recommendations, Is.Not.Empty);
        Assert.That(result.Recommendations.Any(static r => r.Contains("privileged")), Is.True);
        // Should NOT recommend logoff or policy since those are present
        Assert.That(result.Recommendations.Any(static r => r.Contains("logoff")), Is.False);
        Assert.That(result.Recommendations.Any(static r => r.Contains("policy")), Is.False);
    }

    #endregion

    #region GenerateRecommendations — Low Severity Section

    [Test]
    public void GenerateRecommendations_WithLowSeverityFinding_IncludesInformationalSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Minor Finding",
                Passed = false,
                Severity = ValidationSeverity.Low,
                Message = "Low priority issue"
            }
        };

        var recs = _validator.GenerateRecommendations(results);

        Assert.That(recs.Any(static r => r.Contains("INFORMATIONAL")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("Minor Finding")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithAllSeverities_IncludesAllSections()
    {
        var results = new List<AuditValidationResult>
        {
            new() { RuleName = "C", Passed = false, Severity = ValidationSeverity.Critical, Message = "c", RegulationReference = "ref", Recommendations = ["fix"] },
            new() { RuleName = "H", Passed = false, Severity = ValidationSeverity.High, Message = "h", RegulationReference = "ref", Recommendations = ["fix"] },
            new() { RuleName = "M", Passed = false, Severity = ValidationSeverity.Medium, Message = "m", RegulationReference = "ref", Recommendations = ["fix"] },
            new() { RuleName = "L", Passed = false, Severity = ValidationSeverity.Low, Message = "l" }
        };

        var recs = _validator.GenerateRecommendations(results);

        Assert.That(recs.Any(static r => r.Contains("CAT I")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("CAT II")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("CAT III")), Is.True);
        Assert.That(recs.Any(static r => r.Contains("INFORMATIONAL")), Is.True);
    }

    #endregion
}
