using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Query;

/// <summary>
/// Audit report service for generating summaries and reports based on audit events.
/// </summary>
/// <param name="context"></param>
/// <param name="logger"></param>
public sealed class AuditReportService(
    AuditDbContext context,
    ILogger<AuditReportService> logger)
    : IAuditReportService
{
    /// <summary>
    /// Gets the audit summary for the specified date range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditSummaryResponse> GetAuditSummaryAsync(
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Generating audit summary for period {StartDate} to {EndDate}",
            startDate?.ToString("yyyy-MM-dd") ?? "beginning",
            endDate?.ToString("yyyy-MM-dd") ?? "now");

        var query = context.AuditEvents.AsNoTracking();

        if (startDate.HasValue)
            query = query.Where(e => e.InsertedDate >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(e => e.InsertedDate <= endDate.Value);

        int totalEvents = await query.CountAsync(cancellationToken);
        int uniqueUsers = await query
            .Where(static e => e.User != null)
            .Select(static e => e.User)
            .Distinct()
            .CountAsync(cancellationToken);

        var eventTypes = await GetEventTypeDistributionAsync(startDate, endDate, cancellationToken);
        var topUsers = await GetTopUsersAsync(10, startDate, endDate, cancellationToken);

        return new AuditSummaryResponse
        {
            TotalEvents = totalEvents,
            UniqueUsers = uniqueUsers,
            EventTypes = eventTypes,
            TopUsers = topUsers,
            StartDate = startDate,
            EndDate = endDate
        };
    }

    /// <summary>
    /// Gets the audit chart data for the specified date range and grouping.
    /// Response includes truncation metadata when results exceed query limits.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="groupBy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditChartDataResponse> GetAuditChartDataAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        string groupBy = "day",
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Generating audit chart data from {StartDate} to {EndDate} grouped by {GroupBy}",
            startDate, endDate, groupBy);

        var baseQuery = context.AuditEvents.AsNoTracking()
            .Where(e => e.InsertedDate >= startDate && e.InsertedDate <= endDate);

        List<AuditChartData> items;
        bool isTruncated = false;

        switch (groupBy.ToLowerInvariant())
        {
            case "user":
                items = await GroupByUserServerSideAsync(baseQuery, cancellationToken);
                break;
            case "eventtype":
                items = await GroupByEventTypeServerSideAsync(baseQuery, cancellationToken);
                break;
            default:
            {
                int totalCount = await baseQuery
                    .Where(static e => e.InsertedDate.HasValue)
                    .CountAsync(cancellationToken);

                isTruncated = totalCount > QueryLimits.MaxChartDataRows;
                if (isTruncated)
                {
                    logger.LogWarning(
                        "Chart data truncated: {Total} records exceed limit of {Max}",
                        totalCount, QueryLimits.MaxChartDataRows);
                }

                var dates = await baseQuery
                    .Where(static e => e.InsertedDate.HasValue)
                    .OrderByDescending(static e => e.InsertedDate)
                    .Take(QueryLimits.MaxChartDataRows)
                    .Select(static e => e.InsertedDate!.Value)
                    .ToListAsync(cancellationToken);

                items = groupBy.ToLowerInvariant() switch
                {
                    "hour" => GroupByHour(dates),
                    "week" => GroupByWeek(dates),
                    "month" => GroupByMonth(dates),
                    _ => GroupByDay(dates)
                };
                break;
            }
        }

        return new AuditChartDataResponse
        {
            Items = items,
            IsTruncated = isTruncated,
            TruncatedAt = isTruncated ? QueryLimits.MaxChartDataRows : null
        };
    }

    /// <summary>
    /// Gets the audit summary for the specified date range.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<Dictionary<string, int>> GetActivitySummaryAsync(
        Guid? userId = null,
        DateTimeOffset? fromDate = null,
        CancellationToken cancellationToken = default)
    {
        var query = context.AuditEvents.AsNoTracking();

        if (userId.HasValue)
            query = query.Where(e => e.UserId == userId.Value);

        if (fromDate.HasValue)
            query = query.Where(e => e.InsertedDate >= fromDate.Value);

        var summary = await query
            .Where(static e => e.EventType != null)
            .GroupBy(static e => e.EventType)
            .Select(static g => new { EventType = g.Key, Count = g.Count() })
            .ToDictionaryAsync(static x => x.EventType!, static x => x.Count, cancellationToken);

        return summary;
    }

    /// <summary>
    /// Gets the distribution of audit event types within the specified date range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<AuditEventTypeCount>> GetEventTypeDistributionAsync(
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default)
    {
        var query = context.AuditEvents.AsNoTracking();

        if (startDate.HasValue)
            query = query.Where(e => e.InsertedDate >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(e => e.InsertedDate <= endDate.Value);

        return await query
            .Where(static e => e.EventType != null)
            .GroupBy(static e => e.EventType)
            .Select(static g => new AuditEventTypeCount
            {
                EventType = g.Key ?? "Unknown",
                Count = g.Count()
            })
            .OrderByDescending(static x => x.Count)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the top users based on audit activity within the specified date range.
    /// </summary>
    /// <param name="count"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<AuditUserCount>> GetTopUsersAsync(
        int count = 10,
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default)
    {
        var query = context.AuditEvents.AsNoTracking();

        if (startDate.HasValue)
            query = query.Where(e => e.InsertedDate >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(e => e.InsertedDate <= endDate.Value);

        return await query
            .Where(static e => e.User != null)
            .GroupBy(static e => e.User)
            .Select(static g => new AuditUserCount
            {
                User = g.Key ?? "Unknown",
                Count = g.Count()
            })
            .OrderByDescending(static x => x.Count)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Generates an audit report for the specified date range and format.
    /// Response includes truncation metadata when results exceed export limits.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="format"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditReportResponse> GenerateAuditReportAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        string format = "json",
        CancellationToken cancellationToken = default)
    {
        AuditSummaryResponse summary = await GetAuditSummaryAsync(startDate, endDate, cancellationToken);

        return format.ToLowerInvariant() switch
        {
            "json" => new AuditReportResponse
            {
                Content = GenerateJsonReport(summary, startDate, endDate),
                Format = "json",
                IsTruncated = false,
                TotalRecords = summary.TotalEvents
            },
            "csv" => await GenerateCsvReportWithMetadataAsync(startDate, endDate, cancellationToken),
            _ => throw new NotSupportedException(
                $"Report format '{format}' is not supported. Supported formats: json, csv.")
        };
    }

    private static byte[] GenerateJsonReport(
        AuditSummaryResponse summary,
        DateTimeOffset startDate,
        DateTimeOffset endDate)
    {
        var report = new
        {
            summary.TotalEvents,
            summary.UniqueUsers,
            Period = new { Start = startDate, End = endDate },
            summary.EventTypes,
            summary.TopUsers
        };

        string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });

        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    private async Task<AuditReportResponse> GenerateCsvReportWithMetadataAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken)
    {
        var baseQuery = context.AuditEvents
            .AsNoTracking()
            .Where(e => e.InsertedDate >= startDate && e.InsertedDate <= endDate);

        int totalCount = await baseQuery.CountAsync(cancellationToken);
        bool isTruncated = totalCount > QueryLimits.MaxExportRows;

        if (isTruncated)
        {
            logger.LogWarning(
                "CSV export truncated: {Total} records exceed limit of {Max}",
                totalCount, QueryLimits.MaxExportRows);
        }

        var query = baseQuery
            .OrderBy(static e => e.InsertedDate)
            .Take(QueryLimits.MaxExportRows);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("EventId,InsertedDate,EventType,EntityType,EntityId,Action,User,UserFullName,Environment");

        await foreach (var e in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            sb.AppendLine(string.Join(",",
                CsvEscape(e.EventId.ToString()),
                CsvEscape(e.InsertedDate?.ToString("o")),
                CsvEscape(e.EventType),
                CsvEscape(e.EntityType),
                CsvEscape(e.EntityId),
                CsvEscape(e.Action),
                CsvEscape(e.User),
                CsvEscape(e.UserFullName),
                CsvEscape(e.Environment)));
        }

        return new AuditReportResponse
        {
            Content = System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
            Format = "csv",
            IsTruncated = isTruncated,
            TruncatedAt = isTruncated ? QueryLimits.MaxExportRows : null,
            TotalRecords = totalCount
        };
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Guard against CSV formula injection — spreadsheet apps treat cells starting
        // with =, +, -, @, tab, or carriage return as formulas or control characters.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\''))
            return $"\"{value.Replace("\"", "\"\"")}\"";

        return value;
    }

    /// <summary>
    /// Groups dates by hour.
    /// </summary>
    private static List<AuditChartData> GroupByHour(List<DateTimeOffset> dates)
    {
        return dates
            .GroupBy(static d => new { d.Date, d.Hour })
            .Select(static g => new AuditChartData
            {
                Date = g.Key.Date.AddHours(g.Key.Hour),
                Label = g.Key.Date.AddHours(g.Key.Hour).ToString("yyyy-MM-dd HH:00"),
                Count = g.Count()
            })
            .OrderBy(static x => x.Date)
            .ToList();
    }

    /// <summary>
    /// Groups dates by day.
    /// </summary>
    private static List<AuditChartData> GroupByDay(List<DateTimeOffset> dates)
    {
        return dates
            .GroupBy(static d => d.Date)
            .Select(static g => new AuditChartData
            {
                Date = g.Key,
                Label = g.Key.ToString("yyyy-MM-dd"),
                Count = g.Count()
            })
            .OrderBy(static x => x.Date)
            .ToList();
    }

    /// <summary>
    /// Groups dates by week.
    /// </summary>
    private List<AuditChartData> GroupByWeek(List<DateTimeOffset> dates)
    {
        Calendar calendar = CultureInfo.InvariantCulture.Calendar;

        return dates
            .GroupBy(d => new
            {
                d.Year,
                Week = calendar.GetWeekOfYear(d.DateTime, CalendarWeekRule.FirstFourDayWeek,
                    DayOfWeek.Monday)
            })
            .Select(static g => new AuditChartData
            {
                Date = GetFirstDayOfWeek(g.Key.Year, g.Key.Week),
                Label = $"Week {g.Key.Week}, {g.Key.Year}",
                Count = g.Count()
            })
            .OrderBy(static x => x.Date)
            .ToList();
    }

    /// <summary>
    /// Groups dates by month.
    /// </summary>
    private static List<AuditChartData> GroupByMonth(List<DateTimeOffset> dates)
    {
        return dates
            .GroupBy(static d => new { d.Year, d.Month })
            .Select(static g =>
            {
                var date = new DateTimeOffset(g.Key.Year, g.Key.Month, 1, 0, 0, 0, TimeSpan.Zero);
                return new AuditChartData
                {
                    Date = date,
                    Label = date.ToString("MMM yyyy"),
                    Count = g.Count()
                };
            })
            .OrderBy(static x => x.Date)
            .ToList();
    }

    /// <summary>
    /// Groups by user server-side.
    /// </summary>
    private static async Task<List<AuditChartData>> GroupByUserServerSideAsync(
        IQueryable<AuditEventEntity> query, CancellationToken cancellationToken)
    {
        return await query
            .Where(static e => e.User != null && e.User != "")
            .GroupBy(static e => e.User)
            .Select(static g => new AuditChartData
            {
                Label = g.Key ?? "Unknown",
                User = g.Key,
                Count = g.Count()
            })
            .OrderByDescending(static x => x.Count)
            .Take(20)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Groups by event type server-side.
    /// </summary>
    private static async Task<List<AuditChartData>> GroupByEventTypeServerSideAsync(
        IQueryable<AuditEventEntity> query, CancellationToken cancellationToken)
    {
        return await query
            .Where(static e => e.EventType != null && e.EventType != "")
            .GroupBy(static e => e.EventType)
            .Select(static g => new AuditChartData
            {
                Label = g.Key ?? "Unknown",
                EventType = g.Key,
                Count = g.Count()
            })
            .OrderByDescending(static x => x.Count)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the first day of the specified week number in a year.
    /// </summary>
    /// <param name="year"></param>
    /// <param name="weekNumber"></param>
    /// <returns></returns>
    private static DateTimeOffset GetFirstDayOfWeek(int year, int weekNumber)
    {
        DateTimeOffset jan1 = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
        int daysOffset = DayOfWeek.Monday - jan1.DayOfWeek;

        if (daysOffset > 0) daysOffset -= 7;

        DateTimeOffset firstMonday = jan1.AddDays(daysOffset);
        return firstMonday.AddDays(7 * (weekNumber - 1));
    }
}
