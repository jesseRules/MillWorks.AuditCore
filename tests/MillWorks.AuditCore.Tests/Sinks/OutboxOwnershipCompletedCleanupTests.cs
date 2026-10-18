using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Sinks.Identity;
using MillWorks.AuditCore.Services.Sinks.Recovery;

namespace MillWorks.AuditCore.Tests.Sinks;

public sealed partial class OutboxOwnershipTests
{
    private async Task<Guid> CompletedSourceAsync()
    {
        var id = (await Seed()).Single();
        var claim = (await Claim()).Claims.Single();
        await _store.ApplyAsync(Guid.NewGuid(), [new(id, claim.Token, AuditOutboxStatus.Completed)], default);
        await using var db = Db();
        var row = await db.AuditOutbox.AsNoTracking().SingleAsync(x => x.Id == id);
        var envelope = QueuedEnvelopeValidator.Read(row.EnvelopeJson, row.EnvelopeVersion);
        db.Set<AuditEnvelopeIdentity>().Add(new AuditEnvelopeIdentity
        {
            Channel = "outbox", LogicalId = row.IdempotencyKey, Kind = AuditEnvelopeKind.ExplicitEvent,
            TenantId = _tenant, ContentVersion = AuditEnvelopeContent.Version,
            Fingerprint = AuditEnvelopeContent.Fingerprint(envelope), AcceptedAt = DateTimeOffset.UtcNow.AddDays(-40)
        });
        await db.SaveChangesAsync();
        await db.AuditOutbox.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow.AddDays(-40)));
        await db.AuditOutboxOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.VerifyUntil, DateTimeOffset.UtcNow.AddDays(-2)));
        return id;
    }

    [TestCase("missing_receipt")]
    [TestCase("foreign_tenant")]
    [TestCase("recent_completion")]
    [TestCase("verification_window")]
    [TestCase("failure_history")]
    [TestCase("handoff")]
    public async Task CompletedCleanupRejectsMissingAuthorityEvidenceAndProtectedState(string fault)
    {
        var id = await CompletedSourceAsync();
        await using (var db = Db())
        {
            if (fault == "missing_receipt") await db.Set<AuditEnvelopeIdentity>().ExecuteDeleteAsync();
            if (fault == "recent_completion") await db.AuditOutbox.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow.AddHours(-1)));
            if (fault == "verification_window") await db.AuditOutboxOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.VerifyUntil, DateTimeOffset.UtcNow.AddHours(1)));
            if (fault == "failure_history") await db.AuditOutbox.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.FailureCycleId, Guid.NewGuid()));
            if (fault == "handoff")
            {
                var row = await db.AuditOutbox.SingleAsync(x => x.Id == id);
                db.AuditOutboxFailureHandoffs.Add(new AuditOutboxFailureHandoff
                {
                    Id = Guid.NewGuid(), OutboxRowId = id, FailureCycleId = Guid.NewGuid(),
                    EnvelopeJson = row.EnvelopeJson, ReasonCode = "fixture", Status = AuditOutboxHandoffStatus.Delivered,
                    CreatedAt = DateTimeOffset.UtcNow.AddDays(-40), CompletedAt = DateTimeOffset.UtcNow.AddDays(-40)
                });
                await db.SaveChangesAsync();
            }
        }
        await using var scope = _services.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<IAuditOutboxRecovery>();
        Assert.That(await recovery.CleanupCompletedAsync(Guid.NewGuid(), id,
            fault == "foreign_tenant" ? Guid.NewGuid() : _tenant, DateTimeOffset.UtcNow.AddDays(1)), Is.False);
        Assert.That((await Row(id)).Status, Is.EqualTo(AuditOutboxStatus.Completed));
    }

    [TestCase("before")]
    [TestCase("after")]
    public async Task CompletedCleanupReconcilesCommitFaultsAndRetainsIdentityEvidence(string position)
    {
        var id = await CompletedSourceAsync();
        var original = await Row(id);
        var operation = Guid.NewGuid(); var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        await using var scope = _services.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<IAuditOutboxRecovery>();
        _fault.Arm(position);
        try { await recovery.CleanupCompletedAsync(operation, id, _tenant, cutoff); }
        catch (TimeoutException) when (position == "before") { }
        Assert.That(await recovery.CleanupCompletedAsync(operation, id, _tenant, cutoff), Is.True);
        Assert.That(_fault.Faults, Is.EqualTo(1));
        await using var verify = Db();
        Assert.That(await verify.AuditOutbox.AnyAsync(x => x.Id == id), Is.False);
        Assert.That(await verify.Set<AuditEnvelopeIdentity>().AnyAsync(x => x.Channel == "outbox" && x.LogicalId == original.IdempotencyKey), Is.True);
        Assert.That(await verify.AuditOutboxOperations.CountAsync(x => x.Id == operation), Is.EqualTo(1));
    }

    [Test]
    public async Task CompletedCleanupRequiresCurrentHostPermit()
    {
        var id = await CompletedSourceAsync();
        await using var scope = _services.CreateAsyncScope();
        var recovery = scope.ServiceProvider.GetRequiredService<IAuditOutboxRecovery>();
        _authorization.ExpireAfterAcquire = true;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recovery.CleanupCompletedAsync(
            Guid.NewGuid(), id, _tenant, DateTimeOffset.UtcNow.AddDays(-30)));
        Assert.That((await Row(id)).Status, Is.EqualTo(AuditOutboxStatus.Completed));
    }
}
