using System.Text.Json;
using MapsterMapper;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.EntityFramework.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// AuditService provides methods to retrieve audit logs and summaries of user and project activities.
/// </summary>
/// <param name="auditLogRepository"></param>
/// <param name="auditEventRepository"></param>
/// <param name="mapper"></param>
/// <param name="logger"></param>
public sealed class AuditService(
    IAuditLogRepository auditLogRepository,
    IAuditEventRepository auditEventRepository,
    IMapper mapper,
    ILogger<AuditService> logger)
    : IAuditService
{
    /// <summary>
    /// Gets the audit trail for a specific entity by its name and ID.
    /// </summary>
    /// <param name="entityName"></param>
    /// <param name="entityId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditLogDto>> GetEntityAuditTrailAsync(string entityName, Guid entityId,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<AuditLogEntity> auditLogs =
            await auditLogRepository.GetEntityAuditTrailAsync(entityName, entityId, cancellationToken: cancellationToken);
        return mapper.Map<IEnumerable<AuditLogDto>>(auditLogs);
    }

    /// <summary>
    /// Gets the activity logs for a specific user, optionally filtered by date and limited to a specified number of entries.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="take"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditLogDto>> GetUserActivityAsync(Guid userId, DateTimeOffset? fromDate = null,
        int take = 50, CancellationToken cancellationToken = default)
    {
        IEnumerable<AuditLogEntity> auditLogs =
            await auditLogRepository.GetUserActivityAsync(userId, fromDate, Math.Max(take, 1), cancellationToken);
        return mapper.Map<IEnumerable<AuditLogDto>>(auditLogs);
    }


    /// <summary>
    /// Gets a summary of user activity, including the number of actions performed by each user since a specified date.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<Dictionary<string, int>> GetActivitySummaryAsync(Guid? userId = null,
        DateTimeOffset? fromDate = null,
        CancellationToken cancellationToken = default)
    {
        var counts = await auditLogRepository.GetActionCountsAsync(userId, fromDate, cancellationToken);
        return counts.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);
    }

    /// <summary>
    ///     Get Audit Event By Id
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventDto?> GetAuditEventById(Guid eventId, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Getting audit event with ID: {EventId}", eventId);

        try
        {
            AuditEventEntity? auditEvent = await auditEventRepository.GetByIdAsync(eventId, cancellationToken);

            if (auditEvent == null)
            {
                logger.LogWarning("Audit event with ID {EventId} not found", eventId);
                return null;
            }

            AuditEventDto result = mapper.Map<AuditEventDto>(auditEvent);
            result.Data = auditEvent.JsonData != null
                ? JsonSerializer.Deserialize<AuditEventJsonDataParsedResponse>(auditEvent.JsonData)
                : null;

            logger.LogDebug("Successfully retrieved audit event {EventId}", eventId);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving audit event with ID {EventId}", eventId);
            throw;
        }
    }

    /// <summary>
    ///     Get Audit Events
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventsResponse> GetAuditEvents(int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Getting audit events with offset {Offset} and limit {Limit}", offset, limit);

        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        try
        {
            var (events, totalCount) = await auditEventRepository.GetPagedAsync(
                pageNumber: (offset / limit) + 1,
                pageSize: limit,
                orderBy: static q => q.OrderByDescending(static x => x.InsertedDate));

            var eventsList = events.ToList();
            int totalPages = (int)Math.Ceiling((double)totalCount / limit);

            List<AuditEventDto> mapped = mapper.Map<List<AuditEventDto>>(eventsList);
            mapped.ForEach(static x =>
                x.Data = x.JsonData != null
                    ? JsonSerializer.Deserialize<AuditEventJsonDataParsedResponse>(x.JsonData)
                    : null);

            logger.LogDebug("Retrieved {Count} audit events", mapped.Count);

            return new AuditEventsResponse
            {
                TotalItems = totalCount,
                Items = mapped,
                Offset = offset,
                Limit = limit,
                TotalPages = totalPages,
                CurrentPage = (offset / limit) + 1
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving audit events with offset {Offset} and limit {Limit}", offset, limit);
            throw;
        }
    }

    /// <summary>
    ///     Search Audit Events
    /// </summary>
    /// <param name="request">Search parameters</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Filtered audit events</returns>
    public async Task<AuditEventsResponse> SearchAuditEvents(AuditSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);

        logger.LogDebug("Searching audit events with EventType={EventType}, User={User}",
            request.EventType, request.User);

        try
        {
            // Build predicate for filtering
            System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>> predicate = static x => true;

            if (request.StartDate.HasValue)
            {
                var startDate = request.StartDate.Value;
                predicate = CombinePredicates(predicate, x => x.InsertedDate >= startDate);
            }

            if (request.EndDate.HasValue)
            {
                var endDate = request.EndDate.Value;
                predicate = CombinePredicates(predicate, x => x.InsertedDate <= endDate);
            }

            if (!string.IsNullOrWhiteSpace(request.User))
            {
                var user = request.User;
                predicate = CombinePredicates(predicate, x => x.User == user);
            }

            if (!string.IsNullOrWhiteSpace(request.EventType))
            {
                var eventType = request.EventType;
                predicate = CombinePredicates(predicate, x => x.EventType == eventType);
            }

            if (!string.IsNullOrWhiteSpace(request.SearchTerm))
            {
                var searchTerm = request.SearchTerm;
                predicate = CombinePredicates(predicate, x =>
                    (x.JsonData != null && x.JsonData.Contains(searchTerm)) ||
                    (x.User != null && x.User.Contains(searchTerm)) ||
                    (x.EventType != null && x.EventType.Contains(searchTerm)));
            }

            var (events, totalCount) = await auditEventRepository.GetPagedAsync(
                pageNumber: (request.Offset / request.Limit) + 1,
                pageSize: request.Limit,
                predicate: predicate,
                orderBy: static q => q.OrderByDescending(static x => x.InsertedDate));

            var eventsList = events.ToList();
            int totalPages = (int)Math.Ceiling((double)totalCount / request.Limit);

            List<AuditEventDto> mapped = mapper.Map<List<AuditEventDto>>(eventsList);
            mapped.ForEach(static x =>
                x.Data = x.JsonData != null
                    ? JsonSerializer.Deserialize<AuditEventJsonDataParsedResponse>(x.JsonData)
                    : null);

            logger.LogDebug("Search returned {Count} audit events", mapped.Count);

            return new AuditEventsResponse
            {
                TotalItems = totalCount,
                Items = mapped,
                Offset = request.Offset,
                Limit = request.Limit,
                TotalPages = totalPages,
                CurrentPage = (request.Offset / request.Limit) + 1
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error searching audit events");
            throw;
        }
    }

    /// <summary>
    ///     Get Audit Events By Date Range
    /// </summary>
    /// <param name="startDate">Start date</param>
    /// <param name="endDate">End date</param>
    /// <param name="offset">Pagination offset</param>
    /// <param name="limit">Pagination limit</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Filtered audit events</returns>
    public async Task<AuditEventsResponse> GetAuditEventsByDateRange(DateTimeOffset startDate, DateTimeOffset endDate,
        int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Getting audit events by date range: {StartDate} to {EndDate}", startDate, endDate);

        AuditSearchRequest request = new()
        {
            StartDate = startDate,
            EndDate = endDate,
            Offset = offset,
            Limit = limit
        };

        return await SearchAuditEvents(request, cancellationToken);
    }

    /// <summary>
    ///     Get Audit Events By User
    /// </summary>
    /// <param name="username">Username</param>
    /// <param name="offset">Pagination offset</param>
    /// <param name="limit">Pagination limit</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Filtered audit events</returns>
    public async Task<AuditEventsResponse> GetAuditEventsByUser(string username, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Getting audit events by user: {Username}", username);

        AuditSearchRequest request = new()
        {
            User = username,
            Offset = offset,
            Limit = limit
        };

        return await SearchAuditEvents(request, cancellationToken);
    }

    /// <summary>
    ///     Get Audit Summary
    /// </summary>
    /// <param name="startDate">Optional start date filter</param>
    /// <param name="endDate">Optional end date filter</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Summary statistics</returns>
    public async Task<AuditSummaryResponse> GetAuditSummary(DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Getting audit summary for date range: {StartDate} to {EndDate}",
            startDate?.ToString() ?? "all time", endDate?.ToString() ?? "present");

        try
        {
            // Build predicate for date filtering
            System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>> datePredicate = static x => true;

            if (startDate.HasValue)
            {
                var start = startDate.Value;
                datePredicate = CombinePredicates(datePredicate, x => x.InsertedDate >= start);
            }

            if (endDate.HasValue)
            {
                var end = endDate.Value;
                datePredicate = CombinePredicates(datePredicate, x => x.InsertedDate <= end);
            }

            // All queries execute server-side — no bulk loading into memory
            int totalEvents = await auditEventRepository.CountAsync(datePredicate, cancellationToken);
            int uniqueUsers = await auditEventRepository.GetUniqueUserCountAsync(datePredicate, cancellationToken);

            var eventTypeKvps = await auditEventRepository.GetEventTypeCountsAsync(datePredicate, cancellationToken);
            List<AuditEventTypeCount> eventTypeCounts = eventTypeKvps
                .Select(static kvp => new AuditEventTypeCount { EventType = kvp.Key, Count = kvp.Value })
                .ToList();

            var topUserKvps = await auditEventRepository.GetTopUsersByActivityAsync(10, datePredicate, cancellationToken);
            List<AuditUserCount> topUsers = topUserKvps
                .Select(static kvp => new AuditUserCount { User = kvp.Key, Count = kvp.Value })
                .ToList();

            logger.LogDebug("Generated audit summary with {EventTypes} event types and {TopUsers} top users",
                eventTypeCounts.Count, topUsers.Count);

            return new AuditSummaryResponse
            {
                TotalEvents = totalEvents,
                UniqueUsers = uniqueUsers,
                EventTypes = eventTypeCounts,
                TopUsers = topUsers,
                StartDate = startDate,
                EndDate = endDate
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating audit summary");
            throw;
        }
    }

    /// <summary>
    ///     Get Audit Chart Data
    /// </summary>
    /// <param name="startDate">Start date</param>
    /// <param name="endDate">End date</param>
    /// <param name="groupBy">Grouping interval (day, week, month)</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Data for charts</returns>
    public async Task<List<AuditChartData>> GetAuditChartData(DateTimeOffset startDate, DateTimeOffset endDate,
        string groupBy = "day", CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Getting audit chart data from {StartDate} to {EndDate} grouped by {GroupBy}",
            startDate, endDate, groupBy);

        try
        {
            List<AuditChartData> result;

            if (string.Equals(groupBy, "user", StringComparison.OrdinalIgnoreCase))
            {
                // Server-side aggregation by user + event type
                var userCounts =
                    await auditEventRepository.GetUserEventCountsAsync(startDate, endDate, 20, cancellationToken);
                result = userCounts
                    .Select(r => new AuditChartData
                    {
                        Date = startDate,
                        Label = r.User,
                        Count = r.Count,
                        User = r.User,
                        EventType = r.EventType
                    })
                    .ToList();
            }
            else
            {
                // Server-side daily aggregation, then re-aggregate client-side for week/month
                var dailyCounts =
                    await auditEventRepository.GetDailyEventCountsAsync(startDate, endDate, cancellationToken);

                result = groupBy.ToLowerInvariant() switch
                {
                    "week" => dailyCounts
                        .GroupBy(static d => new
                        {
                            d.Date.Year,
                            Week = GetIsoWeekNumber(d.Date.DateTime),
                            d.EventType
                        })
                        .Select(static g => new AuditChartData
                        {
                            Date = GetFirstDayOfWeek(g.Key.Year, g.Key.Week),
                            Label = $"Week {g.Key.Week}, {g.Key.Year}",
                            Count = g.Sum(static x => x.Count),
                            EventType = g.Key.EventType
                        })
                        .OrderBy(static x => x.Date)
                        .ToList(),
                    "month" => dailyCounts
                        .GroupBy(static d => new { d.Date.Year, d.Date.Month, d.EventType })
                        .Select(static g => new AuditChartData
                        {
                            Date = new DateTime(g.Key.Year, g.Key.Month, 1),
                            Label = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("MMM yyyy"),
                            Count = g.Sum(static x => x.Count),
                            EventType = g.Key.EventType
                        })
                        .OrderBy(static x => x.Date)
                        .ToList(),
                    _ => dailyCounts
                        .Select(static d => new AuditChartData
                        {
                            Date = d.Date,
                            Label = d.Date.ToString("yyyy-MM-dd"),
                            Count = d.Count,
                            EventType = d.EventType
                        })
                        .ToList()
                };
            }

            logger.LogDebug("Generated {Count} chart data points", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating audit chart data");
            throw;
        }
    }

    /// <summary>
    /// Helper method to get ISO week number
    /// </summary>
    private static int GetIsoWeekNumber(DateTime date)
    {
        return System.Globalization.CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(
            date,
            System.Globalization.CalendarWeekRule.FirstFourDayWeek,
            DayOfWeek.Monday);
    }

    /// <summary>
    /// Helper method to get the first day of a week
    /// </summary>
    private static DateTimeOffset GetFirstDayOfWeek(int year, int weekNumber)
    {
        // Find the first day of the year
        DateTimeOffset jan1 = new DateTime(year, 1, 1);

        // Get the day of week for Jan 1
        int daysOffset = DayOfWeek.Monday - jan1.DayOfWeek;

        // If the first day of the year is later than Monday, go to the previous week
        if (daysOffset > 0) daysOffset -= 7;

        // Get the first Monday of the year
        DateTimeOffset firstMonday = jan1.AddDays(daysOffset);

        // Add the weeks
        return firstMonday.AddDays(7 * (weekNumber - 1));
    }

    /// <summary>
    ///     Get Distinct Users
    /// </summary>
    /// <returns>List of unique usernames</returns>
    public async Task<List<string?>> GetDistinctUsers(CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Getting distinct users from audit logs");

        try
        {
            var users = await auditEventRepository.GetDistinctUsersAsync(cancellationToken);

            logger.LogDebug("Found {Count} distinct users", users.Count);
            return users.ConvertAll<string?>(static u => u);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving distinct users");
            throw;
        }
    }

    /// <summary>
    ///     Get Distinct Event Types
    /// </summary>
    /// <returns>List of unique event types</returns>
    public async Task<List<string?>> GetDistinctEventTypes(CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Getting distinct event types from audit logs");

        try
        {
            // Repository already returns results sorted alphabetically
            var eventTypes = await auditEventRepository.GetDistinctEventTypesAsync(cancellationToken);

            logger.LogDebug("Found {Count} distinct event types", eventTypes.Count);
            return eventTypes.ConvertAll<string?>(static x => x);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving distinct event types");
            throw;
        }
    }

    /// <summary>
    /// Logs a security event with high-risk content and a warning message.
    /// </summary>
    /// <param name="highRiskContent"></param>
    /// <param name="warning"></param>
    /// <param name="user"></param>
    /// <param name="details"></param>
    /// <param name="additionalInfo"></param>
    /// <param name="cancellationToken"></param>
    public async Task LogSecurityEventAsync(string highRiskContent, string warning, string user, object details,
        object additionalInfo, CancellationToken cancellationToken)
    {
        logger.LogInformation("Logging security event for user {User}", user);

        try
        {
            AuditEventEntity auditEvent = new()
            {
                EventType = "Security",
                User = user,
                JsonData = JsonSerializer.Serialize(new
                {
                    HighRiskContent = highRiskContent,
                    Warning = warning,
                    Details = details,
                    AdditionalInfo = additionalInfo
                }),
                InsertedDate = DateTimeOffset.UtcNow
            };

            await auditEventRepository.AddAsync(auditEvent, cancellationToken);
            await auditEventRepository.SaveChangesAsync(cancellationToken);

            logger.LogDebug("Successfully logged security event for user {User}", user);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error logging security event for user {User}", user);
            throw;
        }
    }

    /// <summary>
    /// Helper method to combine LINQ expressions with AND logic
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<T, bool>> CombinePredicates<T>(
        System.Linq.Expressions.Expression<Func<T, bool>> first,
        System.Linq.Expressions.Expression<Func<T, bool>> second)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(T));
        var firstBody = first.Body.Replace(first.Parameters[0], parameter);
        var secondBody = second.Body.Replace(second.Parameters[0], parameter);
        var combined = System.Linq.Expressions.Expression.AndAlso(firstBody, secondBody);
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(combined, parameter);
    }
}

/// <summary>
/// Extension method for replacing parameters in expressions
/// </summary>
public static class ExpressionExtensions
{
    /// <summary>
    /// Replaces occurrences of one expression with another in the given expression tree.
    /// </summary>
    /// <param name="expression"></param>
    /// <param name="searchEx"></param>
    /// <param name="replaceEx"></param>
    /// <returns></returns>
    public static System.Linq.Expressions.Expression Replace(this System.Linq.Expressions.Expression expression,
        System.Linq.Expressions.Expression searchEx, System.Linq.Expressions.Expression replaceEx)
    {
        return new ReplaceVisitor(searchEx, replaceEx).Visit(expression) ?? expression;
    }
}

/// <summary>
/// Visitor class for replacing expressions
/// </summary>
internal sealed class ReplaceVisitor(System.Linq.Expressions.Expression from, System.Linq.Expressions.Expression to)
    : System.Linq.Expressions.ExpressionVisitor
{
    /// <summary>
    /// Visits the expression and replaces occurrences of 'from' with 'to'.
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    public override System.Linq.Expressions.Expression? Visit(System.Linq.Expressions.Expression? node)
    {
        return node == from ? to : base.Visit(node);
    }
}