using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Validators;

/// <summary>
/// NIST compliance validator providing <b>approximate</b> NIST coverage (SP 800-53 / CSF /
/// SP 800-171) by composing the checks the platform already implements:
/// <list type="bullet">
///   <item><description>the DISA STIG validator, whose controls map directly onto the
///   NIST 800-53 AU/AC/IA/SI control families;</description></item>
///   <item><description>the ISO/IEC 27001 validator, which adds ISMS logging and monitoring
///   breadth that overlaps NIST.</description></item>
/// </list>
/// This is deliberately an <i>approximation via overlap</i> rather than a dedicated NIST control
/// catalog — it reuses the two overlapping validators instead of duplicating ~600 lines of
/// control logic. Consumers that map a NIST-family framework onto
/// <see cref="ComplianceStandard.NIST"/> (e.g. NIST 800-53, NIST CSF, NIST 800-171) get a
/// non-empty report built from these overlapping controls.
/// </summary>
public sealed class NistValidator : IComplianceValidator
{
    private readonly StigValidator _stigValidator;
    private readonly Iso27001Validator _iso27001Validator;

    /// <summary>
    /// Creates a NIST validator over the default STIG and ISO 27001 validators. Both delegates are
    /// stateless, so they are constructed directly rather than injected — this keeps the NIST
    /// approximation self-contained and independent of how the two feed validators are registered.
    /// </summary>
    public NistValidator()
        : this(new StigValidator(), new Iso27001Validator())
    {
    }

    /// <summary>
    /// Creates a NIST validator over explicit delegate validators. Exposed for composition and tests.
    /// </summary>
    public NistValidator(StigValidator stigValidator, Iso27001Validator iso27001Validator)
    {
        _stigValidator = stigValidator;
        _iso27001Validator = iso27001Validator;
    }

    /// <inheritdoc />
    public ComplianceStandard Standard => ComplianceStandard.NIST;

    /// <summary>
    /// Validates audit events for approximate NIST coverage by running the STIG and ISO 27001
    /// validators and unioning their findings.
    /// </summary>
    /// <remarks>
    /// Unlike the single-standard validators, this runs whenever <see cref="ComplianceStandard.NIST"/>
    /// <i>or</i> either of its constituent standards (STIG / ISO 27001) is enabled. A NIST report is
    /// derived from those overlapping controls, so gating strictly on NIST would return an empty
    /// report whenever a host enabled only the constituents — and an empty report is dangerous for
    /// callers such as continuous-monitoring drift trackers, which read "no failing rules" as
    /// "every control now passes" and would falsely auto-resolve open NIST drift.
    /// </remarks>
    public async Task<List<AuditValidationResult>> ValidateAsync(ComplianceValidationContext context)
    {
        var relevant = context.EnabledStandards.Contains(ComplianceStandard.NIST)
            || context.EnabledStandards.Contains(ComplianceStandard.STIG)
            || context.EnabledStandards.Contains(ComplianceStandard.ISO27001);

        if (!relevant)
            return [];

        // Force the two constituent standards on for the delegated validators regardless of the
        // host's configured set — NIST coverage is defined AS this STIG + ISO 27001 overlap, so the
        // delegates must run even when a host enabled NIST alone.
        var overlapContext = context with
        {
            EnabledStandards = [ComplianceStandard.STIG, ComplianceStandard.ISO27001]
        };

        var stigResults = await _stigValidator.ValidateAsync(overlapContext);
        var isoResults = await _iso27001Validator.ValidateAsync(overlapContext);

        // Union both control sets. STIG rule names (e.g. "... (V-222582 / AU-12)") and ISO rule names
        // (e.g. "Event Logging (A.12.4.1)") do not collide, so concatenation preserves every finding.
        // De-dupe defensively by rule name so the merged catalog stays a stable set of identifiers
        // for downstream drift tracking even if a future rule is added under both validators.
        var merged = new List<AuditValidationResult>(stigResults.Count + isoResults.Count);
        var seenRuleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in stigResults.Concat(isoResults))
        {
            if (seenRuleNames.Add(result.RuleName))
                merged.Add(result);
        }

        return merged;
    }

    /// <summary>
    /// Generates NIST remediation guidance over the merged STIG / ISO 27001 findings, ordered by
    /// severity, with a preamble making the approximate (overlap-derived) nature explicit.
    /// </summary>
    public List<string> GenerateRecommendations(IEnumerable<AuditValidationResult> results)
    {
        var resultList = results as IReadOnlyList<AuditValidationResult> ?? results.ToList();
        var failed = resultList
            .Where(static r => !r.Passed)
            .OrderByDescending(static r => r.Severity)
            .ToList();

        var recommendations = new List<string>
        {
            "NIST coverage is approximated from the DISA STIG (NIST SP 800-53 mapping) and " +
            "ISO/IEC 27001 controls — it is an overlap-based approximation, not a full NIST catalog."
        };

        if (failed.Count == 0)
        {
            recommendations.Add("✅ NIST (approximate): all overlapping STIG / ISO 27001 checks passed.");
            return recommendations;
        }

        recommendations.Add(
            $"Found {failed.Count} finding(s) across the STIG / ISO 27001 overlap requiring remediation:");

        foreach (var result in failed)
        {
            recommendations.Add(
                $"  • [{result.Severity}] {result.RuleName}" +
                (result.RegulationReference is not null ? $" ({result.RegulationReference})" : string.Empty) +
                $": {result.Message}");
            recommendations.AddRange(result.Recommendations.Select(static r => $"      - {r}"));
        }

        return recommendations;
    }
}
