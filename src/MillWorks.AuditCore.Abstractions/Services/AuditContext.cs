using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Abstractions.Services;

/// <summary>
/// Provides context for the current audit operation, including user information and request details
/// </summary>
public sealed class AuditContext: IAuditContext
{
    /// <summary>
    /// Custom context data storage
    /// </summary>
    private readonly Dictionary<string, object> _contextData = new();

    /// <summary>
    /// Current operation ID if within an operation scope
    /// </summary>
    public Guid? OperationId { get; set; }

    /// <summary>
    /// AppUserDetailEntity.Id - Primary user identifier for business logic
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>
    /// ApplicationUser.Id - Identity user ID
    /// </summary>
    public string? AspNetUserId { get; set; }

    /// <summary>
    /// User email address
    /// </summary>
    public string? UserEmail { get; set; }

    /// <summary>
    /// User full name from AppUserDetailEntity
    /// </summary>
    public string? UserFullName { get; set; }

    /// <summary>
    /// Tenant ID if multi-tenant
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// HTTP request correlation ID
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Client IP address
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent string
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Request path
    /// </summary>
    public string? RequestPath { get; set; }

    /// <summary>
    /// HTTP method
    /// </summary>
    public string? RequestMethod { get; set; }

    /// <summary>
    /// Set custom context data
    /// </summary>
    public void SetData(string key, object value) => _contextData[key] = value;

    /// <summary>
    /// Get custom context data
    /// </summary>
    public T? GetData<T>(string key) => _contextData.TryGetValue(key, out var value) && value is T typed ? typed : default;

    /// <summary>
    /// Check if custom data exists for a key
    /// </summary>
    public bool HasData(string key) => _contextData.ContainsKey(key);

    /// <summary>
    /// Remove custom context data
    /// </summary>
    public bool RemoveData(string key) => _contextData.Remove(key);

    /// <summary>
    /// Get all custom context data
    /// </summary>
    public IDictionary<string, object> GetAllData() => new Dictionary<string, object>(_contextData);

    /// <summary>
    /// Clear all context data
    /// </summary>
    public void Clear()
    {
        _contextData.Clear();
        OperationId = null;
        UserId = null;
        AspNetUserId = null;
        UserEmail = null;
        UserFullName = null;
        TenantId = null;
        CorrelationId = null;
        IpAddress = null;
        UserAgent = null;
        RequestPath = null;
        RequestMethod = null;
    }
}
