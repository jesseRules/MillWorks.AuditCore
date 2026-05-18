using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Options;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Verifies that <see cref="AuditDbContext"/> applies the configured
/// <see cref="EntityFrameworkOptions.Schema"/> to every audit entity's table mapping.
/// Metadata-only — no provider required.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class ConfiguredSchemaTests
{
    private static readonly Type[] AuditEntityTypes =
    [
        typeof(AuditEventEntity),
        typeof(AuditIntegrityEntity),
        typeof(AuditLogEntity),
        typeof(AuditArchiveRecordEntity),
        typeof(AuditSecurityEventEntity),
        typeof(AuditIntegrityWorkItemEntity)
    ];

    [Test]
    public void Model_DefaultSchema_AllAuditEntitiesUseAuditSchema()
    {
        using var context = CreateContext(schema: null);

        foreach (var clrType in AuditEntityTypes)
        {
            var entityType = context.Model.FindEntityType(clrType);
            Assert.That(entityType, Is.Not.Null, $"Entity type {clrType.Name} not found in model");
            Assert.That(entityType!.GetSchema(), Is.EqualTo("audit"),
                $"{clrType.Name} expected to map to 'audit' schema by default");
        }
    }

    [Test]
    public void Model_CustomSchema_AllAuditEntitiesUseCustomSchema()
    {
        const string customSchema = "audit_custom";

        using var context = CreateContext(schema: customSchema);

        foreach (var clrType in AuditEntityTypes)
        {
            var entityType = context.Model.FindEntityType(clrType);
            Assert.That(entityType, Is.Not.Null, $"Entity type {clrType.Name} not found in model");
            Assert.That(entityType!.GetSchema(), Is.EqualTo(customSchema),
                $"{clrType.Name} expected to map to '{customSchema}' when configured");
        }
    }

    [Test]
    public void Model_ArchiveRecord_UsesFluentlyConfiguredTableName()
    {
        using var context = CreateContext(schema: null);

        var entityType = context.Model.FindEntityType(typeof(AuditArchiveRecordEntity));
        Assert.That(entityType, Is.Not.Null);
        Assert.That(entityType!.GetTableName(), Is.EqualTo("ArchiveRecord"),
            "ArchiveRecord table name is configured fluently in ConfigureAudit (singular)");
        Assert.That(entityType.GetSchema(), Is.EqualTo("audit"));
    }

    [Test]
    public void Model_ArchiveRecord_CustomSchema_UsesConfiguredSchema()
    {
        const string customSchema = "audit_custom";

        using var context = CreateContext(schema: customSchema);

        var entityType = context.Model.FindEntityType(typeof(AuditArchiveRecordEntity));
        Assert.That(entityType, Is.Not.Null);
        Assert.That(entityType!.GetTableName(), Is.EqualTo("ArchiveRecord"));
        Assert.That(entityType.GetSchema(), Is.EqualTo(customSchema));
    }

    private static AuditDbContext CreateContext(string? schema)
    {
        // Wire the schema-aware cache key factory so tests with different schemas get
        // independently compiled models — mirrors what UseEntityFramework does in production.
        var dbOptions = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()
            .Options;

        if (schema is null)
        {
            return new AuditDbContext(dbOptions);
        }

        var efOptions = Options.Create(new EntityFrameworkOptions { Schema = schema });
        return new AuditDbContext(dbOptions, encryptionService: null, efOptions: efOptions);
    }
}
