using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace MillWorks.AuditCore.Services.Core;


/// <summary>
/// Audit configuration
/// </summary>
public sealed class AuditConfigurationSettings
{
    /// <summary>
    /// Whether auditing is enabled
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Audit storage provider
    /// </summary>
    public string StorageProvider { get; init; } = "Database"; // Database, EventHub, ServiceBus

    /// <summary>
    /// Audit retention in days
    /// </summary>
    [Range(1, 2555, ErrorMessage = "Audit retention must be between 1 and 2555 days (7 years)")]
    public int RetentionDays { get; init; } = 365;

    /// <summary>
    /// Whether to include sensitive data in audit logs
    /// </summary>
    public bool IncludeSensitiveData { get; init; }

    /// <summary>
    /// Audit events to capture
    /// </summary>
    public List<string> EventTypes { get; init; } =
    [
        "LoginSuccess",
        "LoginFailure",
        "Logout",
        "PasswordChange",
        "PasswordReset",
        "AccountLockout",
        "AccountUnlock",
        "MfaSuccess",
        "MfaFailure",
        "TokenGenerated",
        "TokenRevoked",
        "SecurityAlert"
    ];

    /// <summary>
    /// Real-time audit destinations
    /// </summary>
    [ValidateObjectMembers]
    public RealTimeAuditConfiguration RealTime { get; init; } = new();
}



/// <summary>
/// Real-time audit configuration
/// </summary>
public sealed class RealTimeAuditConfiguration
{
    /// <summary>
    /// Whether real-time auditing is enabled
    /// </summary>
    public bool Enabled { get; set; } = false;

    ///// <summary>
    ///// Azure Event Hub configuration
    ///// </summary>
    //[ValidateObjectMembers]
    //public EventHubConfiguration EventHub { get; set; } = new();

    ///// <summary>
    ///// Azure Service Bus configuration
    ///// </summary>
    //[ValidateObjectMembers]
    //public ServiceBusConfiguration ServiceBus { get; set; } = new();

    ///// <summary>
    ///// Webhook configuration
    ///// </summary>
    //[ValidateObjectMembers]
    //public WebhookConfiguration Webhook { get; set; } = new();
}