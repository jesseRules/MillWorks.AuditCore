using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for AuditComplianceService verifying export, anonymization,
/// compliance reporting, retention validation, and retention policy application
/// against a real SQLite backend.
/// </summary>
[TestFixture]
[Category("Integration")]
public class ComplianceIntegrationTests : SqliteIntegrationFixture
{
    private static IOptions<ComplianceOptions> CreateAllStandardsOptions() =>
        Microsoft.Extensions.Options.Options.Create(new ComplianceOptions
        {
            Standards = Enum.GetValues<ComplianceStandard>().ToList()
        });

    private static AuditEventEntity CreateAuditEvent(
        Guid? userId = null,
        string eventType = "User.Login",
        string user = "alice@test.com",
        DateTimeOffset? insertedDate = null,
        string? jsonData = null)
    {
        return new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            User = user,
            UserFullName = "Alice Test",
            UserId = userId ?? Guid.NewGuid(),
            JsonData = jsonData ?? """{"action":"login","status":"success"}""",
            InsertedDate = insertedDate ?? DateTimeOffset.UtcNow,
            EntityType = "User",
            EntityId = Guid.NewGuid().ToString(),
            IpAddress = "192.168.1.100",
            UserAgent = "TestAgent/1.0"
        };
    }

    [Test]
    public async Task ExportUserAuditData_ReturnsAllUserEvents()
    {
        // Arrange
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new AuditComplianceService(
            eventRepo,
            Enumerable.Empty<IComplianceValidator>(),
            NullLogger<AuditComplianceService>.Instance,
            config,
            CreateAllStandardsOptions());

        var userId = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            await context.AuditEvents.AddAsync(CreateAuditEvent(userId: userId, eventType: $"User.Action{i}"));
        }
        // Add events for a different user to ensure filtering works
        await context.AuditEvents.AddAsync(CreateAuditEvent(userId: Guid.NewGuid(), user: "bob@test.com"));
        await context.SaveChangesAsync();

        // Act
        var result = await service.ExportUserAuditDataAsync(userId);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Empty);
        Assert.That(result.RecordCount, Is.EqualTo(5));
        Assert.That(result.FileName, Does.Contain(userId.ToString()));

        // Verify the exported data contains valid JSON
        var jsonString = Encoding.UTF8.GetString(result.Data);
        Assert.That(jsonString, Does.Contain("User.Action"));
    }

    [Test]
    public async Task AnonymizeUserData_RemovesPII()
    {
        // Arrange - use one context for seeding, a fresh one for the service
        var userId = Guid.NewGuid();
        using (var seedContext = CreateContext())
        {
            for (int i = 0; i < 3; i++)
            {
                await seedContext.AuditEvents.AddAsync(CreateAuditEvent(
                    userId: userId,
                    user: "alice@test.com",
                    jsonData: $$$"""{"UserName":"Alice","Email":"alice@test.com","action":"test{{{i}}}"}"""));
            }
            await seedContext.SaveChangesAsync();
        }

        // Create service with a fresh context to avoid tracked-entity conflicts
        using var serviceContext = CreateContext();
        var eventRepo = new AuditEventRepository(serviceContext);
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var service = new AuditComplianceService(
            eventRepo,
            Enumerable.Empty<IComplianceValidator>(),
            NullLogger<AuditComplianceService>.Instance,
            config,
            CreateAllStandardsOptions());

        // Act
        var result = await service.AnonymizeUserDataAsync(userId);

        // Assert
        Assert.That(result.Success, Is.True, $"Anonymization failed: {result.Message}");
        Assert.That(result.EventCount, Is.EqualTo(3));

        // Verify anonymization in the database using a fresh context
        using var verifyContext = CreateContext();
        var anonymizedEvents = await verifyContext.AuditEvents
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .ToListAsync();

        Assert.That(anonymizedEvents, Has.Count.EqualTo(3));
        foreach (var evt in anonymizedEvents)
        {
            Assert.That(evt.User, Is.EqualTo("ANONYMIZED"));
            Assert.That(evt.UserFullName, Is.EqualTo("ANONYMIZED"));
            Assert.That(evt.UserAgent, Is.EqualTo("ANONYMIZED"));
        }
    }

    [Test]
    public async Task GenerateComplianceReport_WithEvents_ReturnsReport()
    {
        // Arrange
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        // Create a mock compliance validator
        var mockValidator = new Mock<IComplianceValidator>();
        mockValidator.Setup(static v => v.Standard).Returns(ComplianceStandard.SOC2);
        mockValidator
            .Setup(static v => v.ValidateAsync(It.IsAny<ComplianceValidationContext>()))
            .ReturnsAsync(new List<AuditValidationResult>
            {
                new()
                {
                    RuleName = "Access Logging",
                    Passed = true,
                    Message = "All access events are logged"
                }
            });
        mockValidator
            .Setup(static v => v.GenerateRecommendations(It.IsAny<IEnumerable<AuditValidationResult>>()))
            .Returns(new List<string> { "Continue monitoring" });

        var service = new AuditComplianceService(
            eventRepo,
            new[] { mockValidator.Object },
            NullLogger<AuditComplianceService>.Instance,
            config,
            CreateAllStandardsOptions());

        // Seed events
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            await context.AuditEvents.AddAsync(CreateAuditEvent(
                insertedDate: DateTimeOffset.UtcNow.AddDays(-i % 7)));
        }
        await context.SaveChangesAsync();

        // Act
        var report = await service.GenerateComplianceReportAsync(
            ComplianceStandard.SOC2, startDate, endDate);

        // Assert
        Assert.That(report, Is.Not.Null);
        Assert.That(report.Standard, Is.EqualTo(ComplianceStandard.SOC2));
        Assert.That(report.ValidationResults, Is.Not.Empty);
        Assert.That(report.IsCompliant, Is.True);
        Assert.That(report.Recommendations, Is.Not.Empty);
        Assert.That(report.Statistics, Is.Not.Null);
    }

    [Test]
    public async Task ValidateRetentionCompliance_WithOldEvents_ReturnsFalse()
    {
        // Arrange
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);

        // Configure retention policy: 30 days for User.Login events
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Audit:RetentionPolicies:0:EventType"] = "User.Login",
                ["Audit:RetentionPolicies:0:RetentionDays"] = "30",
                ["Audit:RetentionPolicies:0:ArchiveBeforeDelete"] = "false",
                ["Audit:RetentionPolicies:0:IsActive"] = "true"
            }!)
            .Build();

        var service = new AuditComplianceService(
            eventRepo,
            Enumerable.Empty<IComplianceValidator>(),
            NullLogger<AuditComplianceService>.Instance,
            config,
            CreateAllStandardsOptions());

        // Seed events older than 30 days
        for (int i = 0; i < 3; i++)
        {
            await context.AuditEvents.AddAsync(CreateAuditEvent(
                eventType: "User.Login",
                insertedDate: DateTimeOffset.UtcNow.AddDays(-60)));
        }
        await context.SaveChangesAsync();

        // Act
        var isCompliant = await service.ValidateRetentionComplianceAsync();

        // Assert
        Assert.That(isCompliant, Is.False);
    }

    [Test]
    public async Task ApplyRetentionPolicy_DeletesExpiredEvents()
    {
        // Arrange
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);

        // Configure retention policy: 30 days for User.Login
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Audit:RetentionPolicies:0:EventType"] = "User.Login",
                ["Audit:RetentionPolicies:0:RetentionDays"] = "30",
                ["Audit:RetentionPolicies:0:ArchiveBeforeDelete"] = "false",
                ["Audit:RetentionPolicies:0:IsActive"] = "true",
                ["Audit:RetentionPolicies:0:Priority"] = "10"
            }!)
            .Build();

        var service = new AuditComplianceService(
            eventRepo,
            Enumerable.Empty<IComplianceValidator>(),
            NullLogger<AuditComplianceService>.Instance,
            config,
            CreateAllStandardsOptions());

        // Seed old events (older than 30 days)
        for (int i = 0; i < 5; i++)
        {
            await context.AuditEvents.AddAsync(CreateAuditEvent(
                eventType: "User.Login",
                insertedDate: DateTimeOffset.UtcNow.AddDays(-60)));
        }
        // Seed recent events (within 30 days)
        for (int i = 0; i < 3; i++)
        {
            await context.AuditEvents.AddAsync(CreateAuditEvent(
                eventType: "User.Login",
                insertedDate: DateTimeOffset.UtcNow.AddDays(-5)));
        }
        await context.SaveChangesAsync();

        // Verify initial count
        var initialCount = await context.AuditEvents.CountAsync();
        Assert.That(initialCount, Is.EqualTo(8));

        // Act
        await service.ApplyRetentionPolicyAsync();

        // Assert - old events should be deleted, recent ones kept
        using var verifyContext = CreateContext();
        var remainingCount = await verifyContext.AuditEvents.AsNoTracking().CountAsync();
        Assert.That(remainingCount, Is.EqualTo(3));

        // All remaining events should be recent
        var remainingEvents = await verifyContext.AuditEvents.AsNoTracking().ToListAsync();
        foreach (var evt in remainingEvents)
        {
            Assert.That(evt.InsertedDate, Is.GreaterThan(DateTimeOffset.UtcNow.AddDays(-31)));
        }
    }
}
