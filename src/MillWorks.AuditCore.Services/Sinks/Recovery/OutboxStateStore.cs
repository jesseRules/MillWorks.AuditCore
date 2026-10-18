using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;

#pragma warning disable EF1002 // Delimited EF model identifiers only; values are parameters.

namespace MillWorks.AuditCore.Services.Sinks.Recovery;

internal sealed record OutboxClaim(Guid Id, string Token, [property: JsonIgnore] string EnvelopeJson, int EnvelopeVersion,
    int AttemptCount, DateTimeOffset CreatedAt);
internal sealed record OutboxChange(Guid Id, string Token, AuditOutboxStatus Status,
    DateTimeOffset? NextRetryAt = null, string ReasonCode = "write_failed");
internal sealed record HandoffClaim(Guid Id, Guid OutboxRowId, string Token, [property: JsonIgnore] string EnvelopeJson,
    string ReasonCode, int AttemptCount);
internal sealed record OutboxOperationResult(Guid OperationId, Guid[] Applied, Guid[] Rejected,
    OutboxClaim[] Claims, HandoffClaim[] Handoffs, bool ExternalDeliveryMayBeInFlight = false)
{
    public static OutboxOperationResult Empty(Guid id) => new(id, [], [], [], []);
}

/// <summary>An ambiguous result is recoverable using this identity; it is not ownership loss.</summary>
public sealed class AuditOutboxCommitUnknownException(Guid operationId, Exception inner)
    : Exception($"Outbox operation {operationId} could not be reconciled. Retain its identity and retry verification.", inner)
{
    public Guid OperationId { get; } = operationId;
}

/// <summary>Domain-owned guarded mutations. Each operation uses a separate scope/transaction;
/// no writer context can flush outbox state. Callers allocate IDs outside strategy retries.</summary>
internal sealed partial class OutboxStateStore(IServiceScopeFactory scopes, TimeProvider clock) : IOutboxStateStore
{
    internal const int MaxBatchSize = 128;
    internal static readonly TimeSpan VerificationRetention = TimeSpan.FromHours(24);

