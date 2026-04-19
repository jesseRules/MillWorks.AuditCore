using System.Reflection;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Services.Database.Options;

/// <summary>
/// Compliance configuration options
/// </summary>
public sealed class ComplianceOptions
{
    /// <summary>
    /// Compliance standards to validate against
    /// </summary>
    public List<ComplianceStandard> Standards { get; set; } = new();

    /// <summary>
    /// Enable automatic compliance validation
    /// </summary>
    public bool EnableAutomaticValidation { get; set; } = true;

    /// <summary>
    /// Data retention days (for GDPR, etc.)
    /// </summary>
    public int DataRetentionDays { get; set; } = 365;

    /// <summary>
    /// Assemblies to scan for compliance attributes ([FERPA], [SensitiveData]).
    /// If empty, falls back to AppDomain scanning (not recommended for production).
    /// </summary>
    public List<Assembly> AssembliesToScan { get; } = new();

    /// <summary>
    /// Controls how the compliance subsystem responds to violations at save time.
    /// Default is <see cref="ComplianceEnforcementMode.Advisory"/> (log only, never block).
    /// </summary>
    public ComplianceEnforcementMode EnforcementMode { get; set; } = ComplianceEnforcementMode.Advisory;

    /// <summary>
    /// When true, an <c>IHostedService</c> warms the consent cache from the database at startup.
    /// Strongly recommended when <see cref="EnforcementMode"/> is <see cref="ComplianceEnforcementMode.Enforce"/>:
    /// a cold cache in Enforce mode blocks all FERPA entity saves until consent is explicitly recorded
    /// (correct fail-closed behavior, but operationally disruptive if unexpected).
    /// </summary>
    public bool WarmConsentCacheOnStartup { get; set; }

    /// <summary>
    /// Register assemblies to scan for compliance attributes.
    /// Recommended: pass the assembly containing your entity types.
    /// Example: options.ScanAssemblies(typeof(StudentEntity).Assembly)
    /// </summary>
    public ComplianceOptions ScanAssemblies(params Assembly[] assemblies)
    {
        AssembliesToScan.AddRange(assemblies);
        return this;
    }
}

/// <summary>
/// Runtime validator for <see cref="ComplianceOptions"/>. Registered via the options
/// pipeline with <c>ValidateOnStart()</c> so misconfiguration fails at host boot.
/// </summary>
internal sealed class ComplianceOptionsValidator : IValidateOptions<ComplianceOptions>
{
    public ValidateOptionsResult Validate(string? name, ComplianceOptions options)
    {
        var failures = new List<string>();

        if (options.DataRetentionDays <= 0)
        {
            failures.Add(
                $"{nameof(ComplianceOptions.DataRetentionDays)} must be > 0.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}