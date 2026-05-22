namespace MillWorks.AuditCore.Services.Options;

/// <summary>
/// Configuration options for the default field redactor.
/// </summary>
public sealed class RedactionOptions
{
    /// <summary>
    /// Additional field names to treat as safe (pass through without redaction).
    /// Use this to preserve fields like CorrelationId or SessionId when your system
    /// guarantees they contain only opaque identifiers, not PII.
    ///
    /// <para><b>Warning:</b> Adding fields here weakens the safe-by-default posture.
    /// Only add fields you have verified never contain PHI/PII in your system.</para>
    ///
    /// <example>
    /// <code>
    /// services.Configure&lt;RedactionOptions&gt;(o =>
    /// {
    ///     o.AdditionalSafeFields = ["CorrelationId", "SessionId"];
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public string[] AdditionalSafeFields { get; set; } = [];
}
