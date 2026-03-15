using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// AuditComplianceService provides methods for generating compliance reports,
/// </summary>
public sealed class AuditComplianceService : IAuditComplianceService
{
    /// <summary>
    /// Audit event repository
    /// </summary>
    private readonly IAuditEventRepository _auditEventRepository;

    /// <summary>
    /// Audit archival service (optional — only available when UseArchival() is configured)
    /// </summary>
    private readonly IAuditArchivalService? _auditArchivalService;

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<AuditComplianceService> _logger;

    /// <summary>
    /// Configuration instance
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Validators for different compliance standards, built from DI-registered validators
    /// </summary>
    private readonly Dictionary<ComplianceStandard, IComplianceValidator> _validators;

    /// <summary>
    /// AuditComplianceService constructor
    /// </summary>
    public AuditComplianceService(
        IAuditEventRepository auditEventRepository,
        IEnumerable<IComplianceValidator> validators,
        ILogger<AuditComplianceService> logger,
        IConfiguration configuration,
        IAuditArchivalService? auditArchivalService = null)
    {
        _auditEventRepository = auditEventRepository;
        _auditArchivalService = auditArchivalService;
        _logger = logger;
        _configuration = configuration;

        _validators = validators.ToDictionary(static v => v.Standard);
    }

    /// <summary>
    /// Generates a compliance report for the specified standard and date range.
    /// </summary>
    /// <param name="standard"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException"></exception>
    public async Task<ComplianceReport> GenerateComplianceReportAsync(
        ComplianceStandard standard,
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        var report = new ComplianceReport
        {
            Standard = standard,
            GeneratedAt = DateTimeOffset.UtcNow,
            PeriodStart = startDate,
            PeriodEnd = endDate
        };

        if (!_validators.TryGetValue(standard, out var validator))
        {
            throw new NotSupportedException($"Compliance standard {standard} not supported");
        }

        // Fetch a bounded sample for validation (validators do in-memory LINQ checks)
        var events = await _auditEventRepository.GetByDateRangeAsync(startDate, endDate,
            maxResults: 5000, cancellationToken: cancellationToken);
        var eventsList = events.ToList();

        // Validate compliance
        report.ValidationResults = await validator.ValidateAsync(eventsList);
        report.IsCompliant = report.ValidationResults.All(static r => r.Passed);

        // Generate recommendations
        report.Recommendations = validator.GenerateRecommendations(report.ValidationResults);

        // Statistics via server-side counts (no bulk loading)
        report.Statistics = await GetComplianceStatisticsAsync(startDate, endDate,
            cancellationToken: cancellationToken);

        return report;
    }

    /// <summary>
    /// Anonymizes user data in audit events for the specified user ID.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AnonymizationResult> AnonymizeUserDataAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var result = new AnonymizationResult { UserId = userId };

