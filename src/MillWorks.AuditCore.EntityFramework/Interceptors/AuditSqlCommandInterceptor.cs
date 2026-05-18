using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MillWorks.AuditCore.EntityFramework.Interceptors;

/// <summary>
/// EF Core command interceptor that emits SQL metrics for observability.
/// Classifies Azure SQL errors (throttling, connection pool, deadlock, transient)
/// and tracks command duration, slow queries, and error counts.
/// </summary>
public sealed class AuditSqlCommandInterceptor : DbCommandInterceptor
{
    private static readonly Meter Meter = new("MillWorks.AuditCore.Sql", "1.0.0");

    private static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "sql_command_duration_seconds",
        "seconds",
        "SQL command execution duration");

    private static readonly Counter<long> SqlErrors = Meter.CreateCounter<long>(
        "sql_errors_total",
        "errors",
        "SQL errors by category");

    private static readonly Counter<long> SqlRetries = Meter.CreateCounter<long>(
        "sql_retries_total",
        "retries",
        "SQL command retry attempts");

    private static readonly Counter<long> SlowCommands = Meter.CreateCounter<long>(
        "sql_slow_commands_total",
        "commands",
        "SQL commands exceeding 1 second threshold");

    private static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(1);

    // Azure SQL throttling error codes
    private static readonly HashSet<int> ThrottlingCodes =
    [
        10928, // Resource ID: %d. The %s limit for the database is %d and has been reached.
        10929, // Resource ID: %d. The %s minimum guarantee is %d, maximum limit is %d.
        40501, // The service is currently busy. Retry the request after 10 seconds.
        40544, // The database has reached its size quota.
        40549, // Session is terminated because you have a long-running transaction.
        40550, // The session has been terminated because it has acquired too many locks.
        40551, // The session has been terminated because of excessive TEMPDB usage.
        40552, // The session has been terminated because of excessive transaction log space usage.
        40553, // The session has been terminated because of excessive memory usage.
        49918, // Cannot process request. Not enough resources to process request.
        49919, // Cannot process create or update request. Too many create or update operations in progress.
        49920  // Cannot process request. Too many operations in progress.
    ];

    // Connection pool / connection errors
    private static readonly HashSet<int> ConnectionPoolCodes =
    [
        -2,    // Timeout expired (connection pool exhaustion often manifests as timeout)
        233,   // Connection initialization error
        10053, // Connection forcibly closed
        10054, // Connection reset by peer
        10060, // Connection timed out
        40143, // Connection could not be initialized
        40197, // The service has encountered an error processing your request
        40613  // Database is currently unavailable
    ];

    // Deadlock
    private const int DeadlockCode = 1205;

    // Transient errors that typically succeed on retry
    private static readonly HashSet<int> TransientCodes =
    [
        -1,    // General network error
        2,     // Timeout
        53,    // Network path not found
        121,   // Semaphore timeout
        1232,  // Network error
        4060,  // Cannot open database (may be transient during failover)
        4221,  // Login to read-secondary failed
        18456  // Login failed (may be transient during AAD token refresh)
    ];

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        RecordSuccess(eventData, "reader");
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        RecordSuccess(eventData, "reader");
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result)
    {
        RecordSuccess(eventData, "nonquery");
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        RecordSuccess(eventData, "nonquery");
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result)
    {
        RecordSuccess(eventData, "scalar");
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        RecordSuccess(eventData, "scalar");
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override void CommandFailed(
        DbCommand command,
        CommandErrorEventData eventData)
    {
        RecordFailure(eventData, "reader");
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        RecordFailure(eventData, "reader");
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    private static void RecordSuccess(CommandExecutedEventData eventData, string operation)
    {
        var duration = eventData.Duration.TotalSeconds;
        var tags = new TagList
        {
            { "operation", operation },
            { "outcome", "success" }
        };

        CommandDuration.Record(duration, tags);

        if (eventData.Duration > SlowThreshold)
        {
            SlowCommands.Add(1, new TagList { { "operation", operation } });
        }
    }

    private static void RecordFailure(CommandErrorEventData eventData, string operation)
    {
        var duration = eventData.Duration.TotalSeconds;
        var category = ClassifyError(eventData.Exception);

        CommandDuration.Record(duration, new TagList
        {
            { "operation", operation },
            { "outcome", "failure" }
        });

        SqlErrors.Add(1, new TagList { { "category", category } });
    }

    /// <summary>
    /// Records a retry attempt. Call this from retry logic (e.g., Polly handler).
    /// </summary>
    public static void RecordRetry(string? operation = null)
    {
        SqlRetries.Add(1, new TagList { { "operation", operation ?? "unknown" } });
    }

    /// <summary>
    /// Classifies a SQL exception into a category for metrics tagging.
    /// </summary>
    public static string ClassifyError(Exception? exception)
    {
        if (exception is SqlException sqlEx)
        {
            foreach (SqlError error in sqlEx.Errors)
            {
                if (error.Number == DeadlockCode)
                    return "deadlock";

                if (ThrottlingCodes.Contains(error.Number))
                    return "throttling";

                if (ConnectionPoolCodes.Contains(error.Number))
                    return "connection_pool";

                if (TransientCodes.Contains(error.Number))
                    return "transient";
            }
        }

        if (exception is TimeoutException)
            return "timeout";

        if (exception is InvalidOperationException &&
            exception.Message.Contains("pool", StringComparison.OrdinalIgnoreCase))
            return "connection_pool";

        return "other";
    }

    /// <summary>
    /// Determines if an exception is likely transient and should be retried.
    /// </summary>
    public static bool IsTransient(Exception? exception)
    {
        var category = ClassifyError(exception);
        return category is "transient" or "throttling" or "deadlock" or "connection_pool" or "timeout";
    }
}
