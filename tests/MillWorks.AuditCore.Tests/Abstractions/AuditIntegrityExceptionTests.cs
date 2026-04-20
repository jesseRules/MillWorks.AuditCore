using MillWorks.AuditCore.Abstractions.Exceptions;

namespace MillWorks.AuditCore.Tests.Abstractions;

[TestFixture]
[Category("Unit")]
public sealed class AuditIntegrityExceptionTests
{
    [Test]
    public void Constructor_AssignsAllProperties()
    {
        var inner = new InvalidOperationException("root cause");

        var ex = new AuditIntegrityException(
            entityName: "Patient",
            action: "Modified",
            failureReason: "AuditLogs insert failed",
            innerException: inner);

        Assert.That(ex.EntityName, Is.EqualTo("Patient"));
        Assert.That(ex.Action, Is.EqualTo("Modified"));
        Assert.That(ex.FailureReason, Is.EqualTo("AuditLogs insert failed"));
    }

    [Test]
    public void Constructor_ComposesMessage()
    {
        var inner = new InvalidOperationException("root cause");

        var ex = new AuditIntegrityException(
            "Patient",
            "Modified",
            "AuditLogs insert failed",
            inner);

        Assert.That(ex.Message, Is.EqualTo(
            "Audit integrity failure for Patient (Modified): AuditLogs insert failed"));
    }

    [Test]
    public void Constructor_PreservesInnerException()
    {
        var inner = new InvalidOperationException("root cause");

        var ex = new AuditIntegrityException("Patient", "Added", "AuditLogs insert failed", inner);

        Assert.That(ex.InnerException, Is.SameAs(inner));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Constructor_WhenEntityNameIsNullOrWhiteSpace_Throws(string? entityName)
    {
        var inner = new InvalidOperationException();

        Assert.That(
            () => new AuditIntegrityException(entityName!, "Modified", "reason", inner),
            Throws.InstanceOf<ArgumentException>());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Constructor_WhenActionIsNullOrWhiteSpace_Throws(string? action)
    {
        var inner = new InvalidOperationException();

        Assert.That(
            () => new AuditIntegrityException("Patient", action!, "reason", inner),
            Throws.InstanceOf<ArgumentException>());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Constructor_WhenFailureReasonIsNullOrWhiteSpace_Throws(string? failureReason)
    {
        var inner = new InvalidOperationException();

        Assert.That(
            () => new AuditIntegrityException("Patient", "Modified", failureReason!, inner),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Constructor_WhenInnerExceptionIsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AuditIntegrityException("Patient", "Modified", "reason", innerException: null!));
    }
}
