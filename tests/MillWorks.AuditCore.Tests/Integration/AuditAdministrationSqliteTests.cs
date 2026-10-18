using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Administration;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Integration;

[TestFixture]
public sealed class AuditAdministrationSqliteTests : SqliteIntegrationFixture
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task RedactionAndSuccessReceiptCommitTogetherAndProtectedRefusalIsRecorded(bool protectedEvidence)
    {
        using var db = CreateContext();
        var tenant = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        db.AuditEvents.Add(new AuditEventEntity { TenantId = tenant, UserId = subject, User = "private-name", JsonData = "private-content" });
        await db.SaveChangesAsync();
        var authority = new Mock<IAuditAdministrationAuthority>(MockBehavior.Strict);
        var permit = new Mock<IAuditAdministrationPermit>(MockBehavior.Strict);
        permit.SetupGet(p => p.Scope).Returns(new AuditAdministrationScope(tenant, actor, Guid.NewGuid().ToString()));
        permit.Setup(p => p.CheckCurrentAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        permit.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        authority.Setup(a => a.AcquireAsync(It.IsAny<AuditAdministrationTarget>(), It.IsAny<CancellationToken>())).ReturnsAsync(permit.Object);
        var service = new AuditAdministrationService(db, Mock.Of<IAuditArchivalService>(MockBehavior.Strict),
            Microsoft.Extensions.Options.Options.Create(new SecurityOptions { EnableTamperDetection = protectedEvidence }), authority.Object);
        if (protectedEvidence)
            await Assert.ThrowsAsync<AuditAdministrationConflictException>(() => service.AnonymizeAsync(subject));
        else
            Assert.That((await service.AnonymizeAsync(subject)).Success, Is.True);
        db.ChangeTracker.Clear();
        var row = await db.AuditEvents.SingleAsync();
        Assert.That(row.JsonData, Is.EqualTo(protectedEvidence ? "private-content" : null));
        var receipts = await db.SecurityEvents.ToListAsync();
        Assert.That(receipts.Count, Is.EqualTo(2));
        Assert.That(receipts.Any(e => e.DetailsJson!.Contains(protectedEvidence ? "failed" : "succeeded")), Is.True);
        Assert.That(receipts.All(e => e.TenantId == tenant && e.ActorUserId == actor && e.SubjectUserId == subject), Is.True);
    }
}
