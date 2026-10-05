using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

[TestFixture]
[Category("SqlServer")]
public sealed class AuditSubjectLookupSqlServerTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [SetUp]
    public async Task SetUpAsync()
    {
        if (!SqlServerContainerFixture.DockerAvailable)
        {
            Assert.Inconclusive(
                $"SQL Server integration tests require Docker for Testcontainers. " +
                $"Reason: {SqlServerContainerFixture.DockerSkipReason ?? "unknown"}");
        }

        await SqlServerContainerFixture.ResetAsync();
    }

    [Test]
    public async Task FindAsync_UsesTenantScopedIndexedQueriesAndStableKeysetAcrossAllStores()
    {
        Guid tenantId = Guid.NewGuid();
        Guid subjectId = Guid.NewGuid();
        await using AuditDbContext context = SqlServerContainerFixture.CreateContext();

        AuditEventEntity auditEvent = CreateAuditEvent(tenantId, subjectId, BaseTime.AddMinutes(1));
        AuditLogEntity auditLog = CreateAuditLog(BaseTime.AddMinutes(2));
        auditLog.SubjectLinks.Add(CreateLink(auditLog, tenantId, subjectId, "TargetUserId"));
        auditLog.SubjectLinks.Add(CreateLink(auditLog, tenantId, subjectId, "InitiatedByUserId"));
        AuditSecurityEventEntity securityEvent = CreateSecurityEvent(tenantId, subjectId, BaseTime.AddMinutes(3));
        AuditEventEntity otherTenant = CreateAuditEvent(Guid.NewGuid(), subjectId, BaseTime.AddMinutes(4));
        context.AddRange(auditEvent, auditLog, securityEvent, otherTenant);
        await context.SaveChangesAsync();

        var service = new AuditSubjectLookupService(context);
        AuditSubjectLookupPage first = await service.FindAsync(tenantId, subjectId, limit: 2);
        AuditSubjectLookupPage second = await service.FindAsync(
            tenantId,
            subjectId,
            first.NextCursor,
            limit: 2);

        Assert.Multiple(() =>
        {
            Assert.That(first.NextCursor, Is.Not.Null);
            Assert.That(second.NextCursor, Is.Null);
            Assert.That(first.Items.Concat(second.Items), Is.EqualTo(new[]
            {
                new AuditSubjectRecordReference(
                    AuditSubjectRecordStore.AuditEvent,
                    auditEvent.EventId,
                    BaseTime.AddMinutes(1)),
                new AuditSubjectRecordReference(
                    AuditSubjectRecordStore.AuditLog,
                    auditLog.Id,
                    BaseTime.AddMinutes(2)),
                new AuditSubjectRecordReference(
                    AuditSubjectRecordStore.SecurityEvent,
                    securityEvent.Id,
                    BaseTime.AddMinutes(3))
            }));
        });
    }

    private static AuditEventEntity CreateAuditEvent(
        Guid tenantId,
        Guid subjectId,
        DateTimeOffset occurredAt) => new()
    {
        EventId = Guid.NewGuid(),
        TenantId = tenantId,
        UserId = subjectId,
        EventType = "SubjectLookup.SqlServer",
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
        Message = "Subject lookup SQL Server proof",
        DetectedAt = occurredAt,
        CreatedAt = occurredAt,
        CreatedById = Guid.NewGuid()
    };
}
