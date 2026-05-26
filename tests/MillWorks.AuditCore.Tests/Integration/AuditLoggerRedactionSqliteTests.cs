using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Phase 2 acceptance: verifies that AuditLogger routes AuditEvent.ErrorMessage through
/// the IAuditFieldRedactor before JsonData serialization, so sensitive content embedded
/// in exception messages (connection strings, tokens, IPs, passwords) is scrubbed before
/// it reaches the audit store. Uses real SQLite because the JSON column behavior differs
/// from InMemoryDatabase.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class AuditLoggerRedactionSqliteTests : SqliteIntegrationFixture
{
    [Test]
    public async Task LogAsync_ErrorMessageWithSqlConnectionString_SanitizedBeforePersistence()
    {
        const string rawErrorMessage =
            "Login failed. Server=10.0.0.5;Database=prod;User Id=sa;Password=Password123!";

        using var context = CreateContext();
        var repository = new AuditEventRepository(context);
        var redactor = new DefaultAuditFieldRedactor();

        // Explicitly disable tamper detection so the simple no-tamper branch runs regardless
        // of any future default change in SecurityOptions. Fully qualified because the
        // SqliteIntegrationFixture base class exposes an `Options` property.
        var securityOptions = Microsoft.Extensions.Options.Options.Create(new SecurityOptions
        {
            EnableTamperDetection = false,
            EnableBatchedIntegrityWrites = false
        });

        var auditLogger = new AuditLogger(
            NullLogger<AuditLogger>.Instance,
            Mock.Of<IAuditEventFactory>(),
            repository,
            context,
            Mock.Of<IAuditContext>(),
            redactor,
            tamperDetectionService: null,
            integrityWriteBatcher: null,
            securityOptions: securityOptions);

        await auditLogger.LogAsync(new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Login",
            StartDate = DateTimeOffset.UtcNow,
            ErrorMessage = rawErrorMessage
        });

        using var verifyContext = CreateContext();
        var persisted = await verifyContext.Set<AuditEventEntity>().SingleAsync();

        Assert.That(persisted.JsonData, Is.Not.Null);
        Assert.That(persisted.JsonData, Does.Not.Contain("10.0.0.5"));
        Assert.That(persisted.JsonData, Does.Not.Contain("Password123!"));
        Assert.That(persisted.JsonData, Does.Not.Contain("Server="));
        Assert.That(persisted.JsonData, Does.Not.Contain("Password="));
        Assert.That(persisted.JsonData, Does.Contain("[SANITIZED]"));
    }

    [Test]
    public async Task LogAsync_ErrorMessageWithStudentIdInUniqueKeyViolation_SanitizedBeforePersistence()
    {
        const string rawErrorMessage =
            "Cannot insert duplicate key row in object 'dbo.StudentRecords'. The duplicate key value is ('STUDENT-12345').";

        using var context = CreateContext();
        var repository = new AuditEventRepository(context);
        var redactor = new DefaultAuditFieldRedactor();

        var securityOptions = Microsoft.Extensions.Options.Options.Create(new SecurityOptions
        {
            EnableTamperDetection = false,
            EnableBatchedIntegrityWrites = false
        });

        var auditLogger = new AuditLogger(
            NullLogger<AuditLogger>.Instance,
            Mock.Of<IAuditEventFactory>(),
            repository,
            context,
            Mock.Of<IAuditContext>(),
            redactor,
            tamperDetectionService: null,
            integrityWriteBatcher: null,
            securityOptions: securityOptions);

        await auditLogger.LogAsync(new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.FerpaValidation",
            StartDate = DateTimeOffset.UtcNow,
            ErrorMessage = rawErrorMessage
        });

        using var verifyContext = CreateContext();
        var persisted = await verifyContext.Set<AuditEventEntity>().SingleAsync();

        Assert.That(persisted.JsonData, Is.Not.Null);
        Assert.That(persisted.JsonData, Does.Not.Contain("STUDENT-12345"));
        Assert.That(persisted.JsonData, Does.Contain("[SANITIZED]"));
    }
}
