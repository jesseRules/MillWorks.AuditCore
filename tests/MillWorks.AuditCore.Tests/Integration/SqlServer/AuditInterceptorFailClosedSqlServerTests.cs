using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Services.Diagnostics;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

public sealed class AuditInterceptorFailClosedSqlServerTests
{
    private const string InterceptorDatabaseName = "MillWorksAuditCoreInterceptorTests";

    private string _connectionString = null!;

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
            InitialCatalog = InterceptorDatabaseName
        };
        _connectionString = builder.ConnectionString;

        await DropAndCreateDatabaseAsync();

        await using var ctx = new FailClosedTestDbContext(BuildOptions(interceptor: null));
        await ctx.Database.EnsureCreatedAsync();
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
    public void SaveChangesAsync_FailClosedAlways_AuditFailure_RollsBackBusinessRow()
    {
        var diagnostics = new AuditDiagnostics();
        var interceptor = new AuditSaveChangesInterceptor(
            logger: new ThrowingLogger<AuditSaveChangesInterceptor>(),
            diagnostics: diagnostics,
            failureMode: AuditFailureMode.FailClosedAlways,
            failurePolicy: new RegulatedEntityFailurePolicy());

        AuditIntegrityException? thrown;
        using (var ctx = new FailClosedTestDbContext(BuildOptions(interceptor)))
        {
            ctx.PlainEntities.Add(new PlainTestEntity { Name = "rollback-target" });
            thrown = Assert.ThrowsAsync<AuditIntegrityException>(async () =>
                await ctx.SaveChangesAsync());
        }

        using var verify = new FailClosedTestDbContext(BuildOptions(interceptor: null));
        Assert.Multiple(() =>
        {
            Assert.That(thrown, Is.Not.Null);
            Assert.That(thrown!.EntityName, Is.EqualTo(nameof(PlainTestEntity)));
            Assert.That(thrown.Action, Is.EqualTo(nameof(AuditAction.Created)));
            Assert.That(verify.PlainEntities.Count(), Is.Zero,
                "Business row must be rolled back when the interceptor throws under SQL Server's transaction.");
            Assert.That(verify.Set<AuditLogEntity>().Count(), Is.Zero,
                "Audit-log row must also be rolled back atomically with the business row.");
            Assert.That(diagnostics.InterceptorAuditFailureCount, Is.EqualTo(1),
                "Interceptor failure should be counted exactly once.");
        });
    }

    private DbContextOptions<FailClosedTestDbContext> BuildOptions(AuditSaveChangesInterceptor? interceptor)
    {
        // ReplaceService mirrors MillWorksAuditBuilder.cs:173 and the SQL Server fixture's
        // CreateContext overloads so this test's model cache is keyed consistently with
        // every other context constructed in the SqlServer integration namespace.
        var builder = new DbContextOptionsBuilder<FailClosedTestDbContext>()
            .UseSqlServer(_connectionString)
            .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>();

        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        return builder.Options;
    }

    private async Task DropAndCreateDatabaseAsync()
    {
        await DropDatabaseIfExistsAsync();
        await using var master = CreateMasterConnection();
        await master.OpenAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{InterceptorDatabaseName}];";
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
            IF DB_ID('{InterceptorDatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{InterceptorDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{InterceptorDatabaseName}];
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

    // ── Test fixture types ───────────────────────────────────────────────────

    private sealed class FailClosedTestDbContext : AuditDbContext
    {
        public FailClosedTestDbContext(DbContextOptions<FailClosedTestDbContext> options)
            : base(options)
        {
        }

        public DbSet<PlainTestEntity> PlainEntities { get; set; } = null!;
    }

    private sealed class PlainTestEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>
    /// Throws on <see cref="LogLevel.Debug"/> only so the interceptor's
    /// <c>ProcessAuditableEntries</c> catch triggers on its in-loop LogDebug call.
    /// All other log levels — notably the Error-level swallow log — pass through silently.
    /// </summary>
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug)
                throw new InvalidOperationException("test-induced audit-log build failure");
        }
    }
}
