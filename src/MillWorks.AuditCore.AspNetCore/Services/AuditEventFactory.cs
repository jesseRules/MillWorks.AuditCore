using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.AspNetCore.Services;

/// <summary>
/// Factory for creating standardized audit events without Audit.NET dependencies
/// </summary>
public sealed class AuditEventFactory(
    IHttpContextAccessor httpContextAccessor,
    IAuditContext auditContext,
    IOptions<AuditOptions> auditOptions,
    ILogger<AuditEventFactory> logger)
    : IAuditEventFactory
{
    // Static environment values are constant for the lifetime of the process —
    // no need to query the OS per event.
    private static readonly string _machineName = Environment.MachineName;
    private static readonly string _domainName = Environment.UserDomainName;

    // Cached compiled delegates for extracting "Id" property from entity types,
    // avoiding per-event reflection overhead.
    private static readonly ConcurrentDictionary<Type, Func<object, string>?> _idGetterCache = new();

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
        auditEvent.CustomFields["EntityId"] = GetEntityId(entity);
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
            MachineName = _machineName,
            DomainName = _domainName,
            CallingMethodName = callerMemberName,
            AssemblyName = auditOptions.Value.ApplicationName,
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

        // Get AspNetUserId from claims - this is the ASP.NET Identity string identifier
        string? aspNetUserId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(aspNetUserId))
        {
            auditEvent.CustomFields["AspNetUserId"] = aspNetUserId;
        }

        // Note: We intentionally do NOT set CustomFields["UserId"] here.
        // UserId must be a Guid (the AppUserDetailEntity.Id), not a string.
        // The persistence layer only maps CustomFields["UserId"] when it's a Guid.
        // In this fallback path we only have the ASP.NET Identity string ID,
        // which is already captured in AspNetUserId above.

        string? userEmail = context.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
        if (!string.IsNullOrEmpty(userEmail))
        {
            auditEvent.CustomFields["UserEmail"] = userEmail;
        }

        // Set the display username
        auditEvent.Environment.UserName = userEmail ?? context.User.Identity.Name ?? "Unknown";
    }

    /// <summary>
    /// Enrich the audit event with HTTP request context information
    /// </summary>
    private void EnrichWithRequestContext(AuditEvent auditEvent)
    {
        var context = httpContextAccessor.HttpContext;

        // For each field, prefer AuditContext if populated, otherwise fall back to HttpContext.
        // This ensures we capture the most complete data even if middleware only partially populated AuditContext.

        string? correlationId = !string.IsNullOrEmpty(auditContext.CorrelationId)
            ? auditContext.CorrelationId
            : context?.TraceIdentifier;

        string? ipAddress = !string.IsNullOrEmpty(auditContext.IpAddress)
            ? auditContext.IpAddress
            : context?.Connection.RemoteIpAddress?.ToString();

        string? userAgent = !string.IsNullOrEmpty(auditContext.UserAgent)
            ? auditContext.UserAgent
            : context?.Request.Headers["User-Agent"].ToString();

        string? requestPath = !string.IsNullOrEmpty(auditContext.RequestPath)
            ? auditContext.RequestPath
            : context?.Request.Path.ToString();

        string? requestMethod = !string.IsNullOrEmpty(auditContext.RequestMethod)
            ? auditContext.RequestMethod
            : context?.Request.Method;

        // Only set fields that have values
        if (!string.IsNullOrEmpty(correlationId))
        {
            auditEvent.CustomFields["CorrelationId"] = correlationId;
            auditEvent.CustomFields["RequestId"] = Activity.Current?.Id ?? correlationId;
        }

        if (!string.IsNullOrEmpty(ipAddress))
            auditEvent.CustomFields["IpAddress"] = ipAddress;

        if (!string.IsNullOrEmpty(userAgent))
            auditEvent.CustomFields["UserAgent"] = userAgent;

        if (!string.IsNullOrEmpty(requestPath))
            auditEvent.CustomFields["RequestPath"] = requestPath;

        if (!string.IsNullOrEmpty(requestMethod))
            auditEvent.CustomFields["RequestMethod"] = requestMethod;
    }

    /// <summary>
    /// Extracts the "Id" property value from an entity using cached compiled expressions.
    /// Falls back to "Unknown" if the entity type has no "Id" property.
    /// </summary>
    private static string GetEntityId(object entity)
    {
        var type = entity.GetType();
        var getter = _idGetterCache.GetOrAdd(type, static t =>
        {
            var prop = t.GetProperty("Id");
            if (prop is null) return null;

            // Build: (object obj) => ((TEntity)obj).Id?.ToString() ?? "Unknown"
            var param = Expression.Parameter(typeof(object), "obj");
            var cast = Expression.Convert(param, t);
            var access = Expression.Property(cast, prop);
            var toObject = Expression.Convert(access, typeof(object));
            var func = Expression.Lambda<Func<object, object>>(toObject, param).Compile();
            return obj => func(obj)?.ToString() ?? "Unknown";
        });

        return getter?.Invoke(entity) ?? "Unknown";
    }
}