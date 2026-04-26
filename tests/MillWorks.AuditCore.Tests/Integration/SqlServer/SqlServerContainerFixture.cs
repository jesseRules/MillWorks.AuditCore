using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Options;
using Respawn;
using Testcontainers.MsSql;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

[SetUpFixture]
public sealed class SqlServerContainerFixture
{
    private static MsSqlContainer? _container;
    private static string? _connectionString;
    private static Respawner? _respawner;

    public static bool DockerAvailable { get; private set; }

    public static string? DockerSkipReason { get; private set; }

    public static string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException(
            "SQL Server container was not started. Check DockerAvailable first.");

    [OneTimeSetUp]
    public async Task StartContainerAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithPassword("AuditCore_Test_Password_123!")
                .Build();

            await _container.StartAsync();

            var raw = _container.GetConnectionString();
            var builder = new SqlConnectionStringBuilder(raw)
            {
                TrustServerCertificate = true,
                InitialCatalog = "MillWorksAuditCoreTests"
            };
            _connectionString = builder.ConnectionString;

            await EnsureDatabaseAndSchemasAsync(
                _connectionString, builder.DataSource, builder.UserID, builder.Password);

            await using (var ctx = CreateContext())
            {
                await ctx.Database.EnsureCreatedAsync();
            }

            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            // audit_custom is pre-created (with no tables) so Respawn's dependency
            // graph spans both schemas before any Phase 3 custom-schema test runs.
            _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
            {
                DbAdapter = DbAdapter.SqlServer,
                SchemasToInclude = ["audit", "audit_custom"],
                WithReseed = true
            });

            DockerAvailable = true;
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            DockerAvailable = false;
            DockerSkipReason = ex.Message;
            await TestContext.Progress.WriteLineAsync(
                $"[SqlServerIntegration] Docker unavailable, SQL Server tests will be marked Inconclusive: {ex.Message}");
        }
    }

    [OneTimeTearDown]
    public async Task StopContainerAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    public static async Task ResetAsync()
    {
        if (!DockerAvailable || _respawner is null || _connectionString is null)
        {
            return;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
    }

    public static AuditDbContext CreateContext()
    {
        // Mirrors MillWorksAuditBuilder.cs:173 so both this overload and CreateContext(string)
        // share the same model-cache-key strategy. Without it, EF's default cache keys on
        // context type alone and a model compiled for one schema can be returned for another.
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer(ConnectionString)
            .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()
            .Options;
        return new AuditDbContext(options);
    }

    public static AuditDbContext CreateContext(string schema)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer(ConnectionString)
            .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()
            .Options;
        var efOptions = Options.Create(new EntityFrameworkOptions { Schema = schema });
        return new AuditDbContext(options, encryptionService: null, efOptions: efOptions);
    }

    private static async Task EnsureDatabaseAndSchemasAsync(
        string targetConnectionString, string dataSource, string user, string password)
    {
        var masterBuilder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            UserID = user,
            Password = password,
            InitialCatalog = "master",
            TrustServerCertificate = true
        };

        await using (var master = new SqlConnection(masterBuilder.ConnectionString))
        {
            await master.OpenAsync();
            await using var cmd = master.CreateCommand();
            cmd.CommandText = """
                IF DB_ID('MillWorksAuditCoreTests') IS NULL
                    CREATE DATABASE [MillWorksAuditCoreTests];
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var conn = new SqlConnection(targetConnectionString);
        await conn.OpenAsync();
        await using var schemaCmd = conn.CreateCommand();
        schemaCmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'audit')
                EXEC('CREATE SCHEMA audit');
            IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'audit_custom')
                EXEC('CREATE SCHEMA audit_custom');
            """;
        await schemaCmd.ExecuteNonQueryAsync();
    }

    private static bool IsDockerUnavailable(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("Docker", StringComparison.OrdinalIgnoreCase)
               && (message.Contains("not", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("unable", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("connect", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("daemon", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("pipe", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("socket", StringComparison.OrdinalIgnoreCase));
    }
}
