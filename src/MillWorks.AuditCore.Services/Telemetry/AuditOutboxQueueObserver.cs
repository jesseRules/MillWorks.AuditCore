using System.Diagnostics.Metrics;

namespace MillWorks.AuditCore.Services.Telemetry;

/// <summary>
/// Publishes outbox queue-depth gauges without ever touching the database from a
/// metrics callback:
/// <list type="bullet">
///   <item><c>audit.outbox.pending_count</c></item>
///   <item><c>audit.outbox.inflight_count</c></item>
///   <item><c>audit.outbox.oldest_pending_age_seconds</c></item>
/// </list>
/// <para>
/// The drainer samples the outbox on its own cadence and pushes the latest values via
/// <see cref="Update"/>; the observable-gauge callbacks only read those cached values.
/// This is deliberate: <see cref="ObservableInstrument{T}"/> callbacks run synchronously
/// on the metrics-collection thread, so issuing an async <c>DbContext</c> query there
/// (e.g. via <c>GetAwaiter().GetResult()</c>) would block the collector and concurrently
/// use a non-thread-safe context. Caching keeps the callback allocation-free and instant.
/// </para>
/// <para>
/// Every running instance reports the global queue depth independently (each samples the
/// same shared table), so aggregate these gauges across instances with <c>max</c> or
/// <c>mean</c> — never <c>sum</c>.
/// </para>
/// <para>
/// Owns its own <see cref="Meter"/> (same name/version as <see cref="AuditMetrics"/>) so the
/// gauges share the audit meter for subscription purposes yet are released deterministically
/// on <see cref="Dispose"/>. Instrument names are unique, so there is no collision with the
/// counters/histograms registered on the <see cref="AuditMetrics"/> meter.
/// </para>
/// </summary>
public sealed class AuditOutboxQueueObserver : IDisposable
{
    private readonly Meter _meter;

    private long _pendingCount;
    private long _inFlightCount;
    private long _oldestPendingAgeSeconds;

    public AuditOutboxQueueObserver()
    {
        _meter = new Meter(AuditMetrics.MeterName, AuditMetrics.MeterVersion);

        _meter.CreateObservableGauge(
            AuditMetrics.Names.OutboxPendingCount,
            () => Interlocked.Read(ref _pendingCount),
            unit: "rows",
            description: "Outbox rows awaiting processing (Status = Pending)");

        _meter.CreateObservableGauge(
            AuditMetrics.Names.OutboxInFlightCount,
            () => Interlocked.Read(ref _inFlightCount),
            unit: "rows",
            description: "Outbox rows currently claimed by a drainer (Status = InFlight)");

        _meter.CreateObservableGauge(
            AuditMetrics.Names.OutboxOldestPendingAge,
            () => Interlocked.Read(ref _oldestPendingAgeSeconds),
            unit: "s",
            description: "Age of the oldest pending outbox row in seconds; 0 when the queue is empty");
    }

    /// <summary>
    /// Records the latest sampled queue-depth values. The gauge callbacks read these on the
    /// next metrics collection. Thread-safe; intended to be called from the drainer loop.
    /// </summary>
    public void Update(long pendingCount, long inFlightCount, long oldestPendingAgeSeconds)
    {
        Interlocked.Exchange(ref _pendingCount, pendingCount);
        Interlocked.Exchange(ref _inFlightCount, inFlightCount);
        Interlocked.Exchange(ref _oldestPendingAgeSeconds, oldestPendingAgeSeconds);
    }

    public void Dispose() => _meter.Dispose();
}