    internal async Task<OutboxOperationResult> ExecuteAsync(Guid operationId, string kind, object request,
        Func<AuditDbContext, CancellationToken, Task<OutboxOperationResult>> mutate, CancellationToken ct,
        IAuditOutboxRecoveryPermit? commitPermit = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation identity is required.", nameof(operationId));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + JsonSerializer.Serialize(request))));
        // Never join a caller transaction whose eventual rollback would invalidate our receipt.
        using var ambient = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        await using var strategyScope = scopes.CreateAsyncScope();
        var strategy = strategyScope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async _ =>
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var existing = await db.AuditOutboxOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct);
            if (existing is not null)
            {
                var retained = await ReadReceiptAsync(db, existing, kind, hash, ct);
                if (commitPermit is not null) await commitPermit.CheckCurrentAsync(ct);
                return retained;
            }
            await using var transaction = await db.Database.BeginTransactionAsync(
                db.Database.IsSqlServer() ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.Serializable, ct);
            // Serialize only retries of this operation, not a GUID index gap shared with
            // unrelated operations. The transaction-owned lock releases on rollback/commit.
            if (db.Database.IsSqlServer())
            {
                var tableHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Table<AuditOutboxOperation>(db))));
                var resource = $"audit:outbox:{tableHash}:{operationId:N}";
                await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource={0}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=5000; IF @result < 0 THROW 51000, 'Outbox operation lock unavailable', 1;", [resource], ct);
            }
            existing = await db.AuditOutboxOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct);
            if (existing is not null)
            {
                var retained = await ReadReceiptAsync(db, existing, kind, hash, ct);
                if (commitPermit is not null) await commitPermit.CheckCurrentAsync(ct);
                return retained;
            }
            var result = await mutate(db, ct);
            if (result.Applied.Length == 0 && !db.ChangeTracker.HasChanges())
            {
                if (commitPermit is not null) await commitPermit.CheckCurrentAsync(ct);
                return result; // No mutation to reconcile; idle polling must not grow the receipt table.
            }
            var now = await NowAsync(db, ct);
            db.AuditOutboxOperations.Add(new AuditOutboxOperation
            {
                Id = operationId, Kind = kind, RequestHash = hash, ResultJson = JsonSerializer.Serialize(result),
                CreatedAt = now, VerifyUntil = now + VerificationRetention
            });
            await db.SaveChangesAsync(ct);
            // Receipt/resolution persistence can await after the guarded mutation. A revoked
            // operator or policy must still roll the entire operation back before commit.
            if (commitPermit is not null) await commitPermit.CheckCurrentAsync(ct);
            try
            {
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception commitFailure)
            {
                // Release an uncommitted transaction before verifying on an independent connection.
                // If COMMIT succeeded this disposal cannot undo it. Do not use the canceled token
                // for verification, and never translate an unavailable verifier into false failure.
                try { await transaction.DisposeAsync(); }
                catch (Exception cleanup) { commitFailure.Data["OutboxTransactionCleanup"] = cleanup; }
                try
                {
                    using var verifyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await using var verificationScope = scopes.CreateAsyncScope();
                    var verifier = verificationScope.ServiceProvider.GetRequiredService<AuditDbContext>();
                    verifier.Database.SetCommandTimeout(5);
                    var receipt = await verifier.AuditOutboxOperations.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.Id == operationId, verifyDeadline.Token);
                    if (receipt is not null) return await ReadReceiptAsync(verifier, receipt, kind, hash, verifyDeadline.Token);
                }
                catch (Exception verificationFailure)
                {
                    throw new AuditOutboxCommitUnknownException(operationId, new AggregateException(commitFailure, verificationFailure));
                }
                // A successful read found no receipt: the transaction did not commit.
                // The configured provider strategy decides whether this error is retryable.
                throw;
            }
        }, ct);
    }

    private static async Task<OutboxOperationResult> ReadReceiptAsync(AuditDbContext db, AuditOutboxOperation receipt,
        string kind, string hash, CancellationToken ct)
    {
        if (receipt.Kind != kind || receipt.RequestHash != hash)
            throw new AuditOutboxOperationConflictException(receipt.Id);
        var result = JsonSerializer.Deserialize<OutboxOperationResult>(receipt.ResultJson)
            ?? throw new InvalidOperationException("Invalid durable outbox operation receipt.");
        // Immutable receipts retain result identities, not a second copy of governed payloads.
        // Source/projection payloads remain unchanged until authorized post-window cleanup.
        if (result.Claims.Length != 0)
        {
            var ids = result.Claims.Select(x => x.Id).ToArray();
            var payloads = await db.AuditOutbox.AsNoTracking().Where(x => ids.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.EnvelopeJson, ct);
            if (ids.Any(x => !payloads.ContainsKey(x)))
                throw new InvalidOperationException("Claim committed, but its payload has since been disposed. Do not repeat the audit operation.");
            result = result with { Claims = result.Claims.Select(x => x with { EnvelopeJson = payloads[x.Id] }).ToArray() };
        }
        if (result.Handoffs.Length != 0)
        {
            var ids = result.Handoffs.Select(x => x.Id).ToArray();
            var payloads = await db.AuditOutboxFailureHandoffs.AsNoTracking().Where(x => ids.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.EnvelopeJson, ct);
            if (ids.Any(x => !payloads.ContainsKey(x)))
                throw new InvalidOperationException("Handoff claim committed, but its payload has since been disposed.");
            result = result with { Handoffs = result.Handoffs.Select(x => x with { EnvelopeJson = payloads[x.Id] }).ToArray() };
        }
        return result;
    }

    internal async Task<OutboxOperationResult?> VerifyAsync(Guid operationId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var receipt = await db.AuditOutboxOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, ct);
        return receipt is null ? null : JsonSerializer.Deserialize<OutboxOperationResult>(receipt.ResultJson);
    }

    private async Task<DateTimeOffset> NowAsync(AuditDbContext db, CancellationToken ct) => db.Database.IsSqlServer()
        ? await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT SYSDATETIMEOFFSET() AS [Value]").SingleAsync(ct)
        : clock.GetUtcNow();

    private static string Table<T>(AuditDbContext db) where T : class
    {
        var entity = db.Model.FindEntityType(typeof(T))!;
        return db.GetService<ISqlGenerationHelper>().DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());
    }

    private static IQueryable<AuditOutboxEntity> LockedRows(AuditDbContext db) => db.Database.IsSqlServer()
        ? db.AuditOutbox.FromSqlRaw($"SELECT * FROM {Table<AuditOutboxEntity>(db)} WITH (UPDLOCK, ROWLOCK)").AsNoTracking()
        : db.AuditOutbox.AsNoTracking();
    private static IQueryable<AuditOutboxFailureHandoff> LockedHandoffs(AuditDbContext db) => db.Database.IsSqlServer()
        ? db.AuditOutboxFailureHandoffs.FromSqlRaw($"SELECT * FROM {Table<AuditOutboxFailureHandoff>(db)} WITH (UPDLOCK, ROWLOCK)").AsNoTracking()
        : db.AuditOutboxFailureHandoffs.AsNoTracking();
    private static int BatchSize(int size) => size is > 0 and <= MaxBatchSize ? size
        : throw new ArgumentOutOfRangeException(nameof(size), $"Batch size must be in [1, {MaxBatchSize}].");
    private static Guid HandoffId(Guid operation, Guid row) => new(SHA256.HashData(operation.ToByteArray().Concat(row.ToByteArray()).ToArray()).AsSpan(0, 16));

    public Task<OutboxOperationResult> ClaimAsync(Guid operation, int size, TimeSpan duration, CancellationToken ct)
    {
        BatchSize(size);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var token = operation.ToString("N");
        DateTimeOffset? asOf = null;
        return ExecuteAsync(operation, "claim", new { size, duration }, async (db, cancel) =>
        {
            var now = asOf ??= await NowAsync(db, cancel);
            var expiry = now + duration;
            var rows = db.Database.IsSqlServer()
                ? await db.AuditOutbox.FromSqlRaw($"SELECT TOP ({{0}}) * FROM {Table<AuditOutboxEntity>(db)} WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK) WHERE [Status] = 0 AND ([NextRetryAt] IS NULL OR [NextRetryAt] <= {{1}}) AND ([LeaseExpiresAt] IS NULL OR [LeaseExpiresAt] <= {{1}}) ORDER BY [CreatedAt], [Id]", size, now).AsNoTracking().ToListAsync(cancel)
                : await db.AuditOutbox.AsNoTracking().Where(x => x.Status == AuditOutboxStatus.Pending &&
                        (x.NextRetryAt == null || x.NextRetryAt <= now) &&
                        (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))
                    .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(size).ToListAsync(cancel);
            var ids = rows.Select(x => x.Id).ToArray();
            if (ids.Length != 0)
            {
                var count = await db.AuditOutbox.Where(x => ids.Contains(x.Id) && x.Status == AuditOutboxStatus.Pending &&
                        (x.NextRetryAt == null || x.NextRetryAt <= now) && (x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AuditOutboxStatus.InFlight)
                        .SetProperty(x => x.LeaseOwner, token).SetProperty(x => x.LeaseExpiresAt, expiry), cancel);
                if (count != ids.Length) throw new DBConcurrencyException("Claim eligibility changed; transaction rolled back.");
            }
            return new(operation, ids, [], rows.Select(x => new OutboxClaim(x.Id, token, x.EnvelopeJson,
                x.EnvelopeVersion, x.AttemptCount, x.CreatedAt)).ToArray(), []);
        }, ct);
    }

    public Task<OutboxOperationResult> ApplyAsync(Guid operation, IReadOnlyList<OutboxChange> changes, CancellationToken ct)
    {
        if (changes.Count > MaxBatchSize || changes.Select(x => x.Id).Distinct().Count() != changes.Count)
            throw new ArgumentException("Outcome IDs must be unique and bounded.", nameof(changes));
        if (changes.Any(x => string.IsNullOrWhiteSpace(x.Token) || x.Status == AuditOutboxStatus.InFlight ||
                !Enum.IsDefined(x.Status) || x.ReasonCode.Length > 80))
            throw new ArgumentException("Invalid guarded outcome.", nameof(changes));
        var ordered = changes.OrderBy(x => x.Id).ToArray();
        return ExecuteAsync(operation, "apply", ordered, async (db, cancel) =>
        {
            var now = await NowAsync(db, cancel);
            var applied = new List<Guid>();
            // UPDLOCK is retained through the transaction so selected IDs are exact mutation
            // evidence, even when another worker tries to recover the same rows concurrently.
            foreach (var group in ordered.GroupBy(x => new { x.Token, x.Status, x.NextRetryAt, x.ReasonCode }))
            {
                var ids = group.Select(x => x.Id).ToArray();
                var owned = await LockedRows(db).Where(x => ids.Contains(x.Id) && x.Status == AuditOutboxStatus.InFlight && x.LeaseOwner == group.Key.Token)
                    .OrderBy(x => x.Id).ToListAsync(cancel);
                var ownedIds = owned.Select(x => x.Id).ToArray();
                if (ownedIds.Length == 0) continue;
                var target = group.Key.Status;
                var increment = target == AuditOutboxStatus.Completed ? 0 : 1;
                DateTimeOffset? completed = target == AuditOutboxStatus.Completed ? now : null;
                Guid? cycle = target == AuditOutboxStatus.Failed ? operation : null;
                var count = await db.AuditOutbox.Where(x => ownedIds.Contains(x.Id) && x.Status == AuditOutboxStatus.InFlight && x.LeaseOwner == group.Key.Token)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, target).SetProperty(x => x.LeaseOwner, (string?)null)
                        .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null).SetProperty(x => x.CompletedAt, completed)
                        .SetProperty(x => x.NextRetryAt, group.Key.NextRetryAt).SetProperty(x => x.AttemptCount, x => x.AttemptCount + increment)
                        .SetProperty(x => x.LastError, target == AuditOutboxStatus.Completed ? null : group.Key.ReasonCode)
                        .SetProperty(x => x.FailureCycleId, cycle).SetProperty(x => x.ResolutionId, (Guid?)null), cancel);
                if (count != ownedIds.Length) throw new DBConcurrencyException("Guarded outcome changed; transaction rolled back.");
                if (target == AuditOutboxStatus.Failed)
                foreach (var row in owned)
                {
                    var tenant = QueuedEnvelopeValidator.TenantId(row.EnvelopeJson);
                    await db.AuditOutbox.Where(x => x.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FailureTenantId, tenant), cancel);
                    db.AuditOutboxFailureHandoffs.Add(new AuditOutboxFailureHandoff
                    {
                        Id = HandoffId(operation, row.Id), OutboxRowId = row.Id, FailureCycleId = operation,
                        TenantId = tenant, EnvelopeJson = row.EnvelopeJson, ReasonCode = group.Key.ReasonCode,
                        CreatedAt = now, Status = AuditOutboxHandoffStatus.Pending
                    });
                }
                applied.AddRange(ownedIds);
            }
            return new(operation, applied.ToArray(), ordered.Select(x => x.Id).Except(applied).ToArray(), [], []);
        }, ct);
    }

    public Task<OutboxOperationResult> RecoverAsync(Guid operation, int size, CancellationToken ct)
    {
        BatchSize(size);
        DateTimeOffset? asOf = null;
        return ExecuteAsync(operation, "recover", new { size }, async (db, cancel) =>
        {
            var now = asOf ??= await NowAsync(db, cancel);
            var rows = await LockedRows(db).Where(x => x.Status == AuditOutboxStatus.InFlight && x.LeaseExpiresAt != null && x.LeaseExpiresAt <= now)
                .OrderBy(x => x.LeaseExpiresAt).ThenBy(x => x.Id).Take(size).Select(x => x.Id).ToArrayAsync(cancel);
            if (rows.Length != 0)
            {
                var count = await db.AuditOutbox.Where(x => rows.Contains(x.Id) && x.Status == AuditOutboxStatus.InFlight && x.LeaseExpiresAt <= now)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AuditOutboxStatus.Pending)
                        .SetProperty(x => x.LeaseOwner, (string?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), cancel);
                if (count != rows.Length) throw new DBConcurrencyException("Recovery changed; transaction rolled back.");
            }
            return new(operation, rows, [], [], []);
        }, ct);
    }
}
