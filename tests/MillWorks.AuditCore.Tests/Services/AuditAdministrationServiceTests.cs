using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Administration;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.Tests.Services;

[TestFixture]
public sealed class AuditAdministrationServiceTests
{
    private AuditDbContext db = null!;
    private readonly Guid tenant = Guid.NewGuid(), actor = Guid.NewGuid(), subject = Guid.NewGuid();
    private Mock<IAuditArchivalService> archival = null!;
    private Mock<IAuditAdministrationAuthority> authority = null!;
    private Mock<IAuditAdministrationPermit> permit = null!;

    [SetUp]
    public void SetUp()
    {
        db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        archival = new(MockBehavior.Strict);
        authority = new(MockBehavior.Strict);
        permit = new(MockBehavior.Strict);
        permit.SetupGet(p => p.Scope).Returns(new AuditAdministrationScope(tenant, actor, Guid.NewGuid().ToString()));
        permit.Setup(p => p.CheckCurrentAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        permit.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        authority.Setup(a => a.AcquireAsync(It.IsAny<AuditAdministrationTarget>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(permit.Object);
    }

    [TearDown]
    public async Task TearDown() => await db.DisposeAsync();

    private AuditAdministrationService Service(bool integrity = false, bool registered = true) =>
        new(db, archival.Object, Options.Create(new SecurityOptions { EnableTamperDetection = integrity }),
            registered ? authority.Object : null);

    private AuditEventEntity Event(Guid owner, Guid user) => new()
    {
        TenantId = owner, UserId = user, EventType = "secret-event-text", User = "secret@example.test",
        UserFullName = "Private name", IpAddress = "10.0.0.1", UserAgent = "private-agent",
        JsonData = "{\"password\":\"secret-payload\"}", AdditionalData = "secret-additional",
        EntityId = "secret-natural-key", InsertedDate = DateTimeOffset.UtcNow,
        IntegrityStatus = IntegrityStatus.Completed
    };

    [Test]
    public async Task MissingOrDeniedAuthorityDoesNotReadExportOrMutate()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(registered: false).ExportAsync(subject));
        authority.Setup(a => a.AcquireAsync(It.IsAny<AuditAdministrationTarget>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().AnonymizeAsync(subject));
        Assert.That(await db.SecurityEvents.CountAsync(), Is.Zero);
        archival.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ExportFiltersTenantAndSubjectAndSuppressesAllPayloads()
    {
        var allowed = Event(tenant, subject);
        db.AddRange(allowed, Event(Guid.NewGuid(), subject), Event(tenant, Guid.NewGuid()), Event(Guid.Empty, subject));
        await db.SaveChangesAsync();
        var result = await Service().ExportAsync(subject);
        Assert.That(result.RecordCount, Is.EqualTo(1));
        var content = Encoding.UTF8.GetString(result.Data);
        Assert.That(content, Does.Contain(allowed.EventId.ToString()));
        Assert.That(content, Does.Not.Contain("secret"));
        Assert.That(content, Does.Not.Contain("Private name"));
        var evidence = await db.SecurityEvents.ToListAsync();
        Assert.That(evidence, Has.Count.EqualTo(2));
        Assert.That(evidence.All(e => e.ActorUserId == actor && e.TenantId == tenant && e.SubjectUserId == subject), Is.True);
        Assert.That(evidence.All(e => !e.DetailsJson!.Contains("secret")), Is.True);
        permit.Verify(p => p.CheckCurrentAsync(It.IsAny<CancellationToken>()), Times.Once);
        permit.Verify(p => p.DisposeAsync(), Times.Once);
    }

    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public async Task ProtectedEventsCannotBeAnonymized(bool securityEnabled, bool proof, bool pending)
    {
        var row = Event(tenant, subject);
        db.Add(row);
        if (proof) db.AuditIntegrity.Add(new AuditIntegrityEntity { EventId = row.EventId });
        if (pending) db.IntegrityWorkItems.Add(new AuditIntegrityWorkItemEntity { EventId = row.EventId });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<AuditAdministrationConflictException>(() => Service(securityEnabled).AnonymizeAsync(subject));
        db.ChangeTracker.Clear();
        var retained = await db.AuditEvents.SingleAsync();
        Assert.That(retained.JsonData, Is.EqualTo(row.JsonData));
        Assert.That(retained.UserFullName, Is.EqualTo("Private name"));
        Assert.That(await db.SecurityEvents.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task UnprotectedAnonymizationTouchesOnlyAuthorizedTenantSubject()
    {
        var row = Event(tenant, subject);
        var foreign = Event(Guid.NewGuid(), subject);
        db.AddRange(row, foreign);
        await db.SaveChangesAsync();
        var result = await Service().AnonymizeAsync(subject);
        Assert.That(result.EventCount, Is.EqualTo(1));
        db.ChangeTracker.Clear();
        var changed = await db.AuditEvents.SingleAsync(e => e.EventId == row.EventId);
        Assert.That(changed.JsonData, Is.Null);
        Assert.That(changed.IpAddress, Is.Null);
        Assert.That(changed.UserId, Is.EqualTo(subject));
        Assert.That((await db.AuditEvents.SingleAsync(e => e.EventId == foreign.EventId)).JsonData, Is.Not.Null);
    }

    [Test]
    public async Task ArchiveAndRestorePassOnlyTrustedTenantAndActor()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
        archival.Setup(a => a.ArchiveTenantEventsAsync(tenant, actor, cutoff, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MillWorks.AuditCore.Abstractions.Dto.AuditArchivalResult { Success = true });
        archival.Setup(a => a.RestoreTenantEventsAsync(tenant, "owned-archive", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MillWorks.AuditCore.Abstractions.Dto.AuditRestoreResult { Success = true });
        await Service().ArchiveAsync(cutoff);
        await Service().RestoreAsync("owned-archive");
        archival.VerifyAll();
        archival.VerifyNoOtherCalls();
        authority.Verify(a => a.AcquireAsync(new AuditAdministrationTarget(AuditAdministrationOperation.Archive, null, null, cutoff, null), It.IsAny<CancellationToken>()), Times.Once);
        authority.Verify(a => a.AcquireAsync(new AuditAdministrationTarget(AuditAdministrationOperation.Restore, null, "owned-archive", null, null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void ArchiveMappingPreservesTenantSubjectAndProofInputs()
    {
        var row = Event(tenant, subject);
        var restored = AuditArchivedEvent.From(row).ToEntity();
        Assert.That(restored.TenantId, Is.EqualTo(tenant));
        Assert.That(restored.UserId, Is.EqualTo(subject));
        Assert.That(restored.JsonData, Is.EqualTo(row.JsonData));
        Assert.That(restored.User, Is.EqualTo(row.User));
        Assert.That(restored.IntegrityStatus, Is.EqualTo(row.IntegrityStatus));
    }

    [Test]
    public async Task ProofExportRequiresTenantOwnershipBeforeCallingProofService()
    {
        var row = Event(tenant, subject);
        var foreign = Event(Guid.NewGuid(), subject);
        db.AddRange(row, foreign);
        db.AuditIntegrity.Add(new AuditIntegrityEntity { EventId = row.EventId });
        await db.SaveChangesAsync();
        var proofs = new Mock<MillWorks.AuditCore.Services.TamperDetection.Interfaces.ITamperDetectionService>(MockBehavior.Strict);
        proofs.Setup(p => p.ExportIntegrityProofAsync(row.EventId, It.IsAny<CancellationToken>())).ReturnsAsync(new byte[] { 1, 2 });
        var service = new AuditAdministrationService(db, archival.Object, Options.Create(new SecurityOptions()), authority.Object, proofs.Object);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExportProofAsync(foreign.EventId));
        proofs.VerifyNoOtherCalls();
        Assert.That(await service.ExportProofAsync(row.EventId), Is.EqualTo(new byte[] { 1, 2 }));
        proofs.VerifyAll();
        proofs.VerifyNoOtherCalls();
    }

    [Test]
    public async Task RevokedPermitCannotExecuteOrWriteEvidence()
    {
        permit.Setup(p => p.CheckCurrentAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new UnauthorizedAccessException());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().ArchiveAsync(DateTimeOffset.UtcNow.AddDays(-90)));
        archival.VerifyNoOtherCalls();
        Assert.That(await db.SecurityEvents.CountAsync(), Is.Zero);
        permit.Verify(p => p.DisposeAsync(), Times.Once);
    }
}
