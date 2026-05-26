using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Providers.Base;

/// <summary>
/// Base implementation for audit providers
/// </summary>
public abstract class BaseAuditProvider : IAuditProvider
{
    /// <summary>
    /// HTTP context accessor to get request information
    /// </summary>
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Event factory to create audit events
    /// </summary>
    private readonly IAuditEventFactory _eventFactory;

    /// <summary>
    /// Logger for audit events
    /// </summary>
    protected readonly ILogger Logger;

    /// <summary>
    /// Cache for reflected properties per type to avoid repeated reflection calls
    /// </summary>
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _propertyCache = new();

    /// <summary>
    /// Base constructor for audit providers
    /// </summary>
    /// <param name="httpContextAccessor"></param>
    /// <param name="eventFactory"></param>
    /// <param name="loggerFactory"></param>
    protected BaseAuditProvider(
        IHttpContextAccessor httpContextAccessor,
        IAuditEventFactory eventFactory,
        ILoggerFactory loggerFactory)
    {
        _httpContextAccessor = httpContextAccessor;
        _eventFactory = eventFactory;
        Logger = loggerFactory.CreateLogger(GetType());
    }

    /// <summary>
    /// Entity type that this provider handles
    /// </summary>
    public abstract string EntityType { get; }

    /// <summary>
    /// Creates an audit event for the specified action and entity
    /// </summary>
    /// <param name="action"></param>
    /// <param name="entity"></param>
    /// <param name="oldValues"></param>
    /// <returns></returns>
    public virtual async Task<AuditEvent> CreateAuditEventAsync(string action, object? entity, object? oldValues = null)
    {
        ArgumentNullException.ThrowIfNull(entity);

        AuditEvent auditEvent = _eventFactory.CreateEntityEvent(EntityType, action, entity, oldValues);
        if (_httpContextAccessor.HttpContext != null)
        {
            auditEvent.IpAddress = _httpContextAccessor.HttpContext.Connection.RemoteIpAddress?.ToString();
            auditEvent.UserAgent = _httpContextAccessor.HttpContext.Request.Headers["User-Agent"].ToString();
            auditEvent.RequestId = _httpContextAccessor.HttpContext.TraceIdentifier;
        }

        // Add changes if available
        if (oldValues != null)
        {
            var changes = GetChanges(oldValues, entity);
            if (changes.Count > 0)
            {
                auditEvent.CustomFields["Changes"] = changes;
            }
        }

        await EnrichAuditEventAsync(auditEvent, entity);

        return auditEvent;
    }

    /// <summary>
    /// Should the action be audited for the given entity?
    /// </summary>
    /// <param name="action"></param>
    /// <param name="entity"></param>
    /// <returns></returns>
    public abstract Task<bool> ShouldAuditAsync(string action, object entity);

    /// <summary>
    /// Enrich the audit event with additional entity-specific information
    /// </summary>
    /// <param name="auditEvent"></param>
    /// <param name="entity"></param>
    /// <returns></returns>
    public virtual Task EnrichAuditEventAsync(AuditEvent auditEvent, object? entity)
    {
        // Base implementation - can be overridden by specific providers
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the changes between old and new values
    /// </summary>
    /// <param name="oldValues"></param>
    /// <param name="newValues"></param>
    /// <returns></returns>
    public virtual Dictionary<string, object?> GetChanges(object? oldValues, object? newValues)
    {
        var changes = new Dictionary<string, object?>();

        if (oldValues == null || newValues == null)
            return changes;

        var oldDict = oldValues as Dictionary<string, object>;
        var properties = GetScalarProperties(newValues.GetType());

        foreach (PropertyInfo property in properties)
        {
            try
            {
                object? oldValue;
                if (oldDict != null)
                {
                    // oldValues is a Dictionary (from EF change tracking)
                    if (!oldDict.TryGetValue(property.Name, out oldValue))
                        continue;
                }
                else
                {
                    // Both are entity objects
                    oldValue = property.GetValue(oldValues);
                }

                var newValue = property.GetValue(newValues);

                if (!Equals(oldValue, newValue))
                {
                    changes[property.Name] = new
                    {
                        Old = oldValue,
                        New = newValue
                    };
                }
            }
            catch (TargetException)
            {
                // Skip properties that can't be accessed on both objects
            }
        }

        return changes;
    }

    /// <summary>
    /// Gets scalar (non-navigation, non-collection) public instance properties for a type, with caching.
    /// </summary>
    protected static PropertyInfo[] GetScalarProperties(Type type)
    {
        return _propertyCache.GetOrAdd(type, static t =>
            t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static p => !p.PropertyType.IsClass || p.PropertyType == typeof(string))
                .ToArray());
    }

    /// <summary>
    /// Helpers to check if a property has a specific attribute
    /// </summary>
    /// <param name="property"></param>
    /// <typeparam name="TAttribute"></typeparam>
    /// <returns></returns>
    protected bool HasAttribute<TAttribute>(PropertyInfo property) where TAttribute : Attribute =>
        property.GetCustomAttribute<TAttribute>() != null;
}
