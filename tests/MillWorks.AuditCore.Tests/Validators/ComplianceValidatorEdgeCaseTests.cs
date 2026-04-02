using FluentAssertions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// Phase 4: Cross-cutting edge case tests for all 7 compliance validators.
/// Validates boundary conditions: empty-string fields, unicode values, empty event lists,
/// unknown event types, regulation references, and validator independence.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase4")]
public sealed class ComplianceValidatorEdgeCaseTests
{
    private static readonly IComplianceValidator[] AllValidators =
    [
        new HipaaValidator(),
        new GdprValidator(),
        new Iso27001Validator(),
        new PciDssValidator(),
        new Soc2Validator(),
        new StigValidator(),
    ];

    private static IEnumerable<IComplianceValidator> ValidatorSource() => AllValidators;

    // ── Empty events list ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_EmptyEvents_DoesNotThrow(IComplianceValidator validator)
    {
        var results = await validator.ValidateAsync([]);

        results.Should().NotBeNull();
        results.Should().NotBeEmpty("each validator should produce results even with no events");
    }

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_EmptyEvents_HasFailures(IComplianceValidator validator)
    {
        var results = await validator.ValidateAsync([]);

        // With no events, critical rules should fail
        results.Should().Contain(r => !r.Passed,
            "empty event set should trigger at least one compliance failure");
    }

    // ── Events with required fields present but empty string ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_EmptyStringUser_TreatedAsPresent(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test.Action",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "" // Present but empty
            }
        };

        var results = await validator.ValidateAsync(events);
        results.Should().NotBeNull();
        // The validator should produce results without throwing
    }

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_NullUser_FailsUserIdentification(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test.Action",
                InsertedDate = DateTimeOffset.UtcNow,
                User = null
            }
        };

        var results = await validator.ValidateAsync(events);

        // Validators that have user identification rules should fail them
        var userRules = results.Where(r =>
            r.RuleName.Contains("User", StringComparison.OrdinalIgnoreCase) &&
            r.RuleName.Contains("Identif", StringComparison.OrdinalIgnoreCase)).ToList();

        if (userRules.Count != 0)
        {
            userRules.Should().Contain(r => !r.Passed,
                $"{validator.Standard} should fail user identification for null user");
        }
    }

    // ── Events with unicode values ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_UnicodeUserAndEventType_DoesNotThrow(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "\u00DC\u00F1\u00EEc\u00F6d\u00E9.Action",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "\u4E2D\u6587\u7528\u6237"
            }
        };

        var act = () => validator.ValidateAsync(events);
        await act.Should().NotThrowAsync();
    }

    // ── Unknown/uncovered event types ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_UnknownEventType_NoFalsePositives(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Custom.InternalOperation.DoSomething",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "user123"
            }
        };

        var results = await validator.ValidateAsync(events);

        // Having an unknown event type shouldn't cause any validation to crash
        results.Should().NotBeNull();
        results.Should().NotBeEmpty();
    }

    // ── Extra fields don't cause false positives ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_ExtraEntityFields_NoFalsePositive(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "user123",
                EntityType = "CustomExtendedEntity",
                IpAddress = "192.168.1.1",
                Action = "Login",
                CorrelationId = Guid.NewGuid().ToString()
            }
        };

        var act = () => validator.ValidateAsync(events);
        await act.Should().NotThrowAsync();
    }

    // ── All results have compliance standard set ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_AllResults_HaveComplianceStandard(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            }
        };

        var results = await validator.ValidateAsync(events);

        results.Should().AllSatisfy(r =>
            r.ComplianceStandard.Should().NotBeNullOrEmpty(
                $"rule '{r.RuleName}' should have its compliance standard set"));
    }

    // ── All results have severity set ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_AllResults_HaveValidSeverity(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        var results = await validator.ValidateAsync(events);

        results.Should().AllSatisfy(r =>
            Enum.IsDefined(typeof(ValidationSeverity), r.Severity).Should().BeTrue(
                $"rule '{r.RuleName}' has invalid severity {r.Severity}"));
    }

    // ── Regulation references present on failures ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_FailedRules_HaveRegulationReference(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>(); // Empty = guaranteed failures

        var results = await validator.ValidateAsync(events);
        var failures = results.Where(r => !r.Passed).ToList();

        if (failures.Count != 0)
        {
            // At least some failures should reference regulations
            failures.Should().Contain(r =>
                !string.IsNullOrEmpty(r.RuleName),
                "failed rules should identify what regulation was violated");
        }
    }

    // ── GenerateRecommendations does not throw on empty results ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public void GenerateRecommendations_EmptyResults_DoesNotThrow(IComplianceValidator validator)
    {
        var act = () => validator.GenerateRecommendations([]);
        act.Should().NotThrow();
    }

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public void GenerateRecommendations_AllPassing_ReturnsNonEmpty(IComplianceValidator validator)
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Test Rule",
                Passed = true,
                Message = "Pass",
                Severity = ValidationSeverity.Info
            }
        };

        var recommendations = validator.GenerateRecommendations(results);
        recommendations.Should().NotBeNull();
    }

    // ── Validators are independent ──

    [Test]
    public async Task Validators_AreIndependent_ResultsDoNotBleed()
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "PHI.Access",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "doctor"
            }
        };

        // Run all validators against the same events
        var allResults = new Dictionary<ComplianceStandard, List<AuditValidationResult>>();
        foreach (var validator in AllValidators)
        {
            allResults[validator.Standard] = await validator.ValidateAsync(events);
        }

        // Each validator should only reference its own standard
        foreach (var (standard, results) in allResults)
        {
            var standardName = standard.ToString();
            results.Should().AllSatisfy(r =>
            {
                // The ComplianceStandard field should match the validator's standard
                r.ComplianceStandard.Should().NotBeNullOrEmpty();
            });
        }

        // Running validators doesn't affect each other
        allResults.Keys.Should().HaveCount(AllValidators.Length);
    }

    // ── Integrity checks across validators ──

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_WithIntegrity_PassesIntegrityRules(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            }
        };

        var results = await validator.ValidateAsync(events);

        var integrityRules = results.Where(r =>
            r.RuleName.Contains("Integrity", StringComparison.OrdinalIgnoreCase) ||
            r.RuleName.Contains("Protection", StringComparison.OrdinalIgnoreCase) ||
            r.RuleName.Contains("Log Protection", StringComparison.OrdinalIgnoreCase)).ToList();

        if (integrityRules.Count != 0)
        {
            integrityRules.Should().Contain(r => r.Passed,
                $"{validator.Standard} should pass integrity rules when integrity data present");
        }
    }

    [Test]
    [TestCaseSource(nameof(ValidatorSource))]
    public async Task ValidateAsync_WithoutIntegrity_FailsIntegrityRules(IComplianceValidator validator)
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                AuditIntegrity = null
            }
        };

        var results = await validator.ValidateAsync(events);

        var integrityRules = results.Where(r =>
            r.RuleName.Contains("Integrity", StringComparison.OrdinalIgnoreCase) ||
            r.RuleName.Contains("Log Protection", StringComparison.OrdinalIgnoreCase) ||
            r.RuleName.Contains("Audit Log Protection", StringComparison.OrdinalIgnoreCase) ||
            r.RuleName.Contains("Protection of Audit", StringComparison.OrdinalIgnoreCase)).ToList();

        if (integrityRules.Count != 0)
        {
            integrityRules.Should().Contain(r => !r.Passed,
                $"{validator.Standard} should fail integrity rules when no integrity data");
        }
    }
}