        await using var transaction = await _auditEventRepository.BeginTransactionAsync(cancellationToken);
        try
        {
            // Use a capped limit instead of int.MaxValue to bound memory usage.
            // 100k is generous for a single user's audit trail; if exceeded, the
            // remaining records are not anonymized and a warning is logged.
            const int maxAnonymize = 100_000;
            var events = await _auditEventRepository.GetByUserIdAsync(userId,
                maxResults: maxAnonymize, cancellationToken: cancellationToken);
            var eventsList = events.ToList();

            result.EventCount = eventsList.Count;

            if (eventsList.Count == maxAnonymize)
            {
                _logger.LogWarning(
                    "User {UserId} has {MaxAnonymize}+ audit events; anonymization capped. " +
                    "Run again to process remaining records.",
                    userId, maxAnonymize);
            }

            foreach (var evt in eventsList)
            {
                evt.User = "ANONYMIZED";
                evt.UserFullName = "ANONYMIZED";
                evt.IpAddress = AnonymizeIpAddress(evt.IpAddress);
                evt.UserAgent = "ANONYMIZED";

                if (!string.IsNullOrEmpty(evt.JsonData))
                {
                    evt.JsonData = AnonymizeJsonData(evt.JsonData, userId);
                }
            }

            await _auditEventRepository.UpdateRangeAsync(eventsList, cancellationToken);
            await _auditEventRepository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            result.Success = true;
            result.Message = $"Successfully anonymized {eventsList.Count} audit events";
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Failed to anonymize user data for {UserId}", userId);
            result.Success = false;
            result.Message = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Exports audit data for a specific user as a JSON file.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditExportResult> ExportUserAuditDataAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var result = new AuditExportResult { UserId = userId };

        try
        {
            // Get events for user using repository (capped to prevent OOM on large datasets)
            const int maxExport = 100_000;
            var events = await _auditEventRepository.GetByUserIdAsync(userId,
                maxResults: maxExport, cancellationToken: cancellationToken);

            var exportData = events
                .OrderBy(static e => e.InsertedDate)
                .Select(static e => new
                {
                    e.EventId,
                    e.EventType,
                    e.InsertedDate,
                    e.EntityType,
                    e.EntityId,
                    e.Action,
                    Description = e.JsonData
                })
                .ToList();

            var jsonData = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            result.Data = System.Text.Encoding.UTF8.GetBytes(jsonData);
            result.FileName = $"audit-export-{userId}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json";
            result.Success = true;
            result.RecordCount = exportData.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export audit data for user {UserId}", userId);
            result.Success = false;
            result.Message = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Validates compliance with retention policies defined in the configuration.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<bool> ValidateRetentionComplianceAsync(CancellationToken cancellationToken = default)
    {
        var retentionPolicies = _configuration.GetSection("Audit:RetentionPolicies").Get<List<AuditRetentionPolicy>>();

        foreach (var policy in retentionPolicies ?? [])
        {
            var cutoffDate = DateTimeOffset.UtcNow.AddDays(-policy.RetentionDays);
            var eventType = policy.EventType;

            var violatingCount = await _auditEventRepository.CountAsync(
                e => e.EventType == eventType && e.InsertedDate < cutoffDate,
                cancellationToken);

            if (violatingCount > 0)
            {
                _logger.LogWarning("Retention policy violation: {Count} {EventType} events exceed {Days} day retention",
                    violatingCount, policy.EventType, policy.RetentionDays);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies retention policies by archiving or deleting old audit events based on configuration.
    /// </summary>
    /// <param name="cancellationToken"></param>
    public async Task ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default)
    {
        var retentionPolicies = _configuration.GetSection("Audit:RetentionPolicies")
            .Get<List<AuditRetentionPolicy>>();

        if (retentionPolicies == null || retentionPolicies.Count == 0)
        {
            _logger.LogInformation("No retention policies configured");
            return;
        }

        // Validate all policies first
        var validPolicies = new List<AuditRetentionPolicy>();
        foreach (var policy in retentionPolicies.Where(static p => p.IsActive))
        {
            if (policy.IsValid(out var errors))
            {
                validPolicies.Add(policy);
            }
            else
            {
                _logger.LogWarning(
                    "Skipping invalid retention policy {EventType}: {Errors}",
                    policy.EventType, string.Join(", ", errors));
            }
        }

        // Sort by priority (highest first)
        var sortedPolicies = validPolicies.OrderByDescending(static p => p.Priority).ToList();

        var results = new List<(string EventType, int Count, bool Success, string? Error)>();

        // Get all distinct event types from the database
        var allEventTypes = await _auditEventRepository.GetDistinctEventTypesAsync(cancellationToken);

        foreach (var policy in sortedPolicies)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Retention policy application cancelled");
                break;
            }

            try
            {
                var cutoffDate = DateTimeOffset.UtcNow.AddDays(-policy.RetentionDays);

                _logger.LogInformation(
                    "Applying retention policy for {EventType}: {Days} days (cutoff: {Cutoff}, priority: {Priority})",
                    policy.EventType, policy.RetentionDays, cutoffDate, policy.Priority);

                // Find all event types that match this policy
                var matchingEventTypes = allEventTypes
                    .Where(et => policy.MatchesEventType(et))
                    .ToList();

                if (matchingEventTypes.Count == 0)
                {
                    _logger.LogInformation(
                        "No matching event types found for policy pattern: {EventType}",
                        policy.EventType);
                    continue;
                }

                _logger.LogDebug(
                    "Policy {EventType} matches {Count} event types: {Types}",
                    policy.EventType, matchingEventTypes.Count, matchingEventTypes);

                foreach (var eventType in matchingEventTypes)
                {
                    if (policy.ArchiveBeforeDelete)
                    {
                        if (_auditArchivalService is null)
                        {
                            _logger.LogError(
                                "Retention policy {EventType} requires ArchiveBeforeDelete but archival is not configured. Call UseArchival() to enable archival.",
                                policy.EventType);
                            results.Add((eventType, 0, false, "Archival service not configured"));
                            continue;
                        }

                        // Archive before deleting
                        var archiveResult = await _auditArchivalService.ArchiveAuditEventsAsync(
                            cutoffDate,
                            null,
                            cancellationToken);

                        results.Add((
                            eventType,
                            archiveResult.EventCount,
                            archiveResult.Success,
                            archiveResult.Success ? null : archiveResult.Message));

                        if (!archiveResult.Success)
                        {
                            _logger.LogError(
                                "Failed to archive events for {EventType}: {Message}",
                                eventType, archiveResult.Message);
                            continue;
                        }

                        _logger.LogInformation(
                            "Archived {Count} {EventType} events per policy {PolicyType}",
                            archiveResult.EventCount, eventType, policy.EventType);
                    }
                    else
                    {
                        // Bulk delete via single SQL statement — no entity loading
                        var capturedEventType = eventType;
                        var capturedCutoff = cutoffDate;

                        try
                        {
                            int deletedCount = await _auditEventRepository.ExecuteDeleteWhereAsync(
                                e => e.EventType == capturedEventType
                                     && e.InsertedDate.HasValue
                                     && e.InsertedDate.Value < capturedCutoff,
                                cancellationToken);

                            if (deletedCount == 0)
                            {
                                _logger.LogDebug(
                                    "No {EventType} events to delete before {Cutoff}",
                                    eventType, cutoffDate);
                                continue;
                            }

                            results.Add((eventType, deletedCount, true, null));

                            _logger.LogInformation(
                                "Deleted {Count} {EventType} events per policy {PolicyType}",
                                deletedCount, eventType, policy.EventType);
                        }
                        catch (Exception ex)
                        {
                            results.Add((eventType, 0, false, ex.Message));

                            _logger.LogError(ex,
                                "Failed to delete {EventType} events",
                                eventType);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                results.Add((policy.EventType, 0, false, ex.Message));

                _logger.LogError(ex,
                    "Error applying retention policy for {EventType}",
                    policy.EventType);
            }
        }

        // Summary logging
        var totalProcessed = results.Sum(static r => r.Count);
        var successCount = results.Count(static r => r.Success);
        var failureCount = results.Count(static r => !r.Success);

        _logger.LogInformation(
            "Retention policy application complete: {Total} events processed, {Success} succeeded, {Failed} failed",
            totalProcessed, successCount, failureCount);

        if (failureCount > 0)
        {
            var failures = results.Where(static r => !r.Success).Select(static r => $"{r.EventType}: {r.Error}");
            _logger.LogWarning("Failed operations: {Failures}", string.Join("; ", failures));
        }
    }

    /// <summary>
    /// Gets audit events that violate retention policies for a specific event type.
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="retentionDays"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditEventEntity>> GetRetentionViolationsAsync(
        string eventType,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        return await _auditEventRepository.FindAsync(
            e => e.EventType == eventType && e.InsertedDate < cutoffDate,
            cancellationToken);
    }

    /// <summary>
    /// Gets compliance statistics for a specific date range and event types.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="eventTypes"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ComplianceStatistics> GetComplianceStatisticsAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        IEnumerable<string>? eventTypes = null,
        CancellationToken cancellationToken = default)
    {
        var eventTypesList = eventTypes?.ToList();

        // Build base predicate for date range + optional event type filter
        System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>> basePredicate =
            eventTypesList is { Count: > 0 }
                ? e => e.InsertedDate >= startDate && e.InsertedDate <= endDate
                       && e.EventType != null && eventTypesList.Contains(e.EventType)
                : e => e.InsertedDate >= startDate && e.InsertedDate <= endDate;

        // All counts execute as server-side SQL queries
        int totalEvents = await _auditEventRepository.CountAsync(basePredicate, cancellationToken);

        int userAccessEvents = await _auditEventRepository.CountAsync(
            e => e.InsertedDate >= startDate && e.InsertedDate <= endDate
                 && e.EventType != null && e.EventType.Contains("User"),
            cancellationToken);

        int dataModificationEvents = await _auditEventRepository.CountAsync(
            e => e.InsertedDate >= startDate && e.InsertedDate <= endDate
                 && e.EventType != null && e.EventType.Contains("Update"),
            cancellationToken);

        int securityEvents = await _auditEventRepository.CountAsync(
            e => e.InsertedDate >= startDate && e.InsertedDate <= endDate
                 && e.EventType != null && e.EventType.Contains("Security"),
            cancellationToken);

        int highRiskEvents = await _auditEventRepository.CountAsync(
            e => e.InsertedDate >= startDate && e.InsertedDate <= endDate
                 && e.EventType != null && e.EventType.Contains("Delete"),
            cancellationToken);

        return new ComplianceStatistics
        {
            TotalEvents = totalEvents,
            UserAccessEvents = userAccessEvents,
            DataModificationEvents = dataModificationEvents,
            SecurityEvents = securityEvents,
            HighRiskEvents = highRiskEvents
        };
    }

    /// <summary>
    /// Validates specific compliance requirements for an event type.
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="standard"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditValidationResult>> ValidateEventTypeComplianceAsync(
        string eventType,
        ComplianceStandard standard,
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        if (!_validators.TryGetValue(standard, out var validator))
        {
            throw new NotSupportedException($"Compliance standard {standard} not supported");
        }

        var events = await _auditEventRepository.FindAsync(
            e => e.EventType == eventType && e.InsertedDate >= startDate && e.InsertedDate <= endDate,
            cancellationToken);

        return await validator.ValidateAsync(events.ToList());
    }

    /// <summary>
    /// Anonymizes an IP address by masking certain segments.
    /// </summary>
    /// <param name="ipAddress"></param>
    /// <returns></returns>
    private string? AnonymizeIpAddress(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress)) return null;

        // Keep first two octets for IPv4, anonymize the rest
        var parts = ipAddress.Split('.');
        if (parts.Length == 4)
        {
            return $"{parts[0]}.{parts[1]}.XXX.XXX";
        }

        // For IPv6, keep network prefix
        if (!ipAddress.Contains(':')) return "ANONYMIZED";
        var colonIndex = ipAddress.IndexOf(':', ipAddress.IndexOf(':') + 1);
        return colonIndex > 0 ? ipAddress[..colonIndex] + ":XXXX:XXXX:XXXX" : "XXXX:XXXX:XXXX:XXXX";
    }

    /// <summary>
    /// Anonymizes user-identifiable information in JSON data.
    /// </summary>
    /// <param name="jsonData"></param>
    /// <param name="userId"></param>
    /// <returns></returns>
    private string AnonymizeJsonData(string jsonData, Guid userId)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonData);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            var userIdString = userId.ToString();

            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.Contains("User", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("Email", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("Name", StringComparison.OrdinalIgnoreCase) ||
                    (property.Value.ValueKind == JsonValueKind.String &&
                     property.Value.GetString()?.Contains(userIdString, StringComparison.OrdinalIgnoreCase) == true))
                {
                    writer.WriteString(property.Name, "ANONYMIZED");
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.Flush();

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return jsonData; // Return original if parsing fails
        }
    }
}