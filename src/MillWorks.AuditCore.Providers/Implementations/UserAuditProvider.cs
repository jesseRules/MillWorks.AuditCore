using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Providers.Base;

namespace MillWorks.AuditCore.Providers.Implementations;

/// <summary>
/// User audit provider that handles auditing of user-related actions.
/// </summary>
/// <param name="httpContextAccessor"></param>
/// <param name="eventFactory"></param>
/// <param name="loggerFactory"></param>
public sealed class UserAuditProvider(
    IHttpContextAccessor httpContextAccessor,
    IAuditEventFactory eventFactory,
    ILoggerFactory loggerFactory)
    : BaseAuditProvider(httpContextAccessor, eventFactory, loggerFactory)
{
    /// <summary>
    /// List of sensitive properties that should be excluded from audit logs
    /// </summary>
    private static readonly HashSet<string> _sensitiveProperties = new(StringComparer.Ordinal)
    {
        "PasswordHash",
        "SecurityStamp",
        "RefreshToken",
        "ConcurrencyStamp",
        "FailedLoginAttempts",
        "LastLoginIpAddress",
        "ExternalLoginProviderKey"
    };

    /// <summary>
    /// Entity type that this provider handles
    /// </summary>
    public override string EntityType => "User";

    /// <summary>
    /// Specifies whether the action should be audited for the given entity
    /// </summary>
    /// <param name="action"></param>
    /// <param name="entity"></param>
    /// <returns></returns>
    public override Task<bool> ShouldAuditAsync(string action, object entity)
    {
        // Audit all user actions for security
        return Task.FromResult(true);
    }

    /// <summary>
    /// Enriches the audit event with additional information specific to users
    /// </summary>
    /// <param name="auditEvent"></param>
    /// <param name="entity"></param>
    public override async Task EnrichAuditEventAsync(AuditEvent auditEvent, object? entity)
    {
        await base.EnrichAuditEventAsync(auditEvent, entity);

        switch (entity)
        {
            case Dictionary<string, object> entityDict:
                // Handle dictionary from EF change tracking
                // For ApplicationUser fields
                if (entityDict.TryGetValue("Id", out var id))
                {
                    // Check if it's a GUID (AppUserDetail) or string (ApplicationUser)
                    if (id is Guid)
                        auditEvent.CustomFields["UserId"] = id;
                    else
                        auditEvent.CustomFields["AspNetUserId"] = id;
                }

                if (entityDict.TryGetValue("AspNetUserId", out var aspNetUserId))
                    auditEvent.CustomFields["AspNetUserId"] = aspNetUserId;
                if (entityDict.TryGetValue("Email", out var email))
                    auditEvent.CustomFields["Email"] = email;
                if (entityDict.TryGetValue("UserName", out var userName))
                    auditEvent.CustomFields["UserName"] = userName;
                if (entityDict.TryGetValue("IsActive", out var isActive))
                    auditEvent.CustomFields["IsActive"] = isActive;
                if (entityDict.TryGetValue("TwoFactorEnabled", out var twoFactorEnabled))
                    auditEvent.CustomFields["TwoFactorEnabled"] = twoFactorEnabled;

                // For AppUserDetailEntity fields
                if (entityDict.TryGetValue("FirstName", out var firstName) &&
                    entityDict.TryGetValue("LastName", out var lastName))
                {
                    auditEvent.CustomFields["FullName"] = $"{firstName} {lastName}".Trim();
                }

                if (entityDict.TryGetValue("Title", out var title))
                    auditEvent.CustomFields["Title"] = title;

                // Security-related fields
                if (entityDict.TryGetValue("RefreshToken", out var refreshToken))
                    auditEvent.CustomFields["HasRefreshToken"] = !string.IsNullOrEmpty(refreshToken?.ToString());
                if (entityDict.TryGetValue("MustChangePassword", out var mustChangePassword))
                    auditEvent.CustomFields["MustChangePassword"] = mustChangePassword;
                if (entityDict.TryGetValue("SsoUserOnly", out var ssoUserOnly))
                    auditEvent.CustomFields["SsoUserOnly"] = ssoUserOnly;
                break;
        }
    }

    /// <summary>
    /// Cache for PersonalDataAttribute lookups per property
    /// </summary>
    private static readonly ConcurrentDictionary<PropertyInfo, bool> _personalDataCache = new();

    /// <summary>
    /// Gets the changes between old and new values for the user entity.
    /// Uses cached property reflection and cached attribute lookups.
    /// </summary>
    public override Dictionary<string, object?> GetChanges(object? oldValues, object? newValues)
    {
        var changes = new Dictionary<string, object?>();

        if (oldValues == null || newValues == null)
            return changes;

        var oldDict = oldValues as Dictionary<string, object>;
        var properties = GetScalarProperties(newValues.GetType());

        foreach (var property in properties)
        {
            if (_sensitiveProperties.Contains(property.Name))
                continue;

            try
            {
                object? oldValue;
                if (oldDict != null)
                {
                    if (!oldDict.TryGetValue(property.Name, out oldValue))
                        continue;
                }
                else
                {
                    oldValue = property.GetValue(oldValues);
                }

                object? newValue = property.GetValue(newValues);

                if (Equals(oldValue, newValue))
                    continue;

                if (IsPersonalData(property))
                {
                    changes[property.Name] = new
                    {
                        Old = MaskPersonalData(oldValue),
                        New = MaskPersonalData(newValue)
                    };
                }
                else
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
                // Skip properties that can't be accessed
            }
        }

        return changes;
    }

    /// <summary>
    /// Determines if a property is marked with the PersonalDataAttribute, using a cache for performance
    /// </summary>
    /// <param name="property"></param>
    /// <returns></returns>
    private static bool IsPersonalData(PropertyInfo property)
    {
        return _personalDataCache.GetOrAdd(property, static p =>
            p.GetCustomAttribute<PersonalDataAttribute>() != null);
    }

    /// <summary>
    /// Masks personal data values for auditing
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    private static object? MaskPersonalData(object? value)
    {
        if (value == null) return null;

        return value switch
        {
            string str when str.Contains("@") => MaskEmail(str),
            string { Length: > 4 } str => $"{str.Substring(0, 2)}***",
            DateTimeOffset dt => dt.ToString("yyyy-MM"),
            _ => "***"
        };
    }

    /// <summary>
    /// Masks an email address for auditing
    /// </summary>
    /// <param name="email"></param>
    /// <returns></returns>
    private static string MaskEmail(string email)
    {
        string[] parts = email.Split('@');
        if (parts.Length != 2) return "***@***";

        string localPart = parts[0].Length > 2 ? $"{parts[0][..2]}***" : "***";
        return $"{localPart}@{parts[1]}";
    }
}
