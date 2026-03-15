namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Interface for providing context information for audit operations
/// </summary>
public interface IAuditContext
{
    /// <summary>
    /// Current operation ID if within an operation scope
    /// </summary>
    Guid? OperationId { get; set; }

    /// <summary>
    /// AppUserDetailEntity.Id - Primary user identifier for business logic
    /// </summary>
    Guid? UserId { get; set; }

    /// <summary>
    /// ApplicationUser.Id - Identity user ID
    /// </summary>
    string? AspNetUserId { get; set; }

    /// <summary>
    /// User email address
    /// </summary>
    string? UserEmail { get; set; }

    /// <summary>
    /// User full name from AppUserDetailEntity
    /// </summary>
    string? UserFullName { get; set; }

    /// <summary>
    /// Tenant ID if multi-tenant
    /// </summary>
    Guid? TenantId { get; set; }

    /// <summary>
    /// HTTP request correlation ID
    /// </summary>
    string? CorrelationId { get; set; }

    /// <summary>
    /// Client IP address
    /// </summary>
    string? IpAddress { get; set; }

    /// <summary>
    /// User agent string
    /// </summary>
    string? UserAgent { get; set; }

    /// <summary>
    /// Request path
    /// </summary>
    string? RequestPath { get; set; }

    /// <summary>
    /// HTTP method
    /// </summary>
    string? RequestMethod { get; set; }
    
    /// <summary>
    /// Set Data
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    void SetData(string key, object value);

    /// <summary>
    /// Get custom context data
    /// </summary>
    /// <typeparam name="T">Type to cast the data to</typeparam>
    /// <param name="key">Key for the data</param>
    /// <returns>The data or default if not found</returns>
    T? GetData<T>(string key);

    /// <summary>
    /// Get all custom context data
    /// </summary>
    /// <returns>Dictionary of all custom data</returns>
    IDictionary<string, object> GetAllData();
    
    /// <summary>
    /// Has Data
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool HasData(string key);
    
    
    /// <summary>
    /// Remove Data
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool RemoveData(string key);

    /// <summary>
    /// Clear all context data
    /// </summary>
    void Clear();
}