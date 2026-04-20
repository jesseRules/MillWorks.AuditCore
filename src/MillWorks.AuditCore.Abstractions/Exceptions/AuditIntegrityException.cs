namespace MillWorks.AuditCore.Abstractions.Exceptions;

/// <summary>
/// Thrown by the EF audit interceptor when building audit log records fails and the
/// configured <see cref="Dto.AuditFailureMode"/> dictates fail-closed behavior. Must
/// propagate out of <c>SavingChangesAsync</c> so EF aborts the save and the business
/// transaction rolls back alongside the audit write.
/// </summary>
public sealed class AuditIntegrityException : Exception
{
    /// <summary>
    /// CLR type name of the entity that triggered the audit write attempt.
    /// </summary>
    public string EntityName { get; }

    /// <summary>
    /// EF entity-state action that triggered the audit write (e.g., "Added", "Modified", "Deleted").
    /// </summary>
    public string Action { get; }

    /// <summary>
    /// Short human-readable description of the failure (e.g., "AuditLogs insert failed").
    /// </summary>
    public string FailureReason { get; }

    public AuditIntegrityException(
        string entityName,
        string action,
        string failureReason,
        Exception innerException)
        : base(
            FormatMessage(entityName, action, failureReason),
            innerException ?? throw new ArgumentNullException(nameof(innerException)))
    {
        EntityName = entityName;
        Action = action;
        FailureReason = failureReason;
    }

    private static string FormatMessage(string entityName, string action, string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        return $"Audit integrity failure for {entityName} ({action}): {failureReason}";
    }
}
