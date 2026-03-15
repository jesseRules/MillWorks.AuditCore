namespace MillWorks.AuditCore.Services.Database.Options;

/// <summary>
/// Security options for audit logging
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// Enable tamper detection for audit logs
    /// </summary>
    public bool EnableTamperDetection { get; set; } = true;

    /// <summary>
    /// Use Redis for distributed locking
    /// </summary>
    public bool UseRedisLocking { get; set; } = false;

    /// <summary>
    /// Redis connection string for distributed locking
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// Enable digital signatures for audit events
    /// </summary>
    public bool EnableDigitalSignatures { get; set; } = false;

    /// <summary>
    /// Path to the private key for signing audit events
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// Path to the public key for verifying audit event signatures
    /// </summary>
    public string? PublicKeyPath { get; set; }
}