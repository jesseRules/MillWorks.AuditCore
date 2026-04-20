using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Interceptors;

namespace MillWorks.AuditCore.Tests.EntityFramework;

[TestFixture]
[Category("Unit")]
public sealed class RegulatedEntityFailurePolicyTests
{
    private static readonly RegulatedEntityFailurePolicy Policy = new();

    [Test]
    public void Permissive_ReturnsFalse_EvenForRegulatedEntities()
    {
        var context = new AuditFailureContext(
            AuditFailureMode.Permissive,
            Entities: [new AuditFailureEntity(typeof(FerpaEntity), "Modified")]);

        Assert.That(Policy.ShouldFailClosed(context), Is.False);
    }

    [Test]
    public void Permissive_ReturnsFalse_ForEmptyEntities()
    {
        var context = new AuditFailureContext(AuditFailureMode.Permissive, []);

        Assert.That(Policy.ShouldFailClosed(context), Is.False);
    }

    [Test]
    public void FailClosedAlways_ReturnsTrue_ForPlainEntities()
    {
        var context = new AuditFailureContext(
            AuditFailureMode.FailClosedAlways,
            Entities: [new AuditFailureEntity(typeof(PlainEntity), "Added")]);

        Assert.That(Policy.ShouldFailClosed(context), Is.True);
    }

    [Test]
    public void FailClosedAlways_ReturnsTrue_ForEmptyEntities()
    {
        var context = new AuditFailureContext(AuditFailureMode.FailClosedAlways, []);

        Assert.That(Policy.ShouldFailClosed(context), Is.True);
    }

    [Test]
    public void FailClosedForRegulated_WithFerpaClassAttribute_ReturnsTrue()
    {
        var context = new AuditFailureContext(
            AuditFailureMode.FailClosedForRegulated,
            Entities: [new AuditFailureEntity(typeof(FerpaEntity), "Modified")]);

        Assert.That(Policy.ShouldFailClosed(context), Is.True);
    }

    [Test]
    public void FailClosedForRegulated_WithPhiClassAttribute_ReturnsTrue()
    {
        var context = new AuditFailureContext(
            AuditFailureMode.FailClosedForRegulated,
            Entities: [new AuditFailureEntity(typeof(PhiEntity), "Added")]);

        Assert.That(Policy.ShouldFailClosed(context), Is.True);
    }

    [TestCase(ComplianceStandard.HIPAA)]
    [TestCase(ComplianceStandard.FERPA)]
    [TestCase(ComplianceStandard.GDPR)]
    [TestCase(ComplianceStandard.PCI_DSS)]
    public void FailClosedForRegulated_WithSensitiveDataRegulatedStandard_ReturnsTrue(
        ComplianceStandard standard)
    {
        var entityType = SensitiveEntityFactory.GetType(standard);
        var context = new AuditFailureContext(
            AuditFailureMode.FailClosedForRegulated,
            Entities: [new AuditFailureEntity(entityType, "Modified")]);

        Assert.That(Policy.ShouldFailClosed(context), Is.True);
    }

    [TestCase(ComplianceStandard.SOC2)]
    [TestCase(ComplianceStandard.ISO27001)]
    public void FailClosedForRegulated_WithSensitiveDataNonRegulatedStandard_ReturnsFalse(
        ComplianceStandard standard)
    {
        var entityType = SensitiveEntityFactory.GetType(standard);
        var context = new AuditFailureContext(
            AuditFailureMode.FailClosedForRegulated,
            Entities: [new AuditFailureEntity(entityType, "Modified")]);

        Assert.That(Policy.ShouldFailClosed(context), Is.False);
    }

    [Test]
    public void FailClosedForRegulated_WithPlainEntity_ReturnsFalse()
    {
        var context = new AuditFailureContext(
            AuditFailureMode.FailClosedForRegulated,
            Entities: [new AuditFailureEntity(typeof(PlainEntity), "Added")]);

        Assert.That(Policy.ShouldFailClosed(context), Is.False);
    }

    [Test]
    public void FailClosedForRegulated_WithEmptyEntities_ReturnsFalse()
    {
        var context = new AuditFailureContext(AuditFailureMode.FailClosedForRegulated, []);

        Assert.That(Policy.ShouldFailClosed(context), Is.False);
    }

    [Test]
    public void FailClosedForRegulated_WithMixedEntities_ReturnsTrue_WhenAnyRegulated()
    {
        var context = new AuditFailureContext(
            AuditFailureMode.FailClosedForRegulated,
            Entities:
            [
                new AuditFailureEntity(typeof(PlainEntity), "Added"),
                new AuditFailureEntity(typeof(FerpaEntity), "Modified"),
                new AuditFailureEntity(typeof(PlainEntity), "Deleted")
            ]);

        Assert.That(Policy.ShouldFailClosed(context), Is.True);
    }

    // ── Test fixture entity types ───────────────────────────────────────────

    private class PlainEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [FERPA]
    private class FerpaEntity
    {
        public int Id { get; set; }
    }

    [PHI]
    private class PhiEntity
    {
        public int Id { get; set; }
    }

    private class HipaaSensitiveEntity
    {
        [SensitiveData(ApplicableStandards = new[] { ComplianceStandard.HIPAA })]
        public string? Field { get; set; }
    }

    private class FerpaSensitiveEntity
    {
        [SensitiveData(ApplicableStandards = new[] { ComplianceStandard.FERPA })]
        public string? Field { get; set; }
    }

    private class GdprSensitiveEntity
    {
        [SensitiveData(ApplicableStandards = new[] { ComplianceStandard.GDPR })]
        public string? Field { get; set; }
    }

    private class PciDssSensitiveEntity
    {
        [SensitiveData(ApplicableStandards = new[] { ComplianceStandard.PCI_DSS })]
        public string? Field { get; set; }
    }

    private class Soc2SensitiveEntity
    {
        [SensitiveData(ApplicableStandards = new[] { ComplianceStandard.SOC2 })]
        public string? Field { get; set; }
    }

    private class Iso27001SensitiveEntity
    {
        [SensitiveData(ApplicableStandards = new[] { ComplianceStandard.ISO27001 })]
        public string? Field { get; set; }
    }

    private static class SensitiveEntityFactory
    {
        public static Type GetType(ComplianceStandard standard) => standard switch
        {
            ComplianceStandard.HIPAA => typeof(HipaaSensitiveEntity),
            ComplianceStandard.FERPA => typeof(FerpaSensitiveEntity),
            ComplianceStandard.GDPR => typeof(GdprSensitiveEntity),
            ComplianceStandard.PCI_DSS => typeof(PciDssSensitiveEntity),
            ComplianceStandard.SOC2 => typeof(Soc2SensitiveEntity),
            ComplianceStandard.ISO27001 => typeof(Iso27001SensitiveEntity),
            _ => throw new ArgumentOutOfRangeException(nameof(standard), standard, "Unexpected standard")
        };
    }
}
