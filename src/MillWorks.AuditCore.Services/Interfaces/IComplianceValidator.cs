using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Validators.Interfaces;

/// <summary>
/// Context for compliance validation with server-side aggregates.
/// </summary>
public sealed record ComplianceValidationContext
{
    /// <summary>
    /// Sampled events for validators that need to inspect event content.
    /// </summary>
    public required List<AuditEventEntity> Events { get; init; }

    /// <summary>
    /// Server-side oldest event date in the period (null if no events).
    /// </summary>
    public DateTimeOffset? OldestEventDate { get; init; }

    /// <summary>
    /// Server-side newest event date in the period (null if no events).
    /// </summary>
    public DateTimeOffset? NewestEventDate { get; init; }

    /// <summary>
    /// Total event count in the date range (server-side).
    /// </summary>
    public int TotalEventCount { get; init; }

    /// <summary>
    /// Count of events lacking integrity protection (server-side).
    /// </summary>
    public int UnprotectedEventCount { get; init; }

    /// <summary>
    /// Compliance standards enabled for this validation run.
    /// Validators should return early if their standard is not in this set.
    /// </summary>
    public HashSet<ComplianceStandard> EnabledStandards { get; init; } = [];

    /// <summary>
    /// Creates a context from an event list for testing. Computes aggregates from the events.
    /// </summary>
    public static ComplianceValidationContext FromEvents(List<AuditEventEntity> events)
    {
        return new ComplianceValidationContext
        {
            Events = events,
            OldestEventDate = events.Where(e => e.InsertedDate.HasValue).MinBy(e => e.InsertedDate)?.InsertedDate,
            NewestEventDate = events.Where(e => e.InsertedDate.HasValue).MaxBy(e => e.InsertedDate)?.InsertedDate,
            TotalEventCount = events.Count,
            UnprotectedEventCount = events.Count(e => e.AuditIntegrity == null),
            EnabledStandards = Enum.GetValues<ComplianceStandard>().ToHashSet()
        };
    }
}

/// <summary>
/// Interface for compliance validation service
/// </summary>
public interface IComplianceValidator
{
    /// <summary>
    /// The compliance standard this validator targets.
    /// </summary>
    ComplianceStandard Standard { get; }

    /// <summary>
    /// Validates audit events against compliance standards using server-side aggregates.
    /// </summary>
    Task<List<AuditValidationResult>> ValidateAsync(ComplianceValidationContext context);

    /// <summary>
    /// Generates a compliance report based on the validation results.
    /// </summary>
    /// <param name="results"></param>
    /// <returns></returns>
    List<string> GenerateRecommendations(IEnumerable<AuditValidationResult> results);
}