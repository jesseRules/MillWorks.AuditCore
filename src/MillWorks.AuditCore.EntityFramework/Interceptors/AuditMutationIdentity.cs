using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.EntityFramework.Interceptors;

/// <summary>Identity of one entity mutation, including keys not yet assigned by the store.
/// Retain this token outside a context when retrying the same mutation in a replacement context.
/// A token is not proof of business commit and grants no publication authority.</summary>
public sealed class AuditMutationToken
{
    /// <summary>Envelope identity of this mutation, independent of its values and temporary store keys.</summary>
    public Guid Id { get; }

    /// <summary>CLR type name and simple assembly name binding the token to its producer entity.</summary>
    public string EntityType { get; }

    /// <summary>The creation, update or deletion represented by this token.</summary>
    public AuditAction Action { get; }

    /// <summary>Primary-key properties that were temporary when capture began; their values stay unknown on replay.</summary>
    public IReadOnlyList<string> UnresolvedKeyProperties { get; }

    /// <summary>Rehydrates retained mutation identity. Restore validates its type, action and key metadata against EF.</summary>
    public AuditMutationToken(Guid id, string entityType, AuditAction action,
        IReadOnlyList<string> unresolvedKeyProperties)
    {
        if (id == Guid.Empty) throw new ArgumentException("Mutation identity must be non-empty.", nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentNullException.ThrowIfNull(unresolvedKeyProperties);
        if (unresolvedKeyProperties.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Unresolved key names must be non-empty.", nameof(unresolvedKeyProperties));
        if (action is not (AuditAction.Created or AuditAction.Updated or AuditAction.Deleted))
            throw new ArgumentOutOfRangeException(nameof(action));
        Id = id;
        EntityType = entityType;
        Action = action;
        UnresolvedKeyProperties = Array.AsReadOnly(unresolvedKeyProperties.Distinct(StringComparer.Ordinal).ToArray());
    }
}

/// <summary>
/// Owns pending entity-mutation identities. Save failure/cancellation retains them; successful
/// SaveChanges retires them before provider dispatch. Call Capture before a retryable save and
/// Restore after tracking the entity in a replacement context. Without the retained token a
/// replacement context represents a new mutation; equality of values cannot establish a retry.
/// Persist tokens with durable commands when retries must survive a process restart.
/// </summary>
public static class AuditMutationIdentity
{
    private static readonly ConditionalWeakTable<DbContext, Pending> States = new();

    /// <summary>Captures or returns the pending identity after an entity has been tracked as Added, Modified or Deleted.</summary>
    public static AuditMutationToken Capture(DbContext context, object entity)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        var entry = context.Entry(entity);
        var action = ActionFor(entry.State);
        if (action == AuditAction.Unknown)
            throw new InvalidOperationException("Track a pending mutation before capturing its identity.");
        var state = State(context);
        if (state.Tokens.TryGetValue(entity, out var token))
        {
            Validate(context, entity, token);
            return token;
        }

        token = new AuditMutationToken(Guid.CreateVersion7(), TypeIdentity(entry.Entity.GetType()), action,
            entry.Properties.Where(p => entry.State == EntityState.Added && p.Metadata.IsPrimaryKey() && p.IsTemporary)
                .Select(static p => p.Metadata.Name).ToArray());
        state.Tokens.Add(entity, token);
        return token;
    }

    /// <summary>Restores the original token after tracking the same mutation in a replacement context.
    /// The caller must also retain original semantic inputs/provenance and reconcile uncertain business commits.</summary>
    public static void Restore(DbContext context, object entity, AuditMutationToken token)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(token);
        Validate(context, entity, token);
        var state = State(context);
        if (state.Tokens.TryGetValue(entity, out var existing) && existing.Id != token.Id)
            throw new InvalidOperationException("A different mutation is already pending for this entity.");
        if (existing is not null && !existing.UnresolvedKeyProperties.SequenceEqual(token.UnresolvedKeyProperties))
            throw new InvalidOperationException("Pending mutation key provenance cannot change.");
        state.Tokens.Remove(entity);
        state.Tokens.Add(entity, token);
    }

    /// <summary>Whether a key was unavailable when this mutation began. Used by host provenance policies.</summary>
    public static bool IsUnresolvedKey(DbContext context, object entity, string propertyName) =>
        State(context).Tokens.TryGetValue(entity, out var token) &&
        token.UnresolvedKeyProperties.Contains(propertyName);

    internal static void Complete(DbContext? context)
    {
        if (context is not null) States.Remove(context);
    }

    private static void Validate(DbContext context, object entity, AuditMutationToken token)
    {
        var entry = context.Entry(entity);
        if (token.EntityType != TypeIdentity(entry.Entity.GetType()) || token.Action != ActionFor(entry.State))
            throw new InvalidOperationException("Mutation token does not match the tracked entity type/action.");
        foreach (var name in token.UnresolvedKeyProperties)
        {
            var property = entry.Metadata.FindProperty(name);
            if (entry.State != EntityState.Added || property is null || !property.IsPrimaryKey() ||
                (property.ValueGenerated & ValueGenerated.OnAdd) == 0)
                throw new InvalidOperationException(
                    "Mutation token names a key that is not store-generated on creation.");
        }
    }

    private static Pending State(DbContext context)
    {
        var state = States.GetValue(context, static c => new Pending(c.ContextId));
        // Pooled contexts are reused with a new lease; failed work from an earlier lease must not escape.
        if (!state.ContextId.Equals(context.ContextId))
        {
            States.Remove(context);
            state = States.GetValue(context, static c => new Pending(c.ContextId));
        }

        return state;
    }

    private static string TypeIdentity(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

    private static AuditAction ActionFor(EntityState state) => state switch
    {
        EntityState.Added => AuditAction.Created,
        EntityState.Modified => AuditAction.Updated,
        EntityState.Deleted => AuditAction.Deleted,
        _ => AuditAction.Unknown
    };

    private sealed class Pending(DbContextId contextId)
    {
        internal DbContextId ContextId { get; } = contextId;

        // A failed pooled context must not retain detached business objects while idle.
        internal ConditionalWeakTable<object, AuditMutationToken> Tokens { get; } = new();
    }
}
