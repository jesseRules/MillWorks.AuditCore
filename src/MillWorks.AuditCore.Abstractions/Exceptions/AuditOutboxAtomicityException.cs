namespace MillWorks.AuditCore.Abstractions.Exceptions;

/// <summary>
/// Thrown when <c>AuditSinkMode.TransactionalOutbox</c> cannot guarantee that the outbox
/// row commits atomically with the consumer's business write.
/// <para>
/// The transactional outbox is atomic only when one of two conditions holds:
/// the consumer's <c>DbContext</c> maps <c>AuditOutboxEntity</c> (so EF persists it in the
/// same <c>SaveChangesAsync</c> unit of work), or the application has opened an explicit
/// <c>DbContext</c> transaction that the raw-SQL outbox writer can enlist in. When neither
/// holds, the row would commit independently of the business write — so a failed business
/// save could leave an audit row for a change that never happened. For an audit subsystem
/// that is worse than failing loudly, so this is raised instead.
/// </para>
/// <para>
/// This is a configuration/contract violation, not a transient write failure: it propagates
/// regardless of <c>AuditFailureMode</c> and is never swallowed in permissive mode.
/// </para>
/// </summary>
public sealed class AuditOutboxAtomicityException(string message)
    : Exception(message);
