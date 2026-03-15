using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Validators.Interfaces;

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
    /// Validates a list of audit events against compliance standards.
    /// </summary>
    /// <param name="events"></param>
    /// <returns></returns>
    Task<List<AuditValidationResult>> ValidateAsync(List<AuditEventEntity> events);

    /// <summary>
    /// Generates a compliance report based on the validation results.
    /// </summary>
    /// <param name="results"></param>
    /// <returns></returns>
    List<string> GenerateRecommendations(IEnumerable<AuditValidationResult> results);
}