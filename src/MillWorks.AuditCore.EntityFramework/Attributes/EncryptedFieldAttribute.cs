namespace MillWorks.AuditCore.EntityFramework.Attributes;

/// <summary>
/// Marks a property for automatic field-level encryption in audit logs and database storage.
/// </summary>
/// <remarks>
/// <para>
/// <b>WARNING: Encrypted properties are NOT queryable by value.</b> AES-GCM encryption uses a random
/// nonce, so equality queries (<c>Where(e => e.Ssn == value)</c>) always return zero rows.
/// For equality lookups, add a deterministic HMAC shadow column or query by a non-encrypted
/// identifier and filter in memory.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class EncryptedFieldAttribute : Attribute
{
    /// <summary>
    /// Optional custom key name for this field
    /// If not specified, uses the property name
    /// </summary>
    public string? KeyName { get; set; }

    /// <summary>
    /// Whether to encrypt this field in audit logs
    /// Default is true
    /// </summary>
    public bool EncryptInAuditLog { get; set; } = true;

    /// <summary>
    /// Whether to encrypt this field in database storage
    /// Default is true
    /// </summary>
    public bool EncryptInDatabase { get; set; } = true;
}