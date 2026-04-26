using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Tests.Abstractions;

/// <summary>
/// Pins the Phase 04.5 lift location: the four marker attributes
/// (<c>[PHI]</c>, <c>[FERPA]</c>, <c>[SensitiveData]</c>, <c>[NoAudit]</c>),
/// the <see cref="SensitiveDataType"/> enum, and <see cref="AuditLogDto"/>
/// must resolve to <c>MillWorks.AuditCore.Abstractions</c>. A regression that
/// re-introduced an EntityFramework-namespaced sibling would re-couple
/// consumer libraries to the EF package and is what this fixture exists to
/// catch.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class LiftLocationTests
{
    [Test]
    public void PHIAttribute_ResolvesToAbstractionsAttributesNamespace()
    {
        Assert.That(typeof(PHIAttribute).Namespace,
            Is.EqualTo("MillWorks.AuditCore.Abstractions.Attributes"));
    }

    [Test]
    public void FERPAAttribute_ResolvesToAbstractionsAttributesNamespace()
    {
        Assert.That(typeof(FERPAAttribute).Namespace,
            Is.EqualTo("MillWorks.AuditCore.Abstractions.Attributes"));
    }

    [Test]
    public void SensitiveDataAttribute_ResolvesToAbstractionsAttributesNamespace()
    {
        Assert.That(typeof(SensitiveDataAttribute).Namespace,
            Is.EqualTo("MillWorks.AuditCore.Abstractions.Attributes"));
    }

    [Test]
    public void NoAuditAttribute_ResolvesToAbstractionsAttributesNamespace()
    {
        Assert.That(typeof(NoAuditAttribute).Namespace,
            Is.EqualTo("MillWorks.AuditCore.Abstractions.Attributes"));
    }

    [Test]
    public void SensitiveDataType_ResolvesToAbstractionsAttributesNamespace()
    {
        Assert.That(typeof(SensitiveDataType).Namespace,
            Is.EqualTo("MillWorks.AuditCore.Abstractions.Attributes"));
    }

    [Test]
    public void AuditLogDto_ResolvesToAbstractionsDtoNamespace()
    {
        Assert.That(typeof(AuditLogDto).Namespace,
            Is.EqualTo("MillWorks.AuditCore.Abstractions.Dto"));
    }

    [Test]
    public void LiftedTypes_LiveInAbstractionsAssembly()
    {
        var abstractionsAssembly = typeof(IAuditContextSource).Assembly;

        Assert.Multiple(() =>
        {
            Assert.That(typeof(PHIAttribute).Assembly,
                Is.SameAs(abstractionsAssembly));
            Assert.That(typeof(FERPAAttribute).Assembly,
                Is.SameAs(abstractionsAssembly));
            Assert.That(typeof(SensitiveDataAttribute).Assembly,
                Is.SameAs(abstractionsAssembly));
            Assert.That(typeof(NoAuditAttribute).Assembly,
                Is.SameAs(abstractionsAssembly));
            Assert.That(typeof(SensitiveDataType).Assembly,
                Is.SameAs(abstractionsAssembly));
            Assert.That(typeof(AuditLogDto).Assembly,
                Is.SameAs(abstractionsAssembly));
        });
    }
}
