namespace MillWorks.AuditCore.Services.Query;

public static class QueryLimits
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;

    /// <summary>
    /// Maximum number of distinct values returned by GetDistinct* methods.
    /// Prevents unbounded table scans on large audit tables.
    /// </summary>
    public const int MaxDistinctValues = 500;

    /// <summary>
    /// Minimum search term length for wildcard searches.
    /// Short terms with leading wildcards cause expensive table scans.
    /// </summary>
    public const int MinSearchTermLength = 3;

    /// <summary>
    /// Maximum rows per outbox batch insert.
    /// SQL Server has a 2100 parameter limit; with 6 params per row, max is 350.
    /// Use 300 to leave margin for future columns.
    /// </summary>
    public const int MaxOutboxBatchSize = 300;

    public static int Clamp(int limit) =>
        limit <= 0 ? DefaultPageSize : Math.Min(limit, MaxPageSize);
}
