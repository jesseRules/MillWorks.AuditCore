using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// NIST validator tests. NIST coverage is an approximation composed from the STIG and ISO 27001
/// validators, so these assert the union behavior and the deliberate "runs whenever a constituent
/// standard is enabled, and always returns real results" gating.
/// </summary>
[TestFixture]
public class NistValidatorTests
{
    private NistValidator _validator;

    [SetUp]
    public void Setup()
    {
        _validator = new NistValidator();
    }

    [Test]
    public void Standard_IsNist()
    {
        Assert.That(_validator.Standard, Is.EqualTo(ComplianceStandard.NIST));
    }

    [Test]
    public async Task ValidateAsync_WhenNistEnabled_ReturnsUnionOfStigAndIso()
    {
        var results = await _validator.ValidateAsync(ContextWith(ComplianceStandard.NIST));

        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Any(static r => r.ComplianceStandard == "DISA STIG"), Is.True,
            "NIST report must include the STIG (NIST 800-53 mapped) controls");
        Assert.That(results.Any(static r => r.ComplianceStandard == "ISO 27001"), Is.True,
            "NIST report must include the overlapping ISO 27001 controls");
    }

    [Test]
    public async Task ValidateAsync_WhenOnlyNistEnabled_StillRunsDelegates()
    {
        // Proves the delegates are force-enabled: NIST alone (STIG/ISO not in the enabled set)
        // must still yield STIG + ISO findings, because NIST is defined AS that overlap.
        var results = await _validator.ValidateAsync(ContextWith(ComplianceStandard.NIST));

        Assert.That(results.Any(static r => r.ComplianceStandard == "DISA STIG"), Is.True);
        Assert.That(results.Any(static r => r.ComplianceStandard == "ISO 27001"), Is.True);
    }

    [Test]
    public async Task ValidateAsync_WhenOnlyIso27001Enabled_StillReturnsResults()
    {
        var results = await _validator.ValidateAsync(ContextWith(ComplianceStandard.ISO27001));

        Assert.That(results, Is.Not.Empty,
            "a NIST report derived from an enabled constituent must not be empty");
        Assert.That(results.Any(static r => r.ComplianceStandard == "DISA STIG"), Is.True);
    }

    [Test]
    public async Task ValidateAsync_WhenOnlyStigEnabled_StillReturnsResults()
    {
        var results = await _validator.ValidateAsync(ContextWith(ComplianceStandard.STIG));

        Assert.That(results, Is.Not.Empty);
        Assert.That(results.Any(static r => r.ComplianceStandard == "ISO 27001"), Is.True);
    }

    [Test]
    public async Task ValidateAsync_WhenNoRelevantStandardEnabled_ReturnsEmpty()
    {
        var results = await _validator.ValidateAsync(ContextWith(ComplianceStandard.HIPAA));

        Assert.That(results, Is.Empty);
    }

    [Test]
    public async Task ValidateAsync_ProducesNoDuplicateRuleNames()
    {
        var results = await _validator.ValidateAsync(ContextWith(ComplianceStandard.NIST));

        Assert.That(results.Select(static r => r.RuleName), Is.Unique);
    }

    [Test]
    public async Task ValidateAsync_UnionEqualsDedupedStigPlusIso()
    {
        var context = ContextWith(ComplianceStandard.NIST);
        var overlap = context with
        {
            EnabledStandards = [ComplianceStandard.STIG, ComplianceStandard.ISO27001]
        };
        var expected = (await new StigValidator().ValidateAsync(overlap))
            .Concat(await new Iso27001Validator().ValidateAsync(overlap))
            .Select(static r => r.RuleName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var results = await _validator.ValidateAsync(context);

        Assert.That(results.Count, Is.EqualTo(expected));
    }

    [Test]
    public async Task GenerateRecommendations_IncludesApproximationPreamble()
    {
        var results = await _validator.ValidateAsync(ContextWith(ComplianceStandard.NIST));

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations, Is.Not.Empty);
        Assert.That(recommendations[0], Does.Contain("approximated"));
    }

    private static ComplianceValidationContext ContextWith(params ComplianceStandard[] enabledStandards)
    {
        var events = SampleEvents();
        return new ComplianceValidationContext
        {
            Events = events,
            OldestEventDate = events.Where(static e => e.InsertedDate.HasValue).Min(static e => e.InsertedDate),
            NewestEventDate = events.Where(static e => e.InsertedDate.HasValue).Max(static e => e.InsertedDate),
            TotalEventCount = events.Count,
            UnprotectedEventCount = 0,
            EnabledStandards = enabledStandards.ToHashSet()
        };
    }

    private static List<AuditEventEntity> SampleEvents() =>
    [
        new()
        {
            EventType = "User.Login",
            InsertedDate = DateTimeOffset.UtcNow,
            User = "testuser",
            IpAddress = "10.0.0.1",
            MachineName = "host1",
            Action = "Added",
            CorrelationId = Guid.NewGuid().ToString()
        },
        new()
        {
            EventType = "Data.Update",
            InsertedDate = DateTimeOffset.UtcNow,
            User = "testuser",
            IpAddress = "10.0.0.2",
            AssemblyName = "MillWorks.Api",
            Action = "Modified",
            CorrelationId = Guid.NewGuid().ToString()
        }
    ];
}
