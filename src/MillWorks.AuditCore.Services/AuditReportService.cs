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
    AuditApplicationDbContext context,
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
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="groupBy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<AuditChartData>> GetAuditChartDataAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        string groupBy = "day",
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Generating audit chart data from {StartDate} to {EndDate} grouped by {GroupBy}",
            startDate, endDate, groupBy);

        var baseQuery = context.AuditEvents.AsNoTracking()
            .Where(e => e.InsertedDate >= startDate && e.InsertedDate <= endDate);

        // User and event type groupings can be done entirely server-side
        switch (groupBy.ToLowerInvariant())
        {
            case "user":
                return await GroupByUserServerSideAsync(baseQuery, cancellationToken);
            case "eventtype":
                return await GroupByEventTypeServerSideAsync(baseQuery, cancellationToken);
            default:
            {
                // For date-based groupings, project only the InsertedDate column
                var dates = await baseQuery
                    .Where(static e => e.InsertedDate.HasValue)
                    .Select(static e => e.InsertedDate!.Value)
                    .ToListAsync(cancellationToken);

                return groupBy.ToLowerInvariant() switch
                {
                    "hour" => GroupByHour(dates),
                    "week" => GroupByWeek(dates),
                    "month" => GroupByMonth(dates),
                    _ => GroupByDay(dates)
                };
            }
        }
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
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="format"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<byte[]> GenerateAuditReportAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        string format = "pdf",
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning("GenerateAuditReportAsync not yet implemented for format {Format}", format);

        AuditSummaryResponse summary = await GetAuditSummaryAsync(startDate, endDate, cancellationToken);

        // For now, return a simple text representation
        string report = $"Audit Report\n" +
                        $"Period: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}\n" +
                        $"Total Events: {summary.TotalEvents}\n" +
                        $"Unique Users: {summary.UniqueUsers}\n";

        return System.Text.Encoding.UTF8.GetBytes(report);
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
            .Select(g => new AuditChartData
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