namespace MillWorks.AuditCore.Services.Query;

public static class QueryLimits
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;

    public static int Clamp(int limit) =>
        limit <= 0 ? DefaultPageSize : Math.Min(limit, MaxPageSize);
}
