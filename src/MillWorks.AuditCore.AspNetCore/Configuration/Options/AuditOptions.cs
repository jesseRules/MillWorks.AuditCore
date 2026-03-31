namespace MillWorks.AuditCore.AspNetCore.Configuration.Options;

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