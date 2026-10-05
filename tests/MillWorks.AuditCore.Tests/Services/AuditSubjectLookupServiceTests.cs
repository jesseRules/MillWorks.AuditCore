using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services;

namespace MillWorks.AuditCore.Tests.Services;

[TestFixture]
[Category("Unit")]
public sealed class AuditSubjectLookupServiceTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
    private SqliteConnection _connection = null!;
    private AuditDbContext _context = null!;
    private AuditSubjectLookupService _service = null!;

    [SetUp]
    public async Task SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _context = new AuditDbContext(
            new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync();
        _service = new AuditSubjectLookupService(_context);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Test]
    public async Task FindAsync_ReturnsTenantScopedRecordsAcrossAllActiveEvidenceStores()
    {
        Guid tenantId = Guid.NewGuid();
        Guid subjectId = Guid.NewGuid();
        AuditEventEntity auditEvent = CreateAuditEvent(tenantId, subjectId, BaseTime.AddMinutes(1));
        AuditLogEntity auditLog = CreateAuditLog(BaseTime.AddMinutes(2));
        auditLog.SubjectLinks.Add(CreateLink(auditLog, tenantId, subjectId, "TargetUserId"));
        auditLog.SubjectLinks.Add(CreateLink(auditLog, tenantId, subjectId, "InitiatedByUserId"));
        AuditSecurityEventEntity securityEvent = CreateSecurityEvent(
            tenantId,
            subjectId,
            BaseTime.AddMinutes(3));
        AuditEventEntity otherTenant = CreateAuditEvent(Guid.NewGuid(), subjectId, BaseTime.AddMinutes(4));
        _context.AddRange(auditEvent, auditLog, securityEvent, otherTenant);
        await _context.SaveChangesAsync();

        var page = await _service.FindAsync(tenantId, subjectId, limit: 10);

        Assert.That(page.NextCursor, Is.Null);
        Assert.That(page.Items, Is.EqualTo(new[]
        {
            new AuditSubjectRecordReference(AuditSubjectRecordStore.AuditEvent, auditEvent.EventId, BaseTime.AddMinutes(1)),
            new AuditSubjectRecordReference(AuditSubjectRecordStore.AuditLog, auditLog.Id, BaseTime.AddMinutes(2)),
            new AuditSubjectRecordReference(AuditSubjectRecordStore.SecurityEvent, securityEvent.Id, BaseTime.AddMinutes(3))
        }));
    }

    [Test]
    public async Task FindAsync_UsesStableKeysetCursorWithoutDuplicates()
    {
        Guid tenantId = Guid.NewGuid();
        Guid subjectId = Guid.NewGuid();
        AuditEventEntity auditEvent = CreateAuditEvent(tenantId, subjectId, BaseTime.AddMinutes(1));
        AuditLogEntity auditLog = CreateAuditLog(BaseTime.AddMinutes(2));
        auditLog.SubjectLinks.Add(CreateLink(auditLog, tenantId, subjectId, "UserId"));
        AuditSecurityEventEntity securityEvent = CreateSecurityEvent(
            tenantId,
            subjectId,
            BaseTime.AddMinutes(3));
        _context.AddRange(auditEvent, auditLog, securityEvent);
        await _context.SaveChangesAsync();

        var first = await _service.FindAsync(tenantId, subjectId, limit: 2);
        var second = await _service.FindAsync(tenantId, subjectId, first.NextCursor, limit: 2);

        Assert.Multiple(() =>
        {
            Assert.That(first.Items, Has.Count.EqualTo(2));
            Assert.That(first.NextCursor, Is.Not.Null);
            Assert.That(second.Items, Has.Count.EqualTo(1));
            Assert.That(second.NextCursor, Is.Null);
            Assert.That(
                first.Items.Concat(second.Items).Select(static item => item.RecordId),
                Is.EquivalentTo(new[] { auditEvent.EventId, auditLog.Id, securityEvent.Id }));
        });
    }

    [Test]
    public void FindAsync_RejectsEmptyTenantOrSubject()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await _service.FindAsync(Guid.Empty, Guid.NewGuid()),
                Throws.ArgumentException);
            Assert.That(
                async () => await _service.FindAsync(Guid.NewGuid(), Guid.Empty),
                Throws.ArgumentException);
        });
    }

    private static AuditEventEntity CreateAuditEvent(Guid tenantId, Guid subjectId, DateTimeOffset occurredAt) => new()
    {
        EventId = Guid.NewGuid(),
        TenantId = tenantId,
        UserId = subjectId,
        EventType = "SubjectLookup",
        InsertedDate = occurredAt,
        CreatedAt = occurredAt,
        CreatedById = subjectId,
        IntegrityStatus = IntegrityStatus.Completed
    };

    private static AuditLogEntity CreateAuditLog(DateTimeOffset occurredAt) => new()
    {
        EntityName = "SubjectLinkedEntity",
        EntityId = Guid.NewGuid(),
        Action = AuditAction.Updated,
        CreatedAt = occurredAt,
        CreatedById = Guid.NewGuid()
    };

    private static AuditLogSubjectLinkEntity CreateLink(
        AuditLogEntity auditLog,
        Guid tenantId,
        Guid subjectId,
        string relationship) => new()
    {
        AuditLogId = auditLog.Id,
        AuditLog = auditLog,
        TenantId = tenantId,
        SubjectId = subjectId,
        Relationship = relationship,
        OccurredAt = auditLog.CreatedAt
    };

    private static AuditSecurityEventEntity CreateSecurityEvent(
        Guid tenantId,
        Guid subjectId,
        DateTimeOffset occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        SubjectUserId = subjectId,
        EventType = SecurityEventType.SuspiciousActivity,
        Severity = SecurityEventSeverity.Medium,
        Message = "Subject lookup",
        DetectedAt = occurredAt,
        CreatedAt = occurredAt,
        CreatedById = Guid.NewGuid()
    };
}
