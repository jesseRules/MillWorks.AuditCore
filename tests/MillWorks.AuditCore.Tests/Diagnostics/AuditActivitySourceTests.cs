using System.Diagnostics;
using MillWorks.AuditCore.Abstractions.Diagnostics;

namespace MillWorks.AuditCore.Tests.Diagnostics;

[TestFixture]
[Category("Unit")]
public sealed class AuditActivitySourceTests
{
    private ActivityListener _listener = null!;
    private List<Activity> _capturedActivities = null!;

    [SetUp]
    public void SetUp()
    {
        _capturedActivities = [];
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AuditActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _capturedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    [TearDown]
    public void TearDown()
    {
        _listener.Dispose();
        _capturedActivities.Clear();
    }

    [Test]
    public void ActivitySource_HasCorrectName()
    {
        Assert.That(AuditActivitySource.Name, Is.EqualTo("MillWorks.AuditCore"));
    }

    [Test]
    public void ActivitySource_HasCorrectVersion()
    {
        Assert.That(AuditActivitySource.Version, Is.EqualTo("1.0.0"));
    }

    [Test]
    public void StartActivity_ReturnsActivityWhenListenerRegistered()
    {
        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditWrite,
            ActivityKind.Internal);

        Assert.That(activity, Is.Not.Null);
        Assert.That(activity!.OperationName, Is.EqualTo(AuditActivitySource.Operations.AuditWrite));
    }

    [Test]
    public void StartActivity_ReturnsNullWhenNoListenerRegistered()
    {
        _listener.Dispose();

        using var newSource = new ActivitySource("Test.NoListener", "1.0.0");
        using var activity = newSource.StartActivity("test.operation");

        Assert.That(activity, Is.Null);
    }

    [Test]
    public void Activity_CanSetTags()
    {
        var eventId = Guid.NewGuid();
        var eventType = "User.Created";

        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditWrite,
            ActivityKind.Internal);

        activity?.SetTag(AuditActivitySource.Tags.AuditEventId, eventId.ToString());
        activity?.SetTag(AuditActivitySource.Tags.AuditEventType, eventType);
        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");

