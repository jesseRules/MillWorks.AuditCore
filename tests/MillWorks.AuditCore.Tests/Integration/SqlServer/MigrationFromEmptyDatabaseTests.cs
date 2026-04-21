using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

public sealed class MigrationFromEmptyDatabaseTests
{
    private const string MigrationDatabaseName = "MillWorksAuditCoreMigrationsTests";

    private static readonly string[] ExpectedAuditTables =
    [
        "ArchiveRecord",
        "AuditEvents",
        "AuditIntegrity",
        "AuditIntegrityWorkItems",
        "AuditLogs",
        "SecurityEvents"
    ];

    private static readonly string[] ExpectedAuditEventsIndexes =
    [
        "IX_AuditEvents_AspNetUserId",
        "IX_AuditEvents_CorrelationId",
        "IX_AuditEvents_Date_Type",
        "IX_AuditEvents_Entity",
        "IX_AuditEvents_EventType",
        "IX_AuditEvents_IntegrityStatus",
        "IX_AuditEvents_TenantId",
        "IX_AuditEvents_UserId"
    ];

    private static readonly string[] ExpectedMigrationIds =
    [
        "20260420195321_Init"
    ];

    private string _migrationConnectionString = null!;

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
            InitialCatalog = MigrationDatabaseName
        };
        _migrationConnectionString = builder.ConnectionString;

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
    public async Task MigrateAsync_FromEmptyDatabase_CreatesAllAuditTablesIndexesAndMigrationsHistory()
    {
        await using (var ctx = CreateMigrationContext())
        {
            await ctx.Database.MigrateAsync();
        }

        var actualTables = await QueryAsync("""
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = 'audit' AND TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_NAME;
            """);
        var actualIndexes = await QueryAsync("""
            SELECT i.name
            FROM sys.indexes i
            INNER JOIN sys.tables t ON t.object_id = i.object_id
            INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'audit' AND t.name = 'AuditEvents' AND i.name LIKE 'IX_AuditEvents_%'
            ORDER BY i.name;
            """);
        var historyRows = await QueryAsync("""
            SELECT MigrationId
            FROM audit.__EFMigrationsHistory
            ORDER BY MigrationId;
            """);

        Assert.Multiple(() =>
        {
            Assert.That(actualTables, Is.SupersetOf(ExpectedAuditTables),
                "Expected all six audit tables under 'audit' schema after MigrateAsync().");
            Assert.That(actualTables, Does.Contain("__EFMigrationsHistory"),
                "Expected audit.__EFMigrationsHistory after MigrateAsync().");
            Assert.That(actualIndexes, Is.SupersetOf(ExpectedAuditEventsIndexes),
                "Expected all eight IX_AuditEvents_* indexes on audit.AuditEvents after MigrateAsync().");
            Assert.That(historyRows, Is.SupersetOf(ExpectedMigrationIds),
                "Expected both Init and ChangeIntegrityFKsToRestrict rows in audit.__EFMigrationsHistory.");
        });
    }

    private AuditApplicationDbContext CreateMigrationContext()
    {
        var options = new DbContextOptionsBuilder<AuditApplicationDbContext>()
            .UseSqlServer(_migrationConnectionString, sqlOptions =>
            {
                sqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "audit");
            })
            .Options;
        return new AuditApplicationDbContext(options);
    }

    private async Task DropAndCreateDatabaseAsync()
    {
        await DropDatabaseIfExistsAsync();
        await using var master = CreateMasterConnection();
        await master.OpenAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{MigrationDatabaseName}];";
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
            IF DB_ID('{MigrationDatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{MigrationDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{MigrationDatabaseName}];
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
        await using var conn = new SqlConnection(_migrationConnectionString);
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
