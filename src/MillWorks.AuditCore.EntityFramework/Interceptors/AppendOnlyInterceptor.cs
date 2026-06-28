using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Interceptors;

/// <summary>
/// EF Core SaveChanges interceptor that blocks modification or deletion of any
/// <see cref="IAppendOnlyEntity"/>. A marked entity entering Modified or Deleted state
/// makes the save throw. ExecuteUpdate/ExecuteDelete and raw SQL bypass the change tracker
/// and this guard by design.
/// </summary>
public sealed class AppendOnlyInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ValidateAppendOnly(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken ct = default)
    {
        ValidateAppendOnly(eventData.Context);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    private static void ValidateAppendOnly(DbContext? context)
    {
        if (context is null) return;
        var violations = context.ChangeTracker.Entries()
            .Where(static e => e is { Entity: IAppendOnlyEntity, State: EntityState.Modified or EntityState.Deleted })
            .ToList();
        if (violations.Count > 0)
            throw new InvalidOperationException(
                $"Cannot modify or delete append-only entity: {violations[0].Entity.GetType().Name}. " +
                "This entity type is insert-only.");
    }
}
