namespace MillWorks.AuditCore.Abstractions.Exceptions;

/// <summary>
/// Thrown by the EF audit interceptor when building audit log records fails and the
/// configured <see cref="Dto.AuditFailureMode"/> dictates fail-closed behavior. Must
/// propagate out of <c>SavingChangesAsync</c> so EF aborts the save and the business
/// transaction rolls back alongside the audit write.
/// </summary>
public sealed class AuditIntegrityException(
    string entityName,
    string action,
    string failureReason,
    Exception innerException)
    : Exception(FormatMessage(entityName, action, failureReason),
        innerException ?? throw new ArgumentNullException(nameof(innerException)))
{
    /// <summary>
    /// CLR type name of the entity that triggered the audit write attempt.
    /// </summary>
    public string EntityName { get; } = entityName;

    /// <summary>
    /// EF entity-state action that triggered the audit write (e.g., "Added", "Modified", "Deleted").
    /// </summary>
    public string Action { get; } = action;

    /// <summary>
    /// Short human-readable description of the failure (e.g., "AuditLogs insert failed").
    /// </summary>
    public string FailureReason { get; } = failureReason;

    /// <summary>
    /// Formats the exception message with contextual details about the audit integrity failure.
    /// </summary>
    /// <param name="entityName"></param>
    /// <param name="action"></param>
    /// <param name="failureReason"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    private static string FormatMessage(string entityName, string action, string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);
        return $"Audit integrity failure for {entityName} ({action}): {failureReason}";
    }
}
