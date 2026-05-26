using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Sinks.Writers;

[TestFixture]
[Category("Unit")]
public sealed class WriteOutcomeTests
{
    [Test]
    public void Success_CreatesSucceededOutcome()
    {
        var envelopeId = Guid.NewGuid();

        var outcome = WriteOutcome.Success(envelopeId);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.EnvelopeId, Is.EqualTo(envelopeId));
            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(outcome.IsDuplicate, Is.False);
            Assert.That(outcome.IsRetryable, Is.False);
            Assert.That(outcome.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public void Duplicate_CreatesSucceededDuplicateOutcome()
    {
        var envelopeId = Guid.NewGuid();

        var outcome = WriteOutcome.Duplicate(envelopeId);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.EnvelopeId, Is.EqualTo(envelopeId));
            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(outcome.IsDuplicate, Is.True);
            Assert.That(outcome.IsRetryable, Is.False);
            Assert.That(outcome.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public void Failed_CreatesFailedOutcome()
    {
        var envelopeId = Guid.NewGuid();
        const string errorMessage = "Connection timeout";

        var outcome = WriteOutcome.Failed(envelopeId, errorMessage);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.EnvelopeId, Is.EqualTo(envelopeId));
            Assert.That(outcome.Succeeded, Is.False);
            Assert.That(outcome.IsDuplicate, Is.False);
            Assert.That(outcome.IsRetryable, Is.False);
            Assert.That(outcome.ErrorMessage, Is.EqualTo(errorMessage));
        });
    }

    [Test]
    public void Failed_WithRetryable_SetsIsRetryable()
    {
        var envelopeId = Guid.NewGuid();

        var outcome = WriteOutcome.Failed(envelopeId, "Deadlock detected", isRetryable: true);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Succeeded, Is.False);
            Assert.That(outcome.IsRetryable, Is.True);
        });
    }

    [Test]
    public void DirectConstruction_AllowsCustomValues()
    {
        var envelopeId = Guid.NewGuid();

        var outcome = new WriteOutcome
        {
            EnvelopeId = envelopeId,
            Succeeded = false,
            IsDuplicate = false,
            IsRetryable = true,
            ErrorMessage = "Custom error"
        };

        Assert.Multiple(() =>
        {
            Assert.That(outcome.EnvelopeId, Is.EqualTo(envelopeId));
            Assert.That(outcome.Succeeded, Is.False);
            Assert.That(outcome.IsRetryable, Is.True);
            Assert.That(outcome.ErrorMessage, Is.EqualTo("Custom error"));
        });
    }

    [Test]
    public void EnvelopeId_IsRequired()
    {
        var envelopeId = Guid.NewGuid();
        var outcome = WriteOutcome.Success(envelopeId);

        Assert.That(outcome.EnvelopeId, Is.EqualTo(envelopeId));
        Assert.That(outcome.EnvelopeId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public void MultipleOutcomes_PreserveDistinctEnvelopeIds()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var outcomes = new[]
        {
            WriteOutcome.Success(id1),
            WriteOutcome.Duplicate(id2),
            WriteOutcome.Failed(id3, "error")
        };

        Assert.That(outcomes.Select(o => o.EnvelopeId), Is.EquivalentTo(new[] { id1, id2, id3 }));
    }
}
