using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Options;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

public sealed class SchemaOverrideTests
{
    private const string CustomSchemaDatabaseName = "MillWorksAuditCoreCustomSchemaTests";
    private const string CustomSchema = "audit_custom";

    private static readonly string[] ExpectedAuditTables =
    [
        "ArchiveRecord",
        "AuditEvents",
        "AuditIntegrity",
        "AuditIntegrityWorkItems",
        "AuditLogs",
        "SecurityEvents"
    ];

    private string _customSchemaConnectionString = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        if (!SqlServerContainerFixture.DockerAvailable)
        {
            Assert.Inconclusive(
                $"SQL Server integration tests require Docker for Testcontainers. " +
                $"Reason: {SqlServerContainerFixture.DockerSkipReason ?? "unknown"}");
        }

        var builder = new SqlConnectionStringBuilder(SqlServerContainerFixture.ConnectionString)
        {
            InitialCatalog = CustomSchemaDatabaseName
        };
        _customSchemaConnectionString = builder.ConnectionString;

        await DropAndCreateDatabaseAsync();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (!SqlServerContainerFixture.DockerAvailable)
        {
            return;
        }

        await DropDatabaseIfExistsAsync();
    }

    [Test]
    public async Task EnsureCreated_WithCustomSchema_CreatesTablesUnderCustomSchemaAndPreservesChainSemantics()
    {
        // EnsureCreatedAsync, not MigrateAsync: Phase 3's accepted contract says custom-schema
        // support is fresh-database-only and the existing migrations remain anchored to "audit".
        await using (var ctx = CreateCustomSchemaContext())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        const string hash1 = "AAAA////Hash1Hash1Hash1Hash1Hash1Hash1Hash1=";
        const string hash2 = "BBBB////Hash2Hash2Hash2Hash2Hash2Hash2Hash2=";
        const string hash3 = "CCCC////Hash3Hash3Hash3Hash3Hash3Hash3Hash3=";

        Guid event1Id, event2Id, event3Id;
        await using (var ctx = CreateCustomSchemaContext())
        {
            var event1 = new AuditEventEntity { EventType = "Test", JsonData = "{\"n\":1}" };
            var event2 = new AuditEventEntity { EventType = "Test", JsonData = "{\"n\":2}" };
            var event3 = new AuditEventEntity { EventType = "Test", JsonData = "{\"n\":3}" };
            ctx.Set<AuditEventEntity>().AddRange(event1, event2, event3);
            await ctx.SaveChangesAsync();

            event1Id = event1.EventId;
            event2Id = event2.EventId;
            event3Id = event3.EventId;

            ctx.Set<AuditIntegrityEntity>().AddRange(
                new AuditIntegrityEntity
                {
                    EventId = event1Id,
                    EventHash = hash1,
                    PreviousEventHash = null,
                    Checksum = hash1,
                    SequenceNumber = 1,
                    TrustedTimestamp = DateTimeOffset.UtcNow
                },
                new AuditIntegrityEntity
                {
                    EventId = event2Id,
                    EventHash = hash2,
                    PreviousEventHash = hash1,
                    Checksum = hash2,
                    SequenceNumber = 2,
                    TrustedTimestamp = DateTimeOffset.UtcNow
                },
                new AuditIntegrityEntity
                {
                    EventId = event3Id,
                    EventHash = hash3,
                    PreviousEventHash = hash2,
                    Checksum = hash3,
                    SequenceNumber = 3,
                    TrustedTimestamp = DateTimeOffset.UtcNow
                });
            await ctx.SaveChangesAsync();
        }

        var tablesUnderCustomSchema = await QueryAsync($@"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{CustomSchema}' AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME;");
        var tablesUnderDefaultAudit = await QueryAsync(@"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = 'audit' AND TABLE_TYPE = 'BASE TABLE';");

        List<AuditEventEntity> readEvents;
        Dictionary<Guid, AuditIntegrityEntity> readIntegrity;
        await using (var ctx = CreateCustomSchemaContext())
        {
            readEvents = await ctx.Set<AuditEventEntity>().AsNoTracking().ToListAsync();
            // Key by EventId, not SequenceNumber order: SQL Server's batch-insert with
            // OUTPUT clause does not guarantee identity values match input row order.
            readIntegrity = await ctx.Set<AuditIntegrityEntity>()
                .AsNoTracking()
                .ToDictionaryAsync(static i => i.EventId);
        }

        Assert.Multiple(() =>
        {
            Assert.That(tablesUnderCustomSchema, Is.SupersetOf(ExpectedAuditTables),
                $"Expected all six audit tables under '{CustomSchema}' after EnsureCreatedAsync().");
            Assert.That(tablesUnderDefaultAudit, Is.Empty,
                "Expected no audit tables under default 'audit' schema when context is configured for custom schema.");

            Assert.That(readEvents, Has.Count.EqualTo(3),
                "Expected three AuditEvent rows written under custom schema to round-trip.");
            Assert.That(
                readEvents.Select(static e => e.EventId),
                Is.EquivalentTo(new[] { event1Id, event2Id, event3Id }),
                "Round-tripped EventIds should match the inserted set.");

            Assert.That(readIntegrity, Has.Count.EqualTo(3),
                "Expected three AuditIntegrity rows written under custom schema to round-trip.");
            Assert.That(readIntegrity[event1Id].EventHash, Is.EqualTo(hash1));
            Assert.That(readIntegrity[event1Id].PreviousEventHash, Is.Null,
                "Chain row for event1 should have a null PreviousEventHash.");
            Assert.That(readIntegrity[event2Id].EventHash, Is.EqualTo(hash2));
            Assert.That(readIntegrity[event2Id].PreviousEventHash, Is.EqualTo(readIntegrity[event1Id].EventHash),
                "Chain row for event2 should link back to event1's EventHash.");
            Assert.That(readIntegrity[event3Id].EventHash, Is.EqualTo(hash3));
            Assert.That(readIntegrity[event3Id].PreviousEventHash, Is.EqualTo(readIntegrity[event2Id].EventHash),
                "Chain row for event3 should link back to event2's EventHash.");
        });
    }

    private AuditDbContext CreateCustomSchemaContext()
    {
        // Mirrors MillWorksAuditBuilder.cs:173 — without this, EF's default model cache
        // keys on context type alone and a previously compiled "audit"-schema model is
        // returned from cache, so HasDefaultSchema(_schema) is silently ignored.
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer(_customSchemaConnectionString)
            .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()
            .Options;
        var efOptions = Options.Create(new EntityFrameworkOptions { Schema = CustomSchema });
        return new AuditDbContext(options, encryptionService: null, efOptions: efOptions);
    }

    private async Task DropAndCreateDatabaseAsync()
    {
        await DropDatabaseIfExistsAsync();
        await using var master = CreateMasterConnection();
        await master.OpenAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{CustomSchemaDatabaseName}];";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DropDatabaseIfExistsAsync()
    {
        await using var master = CreateMasterConnection();
        await master.OpenAsync();
        await using var cmd = master.CreateCommand();
        // SINGLE_USER WITH ROLLBACK IMMEDIATE evicts any pooled EF connection so
        // DROP DATABASE doesn't fail with "database in use".
        cmd.CommandText = $"""
            IF DB_ID('{CustomSchemaDatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{CustomSchemaDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{CustomSchemaDatabaseName}];
            END
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static SqlConnection CreateMasterConnection()
    {
        var builder = new SqlConnectionStringBuilder(SqlServerContainerFixture.ConnectionString)
        {
            InitialCatalog = "master"
        };
        return new SqlConnection(builder.ConnectionString);
    }

    private async Task<List<string>> QueryAsync(string sql)
    {
        var results = new List<string>();
        await using var conn = new SqlConnection(_customSchemaConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }
}
