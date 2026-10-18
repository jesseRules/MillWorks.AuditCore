using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Sinks.Recovery;

namespace MillWorks.AuditCore.Tests.Sinks;

public sealed partial class OutboxOwnershipTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task RecoveryReceiptRetryRechecksAuthorizationAfterReadingTheReceipt(bool replay)
    {
        var failed = await Fail();
        var original = await Row(failed.Id);
        var operation = Guid.NewGuid();
        var revocation = new RevokeRecoveryAfterRead("AuditOutboxOperations");
        var options = new DbContextOptionsBuilder<AuditDbContext>(_options).AddInterceptors(revocation).Options;
        await using var services = new ServiceCollection().AddSingleton(options)
            .AddScoped(sp => new AuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()))
            .BuildServiceProvider();
        var state = new OutboxStateStore(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        await using var permit = new ReadReceiptPermit(_tenant, revocation);
        Task Apply() => state.ResolveAsync(operation, failed.Id, _tenant, original.FailureCycleId!.Value,
            "fixture resolution", replay, permit, default);
        await Apply();
        revocation.Armed = true;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(Apply);
        Assert.That(revocation.Revoked, Is.True, "Authorization must expire during the receipt read.");
        await using var verification = Db();
        Assert.That(await verification.AuditOutboxResolutions.CountAsync(x => x.Id == operation), Is.EqualTo(1));
        Assert.That(await verification.AuditOutboxOperations.CountAsync(x => x.Id == operation), Is.EqualTo(1));
    }

    [Test]
    public async Task RecoveryRejectedTargetRechecksAuthorizationAfterReadingTheSource()
    {
        var revocation = new RevokeRecoveryAfterRead("AuditOutbox") { Armed = true };
        var options = new DbContextOptionsBuilder<AuditDbContext>(_options).AddInterceptors(revocation).Options;
        await using var services = new ServiceCollection().AddSingleton(options)
            .AddScoped(sp => new AuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()))
            .BuildServiceProvider();
        var state = new OutboxStateStore(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        await using var permit = new ReadReceiptPermit(_tenant, revocation);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => state.ResolveAsync(Guid.NewGuid(), Guid.NewGuid(),
            _tenant, Guid.NewGuid(), "fixture resolution", true, permit, default));
        Assert.That(revocation.Revoked, Is.True, "Authorization must expire during the rejected target read.");
    }

    [Test]
    public async Task RecoveryInspectionRechecksAuthorizationAfterReadingHandoffCounts()
    {
        var failed = await Fail();
        var revocation = new RevokeRecoveryAfterRead("AuditOutboxFailureHandoffs");
        var options = new DbContextOptionsBuilder<AuditDbContext>(_options).AddInterceptors(revocation).Options;
        await using var services = new ServiceCollection().AddSingleton(options)
            .AddScoped(sp => new AuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()))
            .BuildServiceProvider();
        var authorization = new Mock<IAuditOutboxRecoveryAuthorization>();
        authorization.Setup(x => x.AcquireAsync(It.IsAny<AuditOutboxRecoveryTarget>(),
                AuditOutboxRecoveryAction.Inspect, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadReceiptPermit(_tenant, revocation));
        var recovery = new AuditOutboxRecovery(_store, services.GetRequiredService<IServiceScopeFactory>(), authorization.Object);
        revocation.Armed = true;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recovery.InspectAsync(failed.Id, _tenant));
        Assert.That(revocation.Revoked, Is.True, "Authorization must expire after the source-row check.");
    }

    [TestCase("replay")]
    [TestCase("skip")]
    [TestCase("cleanup")]
    [TestCase("completed_cleanup")]
    public async Task RecoveryRevokedAfterReceiptSaveRollsBackSourceProjectionAndEvidence(string action)
    {
        var id = action == "completed_cleanup" ? await CompletedSourceAsync() : (await Fail()).Id;
        if (action == "cleanup")
        {
            var failed = await Row(id);
            await using var scope = _services.CreateAsyncScope();
            var recovery = scope.ServiceProvider.GetRequiredService<IAuditOutboxRecovery>();
            await recovery.SkipAsync(Guid.NewGuid(), id, _tenant, failed.FailureCycleId!.Value, "fixture skip");
            await using var db = Db();
            await db.AuditOutboxResolutions.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTimeOffset.UtcNow.AddDays(-40)));
            await db.AuditOutboxFailureHandoffs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow.AddDays(-40)));
            await db.AuditOutboxOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.VerifyUntil, DateTimeOffset.UtcNow.AddDays(-2)));
        }
        var original = await Row(id);
        await using var before = Db();
        var handoffs = await before.AuditOutboxFailureHandoffs.AsNoTracking()
            .Where(x => x.OutboxRowId == id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Status }).ToArrayAsync();
        var revocation = new RevokeRecoveryAfterSave();
        var options = new DbContextOptionsBuilder<AuditDbContext>(_options).AddInterceptors(revocation).Options;
        await using var services = new ServiceCollection().AddSingleton(options)
            .AddScoped(sp => new AuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()))
            .BuildServiceProvider();
        var state = new OutboxStateStore(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        await using var permit = new SavedReceiptPermit(_tenant, revocation);
        var operation = Guid.NewGuid();
        Task Apply() => action switch
        {
            "replay" or "skip" => state.ResolveAsync(operation, id, _tenant, original.FailureCycleId!.Value,
                "fixture resolution", action == "replay", permit, default),
            "cleanup" => state.CleanupAsync(operation, id, _tenant, original.FailureCycleId!.Value, permit, default),
            _ => state.CleanupCompletedAsync(operation, id, _tenant, DateTimeOffset.UtcNow.AddDays(-30), permit, default)
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(Apply);
        Assert.That(revocation.Saved, Is.True, "The permit must expire after persistence, not before mutation.");
        var retained = await Row(id);
        Assert.Multiple(() =>
        {
            Assert.That(retained.Status, Is.EqualTo(original.Status));
            Assert.That(retained.ResolutionId, Is.EqualTo(original.ResolutionId));
            Assert.That(retained.FailureCycleId, Is.EqualTo(original.FailureCycleId));
            Assert.That(retained.EnvelopeJson, Is.EqualTo(original.EnvelopeJson));
        });
        await using var verification = Db();
        Assert.That(await verification.AuditOutboxOperations.AnyAsync(x => x.Id == operation), Is.False);
        Assert.That(await verification.AuditOutboxResolutions.AnyAsync(x => x.Id == operation), Is.False);
        Assert.That(await verification.AuditOutboxFailureHandoffs.AsNoTracking()
            .Where(x => x.OutboxRowId == id).OrderBy(x => x.Id).Select(x => new { x.Id, x.Status }).ToArrayAsync(),
            Is.EqualTo(handoffs));
    }

    private sealed class RevokeRecoveryAfterSave : SaveChangesInterceptor
    {
        public bool Saved;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            Saved = true;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class RevokeRecoveryAfterRead(string table) : DbCommandInterceptor
    {
        public bool Armed;
        public bool Revoked;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && (command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal) ||
                command.CommandText.Contains($"[{table}]", StringComparison.Ordinal))) Revoked = true;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ReadReceiptPermit(Guid tenantId, RevokeRecoveryAfterRead revocation) : IAuditOutboxRecoveryPermit
    {
        public string ActorId => "trusted-operator";
        public Guid? TenantId => tenantId;
        public ValueTask CheckCurrentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (revocation.Revoked) throw new UnauthorizedAccessException();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SavedReceiptPermit(Guid tenantId, RevokeRecoveryAfterSave revocation) : IAuditOutboxRecoveryPermit
    {
        public string ActorId => "trusted-operator";
        public Guid? TenantId => tenantId;
        public ValueTask CheckCurrentAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (revocation.Saved) throw new UnauthorizedAccessException();
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
