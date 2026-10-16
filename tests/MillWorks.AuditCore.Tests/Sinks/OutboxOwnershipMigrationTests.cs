using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Sinks.Recovery;

namespace MillWorks.AuditCore.Tests.Sinks;

public sealed partial class OutboxOwnershipTests
{
    [Test]
    public async Task UnavailableCommitVerifierReturnsUnknownIdentityWithoutRepeatingTerminalMutation()
    {
        await Seed(); var claim = (await Claim()).Claims.Single(); var operation = Guid.NewGuid();
        _fault.Arm("after"); _commands.FailReceiptVerification = true;
        try
        {
            await Assert.ThatAsync(() => _store.ApplyAsync(operation,
                    [new(claim.Id, claim.Token, AuditOutboxStatus.Failed)], default),
                Throws.TypeOf<AuditOutboxCommitUnknownException>().With.Property(nameof(AuditOutboxCommitUnknownException.OperationId)).EqualTo(operation));
        }
        finally { _commands.FailReceiptVerification = false; }
        var evidence = await _store.VerifyAsync(operation, default);
        Assert.That(evidence!.Applied, Is.EqualTo(new[] { claim.Id }));
        Assert.That((await Row(claim.Id)).AttemptCount, Is.EqualTo(1));
        await using var db = Db(); Assert.That(await db.AuditOutboxFailureHandoffs.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task IncrementalMigrationsPreserveOldRowsAndSupportGuardedFailure()
    {
        if (provider != "sql") Assert.Ignore("Owning SQL Server incremental migration.");
        var name = _database + "_migration";
        await SqlAsync($"CREATE DATABASE [{name}]; ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT {(rcsi ? "ON" : "OFF")};");
        try
        {
            var builder = new SqlConnectionStringBuilder(_master) { InitialCatalog = name };
            var opts = new DbContextOptionsBuilder<AuditDbContext>().UseSqlServer(builder.ConnectionString).Options;
            var id = Guid.NewGuid();
            await using (var db = new AuditDbContext(opts))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261008124628_AddAuditLogEnvelopeLookupIndex");
                await db.Database.ExecuteSqlRawAsync("INSERT INTO [audit].[AuditOutbox] (Id, IdempotencyKey, EnvelopeJson, EnvelopeVersion, Status, CreatedAt, AttemptCount) VALUES ({0},{1},{2},1,0,{3},0)",
                    id, id, Payload(), DateTimeOffset.UtcNow);
                await db.Database.MigrateAsync();
                Assert.That(await db.AuditOutbox.CountAsync(x => x.Id == id && x.FailureCycleId == null), Is.EqualTo(1));
                Assert.That(await db.Database.GetAppliedMigrationsAsync(), Does.Contain("20261016211640_RetainOutboxFailureSources"));
            }
            await using var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddScoped(_ => new AuditDbContext(opts))
                .AddSingleton<OutboxStateStore>().BuildServiceProvider();
            var store = services.GetRequiredService<OutboxStateStore>(); var claim = (await store.ClaimAsync(Guid.NewGuid(), 1, TimeSpan.FromMinutes(1), default)).Claims.Single();
            var result = await store.ApplyAsync(Guid.NewGuid(), [new(claim.Id, claim.Token, AuditOutboxStatus.Failed)], default);
            Assert.That(result.Applied, Is.EqualTo(new[] { id }));
        }
        finally
        {
            await SqlAsync($"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
            TestContext.Out.WriteLine($"Dropped owned migration fixture {name}");
        }
    }
}

public sealed partial class OutboxOwnershipTests
{
    [Test]
    public async Task ReceiptEvidenceUsesTheAppendOnlyGuard()
    {
        await Seed(); await Claim();
        var options = new DbContextOptionsBuilder<AuditDbContext>(_options)
            .AddInterceptors(new MillWorks.AuditCore.EntityFramework.Interceptors.AppendOnlyInterceptor()).Options;
        await using var db = new AuditDbContext(options);
        var receipt = await db.AuditOutboxOperations.FirstAsync(); receipt.ResultJson = "tampered";
        await Assert.ThatAsync(() => db.SaveChangesAsync(), Throws.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task SuccessfullyReplayedSourceCanBeDisposedAfterAllVerificationWindows()
    {
        var claim = await Fail(); var failed = await Row(claim.Id);
        await using var scope = _services.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<IAuditOutboxRecovery>();
        await recovery.ReplayAsync(Guid.NewGuid(), claim.Id, _tenant, failed.FailureCycleId!.Value, "approved replay");
        var next = (await Claim()).Claims.Single();
        await _store.ApplyAsync(Guid.NewGuid(), [new(next.Id, next.Token, AuditOutboxStatus.Completed)], default);
        Assert.That(await recovery.CleanupAsync(Guid.NewGuid(), next.Id, _tenant, failed.FailureCycleId.Value), Is.False);
        await using (var db = Db())
        {
            var old = DateTimeOffset.UtcNow.AddDays(-2);
            await db.AuditOutboxOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.VerifyUntil, old));
            await db.AuditOutboxResolutions.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, old));
            await db.AuditOutboxFailureHandoffs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, old));
            await db.AuditOutbox.ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, old));
        }
        Assert.That(await recovery.CleanupAsync(Guid.NewGuid(), next.Id, _tenant, failed.FailureCycleId.Value), Is.True);
    }
}
