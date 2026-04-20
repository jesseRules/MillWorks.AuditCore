using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Services.Options;

/// <summary>
/// Audit configuration options
/// </summary>
public sealed class AuditOptions
{
    /// <summary>
    /// Application name for audit logs
    /// </summary>
    private string _applicationName = "Unknown";

    /// <summary>
    /// HMAC key for signing audit events
    /// </summary>
    private string? _hmacKey;

    /// <summary>
    /// Environment name for audit logs
    /// </summary>
    private string _environment = "Production";

    /// <summary>
    /// Enable or disable audit logging
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Application name for audit logs
    /// </summary>
    public string ApplicationName
    {
        get => _applicationName;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("ApplicationName cannot be null or empty", nameof(value));

            if (value.Length > 100)
                throw new ArgumentException("ApplicationName cannot exceed 100 characters", nameof(value));

            // Sanitize - remove potentially problematic characters
            _applicationName = System.Text.RegularExpressions.Regex.Replace(
                value, @"[^\w\s\-\.]", "").Trim();
        }
    }

    /// <summary>
    /// Environment name for audit logs
    /// </summary>
    public string Environment
    {
        get => _environment;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Environment cannot be null or empty", nameof(value));

            if (value.Length > 50)
                throw new ArgumentException("Environment cannot exceed 50 characters", nameof(value));

            _environment = value;
        }
    }

    /// <summary>
    /// Enable digital signatures for audit events
    /// </summary>
    public bool EnableDigitalSignatures { get; set; }

    /// <summary>
    /// HMAC key for signing audit events
    /// </summary>
    public string? HmacKey
    {
        get => _hmacKey;
        set => _hmacKey = value;
    }

    /// <summary>
    /// When true, allows the pass-through (no-op) redactor in Production.
    /// Defaults to false. Set to true only if you explicitly accept that
    /// sensitive data (PHI/PII) will be persisted unredacted in audit storage.
    /// </summary>
    public bool AllowPassThroughRedactor { get; set; }

    /// <summary>
    /// Controls how the EF audit interceptor responds to failures building audit
    /// log records. Default <see cref="AuditFailureMode.Permissive"/> preserves
    /// the historical "audit must never break the application's SaveChanges"
    /// behavior. Fail-closed modes rethrow and roll back the business transaction
    /// when the policy considers the save regulated.
    /// </summary>
    public AuditFailureMode FailureMode { get; set; } = AuditFailureMode.Permissive;

    /// <summary>
    /// Default custom fields to include in every audit event
    /// </summary>
    public Dictionary<string, object> DefaultCustomFields { get; set; } = new();

    /// <summary>
    /// Validates the configuration
    /// </summary>
    public void Validate()
    {
        if (EnableDigitalSignatures)
        {
            if (string.IsNullOrEmpty(HmacKey))
            {
                throw new InvalidOperationException(
                    "HmacKey must be provided when EnableDigitalSignatures is true");
            }

            if (HmacKey.Length < 32)
            {
                throw new InvalidOperationException(
                    "HmacKey must be at least 32 characters when EnableDigitalSignatures is true");
            }
        }

        if (DefaultCustomFields.Count > 50)
        {
            throw new InvalidOperationException(
                "DefaultCustomFields cannot exceed 50 entries");
        }
    }
}

/// <summary>
/// Runtime validator for <see cref="AuditOptions"/>. Registered via the options pipeline
/// with <c>ValidateOnStart()</c> so misconfiguration fails at host boot, not at first use.
/// Uses <see cref="IHostEnvironment"/> when available to determine Production; falls back
/// to <see cref="AuditOptions.Environment"/> when the host environment is not registered.
/// </summary>
internal sealed class AuditOptionsValidator : IValidateOptions<AuditOptions>
{
    private readonly IHostEnvironment? _hostEnvironment;

    public AuditOptionsValidator(IHostEnvironment? hostEnvironment = null)
    {
        _hostEnvironment = hostEnvironment;
    }

    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        var failures = new List<string>();

        try
        {
            options.Validate();
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message);
        }
        catch (ArgumentException ex)
        {
            failures.Add(ex.Message);
        }

        if (IsProduction(options) && string.IsNullOrEmpty(options.HmacKey))
        {
            failures.Add(
                $"{nameof(AuditOptions.HmacKey)} must be configured in Production. " +
                "A transient key would cause false tamper alerts across instances or after restarts.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private bool IsProduction(AuditOptions options)
    {
        if (_hostEnvironment != null)
        {
            return _hostEnvironment.IsProduction();
        }

        return string.Equals(options.Environment, "Production", StringComparison.OrdinalIgnoreCase);
    }
}
