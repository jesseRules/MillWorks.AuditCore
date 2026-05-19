using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Tests.EntityFramework;

[TestFixture]
[Category("Unit")]
public class AuditDbContextConfigurationTests : IDisposable
{
    private SqliteConnection _connection;
    private DbContextOptions<AuditDbContext> _options;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;
    }

    [TearDown]
    public void TearDown()
    {
        _connection.Close();
        _connection.Dispose();
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    [Test]
    public void ConfigureAudit_AllDbSetsExist()
    {
        using var context = new AuditDbContext(_options);

        Assert.That(context.AuditEvents, Is.Not.Null);
        Assert.That(context.AuditIntegrity, Is.Not.Null);
        Assert.That(context.AuditLogs, Is.Not.Null);
        Assert.That(context.ArchiveRecords, Is.Not.Null);
        Assert.That(context.SecurityEvents, Is.Not.Null);
    }

    [Test]
    public void ConfigureAudit_AuditEvents_HasCorrectTableName()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditEventEntity));

        Assert.That(entityType, Is.Not.Null);
        Assert.That(entityType!.GetTableName(), Is.EqualTo("AuditEvents"));
    }

    [Test]
    public void ConfigureAudit_AuditIntegrity_HasCorrectTableName()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditIntegrityEntity));

        Assert.That(entityType, Is.Not.Null);
        Assert.That(entityType!.GetTableName(), Is.EqualTo("AuditIntegrity"));
    }

    [Test]
    public void ConfigureAudit_AuditLogs_HasCorrectTableName()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditLogEntity));

        Assert.That(entityType, Is.Not.Null);
        Assert.That(entityType!.GetTableName(), Is.EqualTo("AuditLogs"));
    }

    [Test]
    public void ConfigureAudit_SecurityEvents_HasCorrectTableName()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditSecurityEventEntity));

        Assert.That(entityType, Is.Not.Null);
        Assert.That(entityType!.GetTableName(), Is.EqualTo("SecurityEvents"));
    }

    [Test]
    public void ConfigureAudit_AuditEvents_HasExpectedIndexes()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditEventEntity))!;
        var indexes = entityType.GetIndexes().ToList();

        var indexNames = indexes
            .Select(static i => i.Name)
            .Where(static n => n != null)
            .ToList();

        Assert.That(indexNames, Does.Contain("IX_AuditEvents_UserId"));
        Assert.That(indexNames, Does.Contain("IX_AuditEvents_EventType"));
        Assert.That(indexNames, Does.Contain("IX_AuditEvents_CorrelationId"));
        Assert.That(indexNames, Does.Contain("IX_AuditEvents_Entity"));
    }

    [Test]
    public void ConfigureAudit_AuditIntegrity_HasHashChainIndex()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditIntegrityEntity))!;
        var indexes = entityType.GetIndexes().ToList();

        var indexNames = indexes.Select(static i => i.Name).ToList();

        Assert.That(indexNames, Does.Contain("IX_AuditIntegrity_EventId"));
        Assert.That(indexNames, Does.Contain("IX_AuditIntegrity_HashChain"));
    }

    [Test]
    public void ConfigureAudit_AuditIntegrity_EventIdIsUnique()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditIntegrityEntity))!;
        var eventIdIndex = entityType.GetIndexes()
            .FirstOrDefault(static i => i.Name == "IX_AuditIntegrity_EventId");

        Assert.That(eventIdIndex, Is.Not.Null);
        Assert.That(eventIdIndex!.IsUnique, Is.True);
    }

    [Test]
    public void ConfigureAudit_AuditLogs_HasEntityIndex()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditLogEntity))!;
        var indexes = entityType.GetIndexes().ToList();

        var indexNames = indexes.Select(static i => i.Name).ToList();

        Assert.That(indexNames, Does.Contain("IX_AuditLogs_Entity"));
        Assert.That(indexNames, Does.Contain("IX_AuditLogs_CreatedAt"));
        Assert.That(indexNames, Does.Contain("IX_AuditLogs_CorrelationId"));
    }

    [Test]
    public void ConfigureAudit_AuditEventEntity_RequiredProperties()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditEventEntity))!;

        var eventIdProp = entityType.FindProperty(nameof(AuditEventEntity.EventId));
        Assert.That(eventIdProp, Is.Not.Null);
        Assert.That(eventIdProp!.IsNullable, Is.False);
    }

    [Test]
    public void ConfigureAudit_AuditIntegrityEntity_RequiredProperties()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditIntegrityEntity))!;

        var eventIdProp = entityType.FindProperty(nameof(AuditIntegrityEntity.EventId));
        var eventHashProp = entityType.FindProperty(nameof(AuditIntegrityEntity.EventHash));
        var checksumProp = entityType.FindProperty(nameof(AuditIntegrityEntity.Checksum));

        Assert.That(eventIdProp!.IsNullable, Is.False);
        Assert.That(eventHashProp!.IsNullable, Is.False);
        Assert.That(checksumProp!.IsNullable, Is.False);
    }

    [Test]
    public void ConfigureAudit_MaxLengths_AreApplied()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditEventEntity))!;

        var eventTypeProp = entityType.FindProperty(nameof(AuditEventEntity.EventType));
        var actionProp = entityType.FindProperty(nameof(AuditEventEntity.Action));

        Assert.That(eventTypeProp!.GetMaxLength(), Is.EqualTo(256));
        Assert.That(actionProp!.GetMaxLength(), Is.EqualTo(50));
    }

    [Test]
    public void ConfigureAudit_AuditLogEntity_MaxLengths()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditLogEntity))!;

        var entityNameProp = entityType.FindProperty(nameof(AuditLogEntity.EntityName));
        var oldValueProp = entityType.FindProperty(nameof(AuditLogEntity.OldValue));

        Assert.That(entityNameProp!.GetMaxLength(), Is.EqualTo(100));
        Assert.That(oldValueProp!.GetMaxLength(), Is.EqualTo(4000));
    }

    [Test]
    public void ConfigureAudit_AuditIntegrity_HasNavigationToAuditEvent()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditIntegrityEntity))!;
        var navigation = entityType.FindNavigation(nameof(AuditIntegrityEntity.AuditEvent));

        Assert.That(navigation, Is.Not.Null);
    }

    [Test]
    public void ConfigureAudit_SecurityEvent_HasNavigationToAuditEvent()
    {
        using var context = new AuditDbContext(_options);
        var entityType = context.Model.FindEntityType(typeof(AuditSecurityEventEntity))!;
        var navigation = entityType.FindNavigation(nameof(AuditSecurityEventEntity.RelatedAuditEvent));

        Assert.That(navigation, Is.Not.Null);
    }

    [Test]
    public void EnsureCreated_CreatesAllTables()
    {
        using var context = new AuditDbContext(_options);
        context.Database.EnsureCreated();

        // Verify we can query all tables without error
        Assert.DoesNotThrow(() => context.AuditEvents.Count());
        Assert.DoesNotThrow(() => context.AuditIntegrity.Count());
        Assert.DoesNotThrow(() => context.AuditLogs.Count());
        Assert.DoesNotThrow(() => context.ArchiveRecords.Count());
        Assert.DoesNotThrow(() => context.SecurityEvents.Count());
    }
}
