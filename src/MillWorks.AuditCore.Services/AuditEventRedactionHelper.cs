using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services;

/// <summary>
/// Creates redacted copies of <see cref="AuditEvent"/> instances for safe storage
/// in failure paths (DLQ, emergency fallback) where raw sensitive data must not persist.
/// </summary>
internal static class AuditEventRedactionHelper
{
    /// <summary>
    /// Returns a new <see cref="AuditEvent"/> with sensitive fields redacted.
    /// The original instance is not modified.
    /// </summary>
    internal static AuditEvent RedactEvent(IAuditFieldRedactor redactor, AuditEvent original)
    {
        return new AuditEvent
        {
            EventId = original.EventId,
            EventType = original.EventType,
            StartDate = original.StartDate,
            EndDate = original.EndDate,
            Environment = original.Environment,
            Target = redactor.RedactTarget(original.Target),
            CustomFields = redactor.RedactFields(original.CustomFields),
            Success = original.Success,
            ErrorMessage = original.ErrorMessage,
            SystemFields = original.SystemFields is not null
                ? redactor.RedactFields(new Dictionary<string, object?>(original.SystemFields))
                : new(),
            CorrelationId = original.CorrelationId,
            ParentId = original.ParentId,
            SessionId = original.SessionId,
            IpAddress = redactor.RedactValue("IpAddress", original.IpAddress),
            UserAgent = redactor.RedactValue("UserAgent", original.UserAgent),
            RequestId = original.RequestId,
            EntityName = original.EntityName,
            Action = original.Action,
            UserId = original.UserId,
            AspNetUserId = original.AspNetUserId,
            KeyValues = redactor.RedactKeyValues(original.KeyValues) ?? new(),
            OldValues = redactor.RedactFields(original.OldValues),
            NewValues = redactor.RedactFields(original.NewValues),
            ChangedProperties = redactor.RedactPropertyNames(original.ChangedProperties) ?? [],
            UserEmail = redactor.RedactValue("UserEmail", original.UserEmail),
        };
    }
}
