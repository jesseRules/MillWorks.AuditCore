namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Marker for entities that are insert-only: once persisted they may never be updated or
/// deleted through the EF change tracker. Enforced by
/// <c>MillWorks.AuditCore.EntityFramework.Interceptors.AppendOnlyInterceptor</c>.
/// Sanctioned destruction (retention pruning, GDPR/HIPAA erasure) uses ExecuteDelete/ExecuteUpdate,
/// which bypass the change tracker and this guard by design.
/// </summary>
public interface IAppendOnlyEntity;
