using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.AspNetCore.Services;

/// <summary>
/// Factory for creating standardized audit events without Audit.NET dependencies
/// </summary>
public sealed class AuditEventFactory(
    IHttpContextAccessor httpContextAccessor,
    IAuditContext auditContext,
    ILogger<AuditEventFactory> logger)
    : IAuditEventFactory
{
    /// <summary>
    /// Create a new audit event with standard fields populated
    /// </summary>
    public AuditEvent CreateEvent(string eventType, object? target = null,
        [CallerMemberName] string callerMemberName = "")
    {
        var auditEvent = new AuditEvent
        {
            EventType = eventType,
            StartDate = DateTimeOffset.UtcNow,
            Environment = CreateEnvironment(callerMemberName),
            Target = target != null ? new AuditTarget { New = target } : null
        };

        // Use AuditContext if available (populated by middleware)
        EnrichFromAuditContext(auditEvent);
        
        // Fallback to direct HTTP context if needed
        if (string.IsNullOrEmpty(auditEvent.CustomFields.GetValueOrDefault("UserId")?.ToString()))
        {
            logger.LogDebug("User has no custom fields");
            EnrichWithUserContext(auditEvent);
        }
        
        EnrichWithRequestContext(auditEvent);

        return auditEvent;
    }

    /// <summary>
    /// Create an audit event for entity changes
    /// </summary>
    public AuditEvent CreateEntityEvent(string entityType, string action, object entity, object? oldValues = null)
    {
        string eventType = $"{entityType}.{action}";
        var auditEvent = CreateEvent(eventType);

        auditEvent.Target = new AuditTarget
        {
            Type = entityType,
            Old = oldValues,
            New = entity
        };

        // Add entity-specific fields
        auditEvent.CustomFields["EntityType"] = entityType;
        auditEvent.CustomFields["EntityId"] = entity.GetType().GetProperty("Id")?.GetValue(entity)?.ToString() ?? "Unknown";
        auditEvent.CustomFields["Action"] = action;

        return auditEvent;
    }

    /// <summary>
    /// Create an operation event (for long-running operations)
    /// </summary>
    public AuditEvent CreateOperationEvent(string operationType, Guid operationId, string status, object? metadata = null)
    {
        string eventType = $"{operationType}.{status}";
        var auditEvent = CreateEvent(eventType, metadata);

        auditEvent.CustomFields["OperationId"] = operationId;
        auditEvent.CustomFields["OperationType"] = operationType;
        auditEvent.CustomFields["Status"] = status;

        return auditEvent;
    }

    /// <summary>
    /// Enrich event from the populated AuditContext
    /// </summary>
    private void EnrichFromAuditContext(AuditEvent auditEvent)
    {
        if (auditContext.UserId.HasValue)
            auditEvent.CustomFields["UserId"] = auditContext.UserId.Value;
        
        if (!string.IsNullOrEmpty(auditContext.AspNetUserId))
            auditEvent.CustomFields["AspNetUserId"] = auditContext.AspNetUserId;
        
        if (!string.IsNullOrEmpty(auditContext.UserEmail))
        {
            auditEvent.CustomFields["UserEmail"] = auditContext.UserEmail;
            auditEvent.Environment.UserName = auditContext.UserEmail;
        }
        
        if (!string.IsNullOrEmpty(auditContext.UserFullName))
            auditEvent.CustomFields["UserFullName"] = auditContext.UserFullName;
        
        if (auditContext.TenantId.HasValue)
            auditEvent.CustomFields["TenantId"] = auditContext.TenantId.Value;
        
        if (auditContext.OperationId.HasValue)
            auditEvent.CustomFields["ParentOperationId"] = auditContext.OperationId.Value;

        // Add any custom data from context
        foreach (var data in auditContext.GetAllData())
        {
            auditEvent.CustomFields[data.Key] = data.Value;
        }
    }

    /// <summary>
    /// Create the environment details for the audit event
    /// </summary>
    private AuditEnvironment CreateEnvironment(string callerMemberName)
    {
        string userName = auditContext.UserEmail
            ?? httpContextAccessor.HttpContext?.User.Identity?.Name
            ?? "System";

        return new AuditEnvironment
        {
            UserName = userName,
            MachineName = Environment.MachineName,
            DomainName = Environment.UserDomainName,
            CallingMethodName = callerMemberName,
            AssemblyName = "MillWorks.Audit",
            Culture = Thread.CurrentThread.CurrentCulture.ToString()
        };
    }

    /// <summary>
    /// Enrich the audit event with user context information (fallback if AuditContext not populated)
    /// </summary>
    private void EnrichWithUserContext(AuditEvent auditEvent)
    {
        var context = httpContextAccessor.HttpContext;
        if (context?.User.Identity?.IsAuthenticated != true) return;

        // Get AspNetUserId from claims
        string? aspNetUserId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(aspNetUserId))
        {
            auditEvent.CustomFields["AspNetUserId"] = aspNetUserId;
        }

        // Try to get AppUserDetailEntity information
        auditEvent.CustomFields["UserId"] = aspNetUserId;
        auditEvent.CustomFields["UserEmail"] = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

        // Set the display username
        string? userEmail = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        auditEvent.Environment.UserName = userEmail ?? context.User.Identity.Name ?? "Unknown";
    }

    /// <summary>
    /// Enrich the audit event with HTTP request context information
    /// </summary>
    private void EnrichWithRequestContext(AuditEvent auditEvent)
    {
        // First try to use AuditContext values
        if (!string.IsNullOrEmpty(auditContext.CorrelationId))
        {
            auditEvent.CustomFields["CorrelationId"] = auditContext.CorrelationId;
            auditEvent.CustomFields["IpAddress"] = auditContext.IpAddress;
            auditEvent.CustomFields["UserAgent"] = auditContext.UserAgent;
            auditEvent.CustomFields["RequestPath"] = auditContext.RequestPath;
            auditEvent.CustomFields["RequestMethod"] = auditContext.RequestMethod;
            auditEvent.CustomFields["RequestId"] = auditContext.CorrelationId;
            return;
        }

        // Fallback to direct HTTP context
        var context = httpContextAccessor.HttpContext;
        if (context == null) return;

        auditEvent.CustomFields["CorrelationId"] = context.TraceIdentifier;
        auditEvent.CustomFields["IpAddress"] = context.Connection.RemoteIpAddress?.ToString();
        auditEvent.CustomFields["UserAgent"] = context.Request.Headers["User-Agent"].ToString();
        auditEvent.CustomFields["RequestPath"] = context.Request.Path.ToString();
        auditEvent.CustomFields["RequestMethod"] = context.Request.Method;
        auditEvent.CustomFields["RequestId"] = Activity.Current?.Id ?? context.TraceIdentifier;
    }

}