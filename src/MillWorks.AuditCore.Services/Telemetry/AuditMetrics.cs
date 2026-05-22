using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;

namespace MillWorks.AuditCore.Services.Telemetry;

/// <summary>
/// Centralized metrics for the audit system. All instruments use the same meter
/// to simplify subscription and filtering in observability backends.
/// </summary>
public static class AuditMetrics
{
    public const string MeterName = "MillWorks.AuditCore";
    public const string MeterVersion = "1.0.0";

    private static readonly Meter _meter = new(MeterName, MeterVersion);

    #region Metric Names

    public static class Names
    {
        public const string OutboxBatchSize = "audit.outbox.batch_size";
        public const string OutboxDrainDuration = "audit.outbox.drain_duration_ms";
        public const string OutboxRowAge = "audit.outbox.row_age_seconds";
        public const string EnvelopesPublished = "audit.envelopes.published";
        public const string EnvelopesFailed = "audit.envelopes.failed";
        public const string EnvelopesDuplicate = "audit.envelopes.duplicate";
        public const string RetryAttempts = "audit.outbox.retry_attempts";
        public const string DlqRouted = "audit.outbox.dlq_routed";
        public const string LeasesRecovered = "audit.outbox.drainer.leases_recovered";
    }

    #endregion

    #region Tag Keys

    public static class Tags
    {
        public const string EnvelopeKind = "envelope_kind";
        public const string ErrorType = "error_type";
    }

    #endregion

    #region Error Type Values

    public static class ErrorTypes
    {
        public const string Deadlock = "deadlock";
        public const string Timeout = "timeout";
        public const string Constraint = "constraint";
        public const string Serialization = "serialization";
        public const string Unknown = "unknown";
    }

    #endregion

    #region Histograms

    public static readonly Histogram<int> OutboxBatchSize = _meter.CreateHistogram<int>(
        Names.OutboxBatchSize,
        unit: "rows",
        description: "Number of rows claimed per outbox drain batch");

    public static readonly Histogram<double> OutboxDrainDuration = _meter.CreateHistogram<double>(
        Names.OutboxDrainDuration,
        unit: "ms",
        description: "Time to process a single outbox drain cycle");

    public static readonly Histogram<double> OutboxRowAge = _meter.CreateHistogram<double>(
        Names.OutboxRowAge,
        unit: "s",
        description: "Age of outbox rows from creation to drain");

    #endregion

    #region Counters

    public static readonly Counter<long> EnvelopesPublished = _meter.CreateCounter<long>(
        Names.EnvelopesPublished,
        unit: "envelopes",
        description: "Number of envelopes successfully published");

    public static readonly Counter<long> EnvelopesFailed = _meter.CreateCounter<long>(
        Names.EnvelopesFailed,
        unit: "envelopes",
        description: "Number of envelopes that failed with non-retryable errors");

    public static readonly Counter<long> EnvelopesDuplicate = _meter.CreateCounter<long>(
        Names.EnvelopesDuplicate,
        unit: "envelopes",
        description: "Number of duplicate envelopes detected (idempotent replays)");

    public static readonly Counter<long> RetryAttempts = _meter.CreateCounter<long>(
        Names.RetryAttempts,
        unit: "retries",
        description: "Number of envelope processing retries scheduled");

    public static readonly Counter<long> DlqRouted = _meter.CreateCounter<long>(
        Names.DlqRouted,
        unit: "rows",
        description: "Number of outbox rows that exhausted retries and were routed to DLQ");

    public static readonly Counter<long> LeasesRecovered = _meter.CreateCounter<long>(
        Names.LeasesRecovered,
        unit: "rows",
        description: "Number of outbox rows recovered from expired leases");

    #endregion

    #region Error Classification

    /// <summary>
    /// Classifies a database exception for metrics tagging. Provider-agnostic with
    /// specific handling for SQL Server error numbers.
    /// </summary>
    public static string ClassifyError(Exception? ex)
    {
        if (ex is null)
            return ErrorTypes.Unknown;

        if (ex is System.Text.Json.JsonException)
            return ErrorTypes.Serialization;

        if (ex is DbUpdateException dbEx)
        {
            var inner = dbEx.InnerException;
            if (inner is not null)
            {
                var typeName = inner.GetType().Name;
                if (typeName == "SqlException")
                {
                    var errorNumber = GetSqlErrorNumber(inner);
                    return errorNumber switch
                    {
                        1205 => ErrorTypes.Deadlock,
                        -2 => ErrorTypes.Timeout,
                        _ => ErrorTypes.Constraint
                    };
                }

                if (typeName.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                    inner.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                    return ErrorTypes.Timeout;

                if (typeName.Contains("Constraint", StringComparison.OrdinalIgnoreCase) ||
                    inner.Message.Contains("constraint", StringComparison.OrdinalIgnoreCase) ||
                    inner.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                    inner.Message.Contains("unique", StringComparison.OrdinalIgnoreCase))
                    return ErrorTypes.Constraint;
            }

            return ErrorTypes.Constraint;
        }

        if (ex.GetType().Name.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return ErrorTypes.Timeout;

        return ErrorTypes.Unknown;
    }

    private static int GetSqlErrorNumber(Exception ex)
    {
        var numberProp = ex.GetType().GetProperty("Number");
        if (numberProp is not null)
        {
            var value = numberProp.GetValue(ex);
            if (value is int number)
                return number;
        }
        return 0;
    }

    #endregion
}