        Assert.That(activity, Is.Not.Null);
        Assert.That(activity!.GetTagItem(AuditActivitySource.Tags.AuditEventId), Is.EqualTo(eventId.ToString()));
        Assert.That(activity.GetTagItem(AuditActivitySource.Tags.AuditEventType), Is.EqualTo(eventType));
        Assert.That(activity.GetTagItem(AuditActivitySource.Tags.Outcome), Is.EqualTo("success"));
    }

    [Test]
    public void Activity_CanAddEvents()
    {
        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditWrite,
            ActivityKind.Internal);

        activity?.AddEvent(new ActivityEvent(AuditActivitySource.Events.RetryAttempt));
        activity?.AddEvent(new ActivityEvent(AuditActivitySource.Events.DlqRouted));

        Assert.That(activity, Is.Not.Null);
        Assert.That(activity!.Events.Count(), Is.EqualTo(2));
        Assert.That(activity.Events.Any(e => e.Name == AuditActivitySource.Events.RetryAttempt), Is.True);
        Assert.That(activity.Events.Any(e => e.Name == AuditActivitySource.Events.DlqRouted), Is.True);
    }

    [Test]
    public void Operations_ContainsExpectedConstants()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AuditActivitySource.Operations.AuditWrite, Is.EqualTo("audit.write"));
            Assert.That(AuditActivitySource.Operations.AuditWriteBatch, Is.EqualTo("audit.write_batch"));
            Assert.That(AuditActivitySource.Operations.AuditQuery, Is.EqualTo("audit.query"));
            Assert.That(AuditActivitySource.Operations.AuditArchive, Is.EqualTo("audit.archive"));
            Assert.That(AuditActivitySource.Operations.AuditRestore, Is.EqualTo("audit.restore"));
            Assert.That(AuditActivitySource.Operations.OutboxWrite, Is.EqualTo("outbox.write"));
            Assert.That(AuditActivitySource.Operations.OutboxDrain, Is.EqualTo("outbox.drain"));
            Assert.That(AuditActivitySource.Operations.IntegrityWrite, Is.EqualTo("integrity.write"));
            Assert.That(AuditActivitySource.Operations.IntegrityFlush, Is.EqualTo("integrity.flush"));
            Assert.That(AuditActivitySource.Operations.IntegrityCheck, Is.EqualTo("integrity.check"));
            Assert.That(AuditActivitySource.Operations.IntegrityReconcile, Is.EqualTo("integrity.reconcile"));
        });
    }

    [Test]
    public void Tags_ContainsExpectedConstants()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AuditActivitySource.Tags.AuditEventId, Is.EqualTo("audit.event.id"));
            Assert.That(AuditActivitySource.Tags.AuditEventType, Is.EqualTo("audit.event.type"));
            Assert.That(AuditActivitySource.Tags.AuditEntityType, Is.EqualTo("audit.entity.type"));
            Assert.That(AuditActivitySource.Tags.AuditEntityId, Is.EqualTo("audit.entity.id"));
            Assert.That(AuditActivitySource.Tags.AuditUserId, Is.EqualTo("audit.user.id"));
            Assert.That(AuditActivitySource.Tags.BatchSize, Is.EqualTo("batch.size"));
            Assert.That(AuditActivitySource.Tags.ProcessedCount, Is.EqualTo("processed.count"));
            Assert.That(AuditActivitySource.Tags.Outcome, Is.EqualTo("outcome"));
            Assert.That(AuditActivitySource.Tags.ArchiveId, Is.EqualTo("archive.id"));
            Assert.That(AuditActivitySource.Tags.QueryType, Is.EqualTo("query.type"));
            Assert.That(AuditActivitySource.Tags.RetryAttempt, Is.EqualTo("retry.attempt"));
            Assert.That(AuditActivitySource.Tags.OutboxRowId, Is.EqualTo("outbox.row.id"));
        });
    }

    [Test]
    public void Events_ContainsExpectedConstants()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AuditActivitySource.Events.DlqRouted, Is.EqualTo("dlq_routed"));
            Assert.That(AuditActivitySource.Events.IntegrityFailed, Is.EqualTo("integrity_failed"));
            Assert.That(AuditActivitySource.Events.RetryAttempt, Is.EqualTo("retry_attempt"));
            Assert.That(AuditActivitySource.Events.OutboxExhausted, Is.EqualTo("outbox_exhausted"));
        });
    }

    [Test]
    public void CapturedActivities_ContainsStartedActivity()
    {
        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditQuery,
            ActivityKind.Internal);

        activity?.SetTag(AuditActivitySource.Tags.QueryType, "entity_trail");

        Assert.That(_capturedActivities, Has.Count.EqualTo(1));
        Assert.That(_capturedActivities[0].OperationName, Is.EqualTo(AuditActivitySource.Operations.AuditQuery));
        Assert.That(_capturedActivities[0].GetTagItem(AuditActivitySource.Tags.QueryType), Is.EqualTo("entity_trail"));
    }

    [Test]
    public void MultipleActivities_AllCaptured()
    {
        using (var activity1 = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditWrite,
            ActivityKind.Internal))
        {
            activity1?.SetTag(AuditActivitySource.Tags.AuditEventId, "event-1");
        }

        using (var activity2 = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.OutboxDrain,
            ActivityKind.Internal))
        {
            activity2?.SetTag(AuditActivitySource.Tags.BatchSize, 10);
        }

        Assert.That(_capturedActivities, Has.Count.EqualTo(2));
        Assert.That(_capturedActivities[0].OperationName, Is.EqualTo(AuditActivitySource.Operations.AuditWrite));
        Assert.That(_capturedActivities[1].OperationName, Is.EqualTo(AuditActivitySource.Operations.OutboxDrain));
    }

    [Test]
    public void Activity_PropagatesTraceContext()
    {
        using var parentActivity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditWriteBatch,
            ActivityKind.Internal);

        Assert.That(parentActivity, Is.Not.Null);

        using var childActivity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditWrite,
            ActivityKind.Internal);

        Assert.That(childActivity, Is.Not.Null);
        Assert.That(childActivity!.TraceId, Is.EqualTo(parentActivity!.TraceId));
        Assert.That(childActivity.ParentSpanId, Is.EqualTo(parentActivity.SpanId));
    }

    [Test]
    public void ZeroOverhead_WhenNoListenerRegistered()
    {
        _listener.Dispose();
        _capturedActivities.Clear();

        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditWrite,
            ActivityKind.Internal);

        Assert.That(activity, Is.Null);
        Assert.That(_capturedActivities, Is.Empty);
    }
}
