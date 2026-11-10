using System.Globalization;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

[TestFixture, NonParallelizable, Category("SqlServer")]
public sealed class AuditEventDistinctValuesSqlServerTests
{
    private readonly string database = "AuditOptions_" + Guid.NewGuid().ToString("N");
    private string master = null!, connection = null!;
    private bool created;
    private readonly Dictionary<AuditDbContext, StatisticsCommands> measuredContexts = [];

    [OneTimeSetUp]
    public async Task CreateOwnedCatalog()
    {
        if (Environment.GetEnvironmentVariable("MILLWORKS_SQL_TESTS_ENABLED") != "1")
            Assert.Ignore("Requires the existing isolated SQL endpoint on1435.");
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "127.0.0.1,1435", InitialCatalog = "master", UserID = "sa",
            Password = Environment.GetEnvironmentVariable("MILLWORKS_SQL_TEST_PASSWORD") ?? "Dev@12345678",
            TrustServerCertificate = true, Pooling = false, ConnectTimeout = 5
        };
        master = builder.ConnectionString;
        await Execute(master, $"CREATE DATABASE [{database}];");
        created = true;
        builder.InitialCatalog = database;
        connection = builder.ConnectionString;
        await Execute(connection, "EXEC(N'CREATE SCHEMA audit');");
        await Execute(connection, """
            CREATE TABLE audit.AuditEvents(EventId uniqueidentifier NOT NULL PRIMARY KEY,
                InsertedDate datetimeoffset NULL,EventType nvarchar(256) NULL,[User] nvarchar(256) NULL);
            WITH d(n) AS(SELECT n FROM(VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9))a(n)),
            nums AS(SELECT TOP(100000) ROW_NUMBER()OVER(ORDER BY(SELECT NULL)) n
                FROM d a CROSS JOIN d b CROSS JOIN d c CROSS JOIN d e CROSS JOIN d f)
            INSERT audit.AuditEvents SELECT NEWID(),DATEADD(second,CONVERT(int,n),CONVERT(datetimeoffset,'2026-01-01')),
                CASE WHEN n%23=0 THEN NULL WHEN n%23=1 THEN N'' ELSE CONCAT('Event.',n%19,REPLICATE('e',60)) END,
                CASE WHEN n%11=0 THEN NULL WHEN n%11=1 THEN N'' ELSE CONCAT('User.',n%31,REPLICATE('u',80)) END FROM nums;
            CREATE INDEX IX_AuditEvents_Date_Type ON audit.AuditEvents(InsertedDate,EventType) INCLUDE([User]);
            CREATE INDEX IX_AuditEvents_EventType ON audit.AuditEvents(EventType);
            """);
    }

    [OneTimeTearDown]
    public async Task DropOwnedCatalog()
    {
        if (!created) return;
        await Execute(master, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
        TestContext.Out.WriteLine("Dropped owned fixture " + database);
    }

    private AuditDbContext Context(string schema = "audit")
    {
        var commands = new StatisticsCommands();
        var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlServer(connection)
            .AddInterceptors(commands).ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>().Options,
            efOptions: Options.Create(new EntityFrameworkOptions { Schema = schema }));
        measuredContexts.Add(db, commands);
        return db;
    }

    [Test]
    public async Task IncrementalIndexAndSeeks_ReduceReads_PreserveValuesAndRollback()
    {
        await using var db = Context();
        var baselineUsers = await Measure(db, () => db.AuditEvents.Where(e => e.User != null && e.User != "")
            .Select(e => e.User!).Distinct().OrderBy(value => value).ToListAsync());
        var baselineTypes = await Measure(db, () => db.AuditEvents.Where(e => e.EventType != null)
            .Select(e => e.EventType!).Distinct().OrderBy(value => value).ToListAsync());
        var missingIndex = await AuditEventDistinctValues.UsersAsync(db);
        Assert.That(missingIndex, Is.EqualTo(baselineUsers.Values));
        var migration = new MillWorks.AuditCore.EntityFramework.Migrations.AddAuditEventUserIndex();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.UpOperations, db.Model))
            await Execute(connection, command.CommandText);
        var users = await Measure(db, () => AuditEventDistinctValues.UsersAsync(db));
        var types = await Measure(db, () => AuditEventDistinctValues.EventTypesAsync(db));
        Assert.Multiple(() =>
        {
            Assert.That(baselineUsers.Reads, Is.GreaterThan(0));
            Assert.That(baselineTypes.Reads, Is.GreaterThan(0));
            Assert.That(users.Values, Is.EqualTo(baselineUsers.Values));
            Assert.That(types.Values, Is.EqualTo(baselineTypes.Values));
            Assert.That(users.Reads, Is.LessThan(baselineUsers.Reads / 2));
            Assert.That(types.Reads, Is.LessThan(baselineTypes.Reads / 2));
        });
        TestContext.Out.WriteLine(JsonSerializer.Serialize(new { BaselineUsers = baselineUsers, BaselineTypes = baselineTypes, Users = users, Types = types }));
        foreach (var command in generator.Generate(migration.DownOperations, db.Model))
            await Execute(connection, command.CommandText);
        Assert.That(await AuditEventDistinctValues.UsersAsync(db), Is.EqualTo(baselineUsers.Values));
        // Leave the fixture with the new index for independent fallback/contract cases.
        foreach (var command in generator.Generate(migration.UpOperations, db.Model))
            await Execute(connection, command.CommandText);
    }

    [Test, DependsOnTest(nameof(IncrementalIndexAndSeeks_ReduceReads_PreserveValuesAndRollback), AllowFailure = true)]
    public async Task HighCardinality_FallsBackWithoutTruncation_AndPreservesLimits()
    {
        await Execute(connection, """
            WITH n(v) AS(SELECT TOP(200) ROW_NUMBER()OVER(ORDER BY(SELECT NULL)) FROM sys.all_objects)
            INSERT audit.AuditEvents(EventId,EventType,[User]) SELECT NEWID(),CONCAT('High.',v),CONCAT('High.',v) FROM n;
            """);
        try
        {
            await using var db = Context();
            var users = await db.AuditEvents.Where(e => e.User != null && e.User != "").Select(e => e.User!)
                .Distinct().OrderBy(value => value).ToListAsync();
            var types = await db.AuditEvents.Where(e => e.EventType != null).Select(e => e.EventType!)
                .Distinct().OrderBy(value => value).ToListAsync();
            Assert.Multiple(() =>
            {
                Assert.That(users.Count, Is.GreaterThan(128));
                Assert.That(types.Count, Is.GreaterThan(128));
            });
            Assert.That(await AuditEventDistinctValues.UsersAsync(db), Is.EqualTo(users));
            Assert.That(await AuditEventDistinctValues.UsersAsync(db, 150), Is.EqualTo(users.Take(150)));
            Assert.That(await AuditEventDistinctValues.EventTypesAsync(db), Is.EqualTo(types));
            Assert.That(await AuditEventDistinctValues.EventTypesAsync(db, true, 5), Is.EqualTo(types.Where(value => value != "").Take(5)));
        }
        finally { await Execute(connection, "DELETE audit.AuditEvents WHERE EventType LIKE N'High.%';"); }
    }

    [Test, DependsOnTest(nameof(HighCardinality_FallsBackWithoutTruncation_AndPreservesLimits), AllowFailure = true)]
    public async Task CustomQuotedSchemaAndColumnCollation_KeepDatabaseSemantics()
    {
        await Execute(connection, """
            EXEC(N'CREATE SCHEMA [audit_options]]]');
            CREATE TABLE [audit_options]]].AuditEvents(EventId uniqueidentifier NOT NULL PRIMARY KEY,
                [User] nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL,EventType nvarchar(256) NULL);
            CREATE INDEX IX_AuditEvents_User ON [audit_options]]].AuditEvents([User]);
            INSERT [audit_options]]].AuditEvents VALUES(NEWID(),N'a',N''),(NEWID(),N'A',N'Created'),(NEWID(),NULL,NULL),(NEWID(),N'',N'Created');
            """);
        await using var db = Context("audit_options]");
        var expected = await db.AuditEvents.Where(e => e.User != null && e.User != "").Select(e => e.User!)
            .Distinct().OrderBy(value => value).ToListAsync();
        Assert.That(await AuditEventDistinctValues.UsersAsync(db), Is.EqualTo(expected));
        Assert.That(expected, Has.Count.EqualTo(2));
        Assert.That(await AuditEventDistinctValues.EventTypesAsync(db), Is.EqualTo(new[] { "", "Created" }));
    }

    [Test, DependsOnTest(nameof(CustomQuotedSchemaAndColumnCollation_KeepDatabaseSemantics), AllowFailure = true)]
    public async Task GlobalQueryFilterAndCancellation_AreNotBypassed()
    {
        await using var db = new FilteredContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlServer(connection).Options);
        var expected = await db.AuditEvents.Select(e => e.User!).Distinct().OrderBy(value => value).ToListAsync();
        Assert.That(expected, Has.Count.EqualTo(1));
        Assert.That(await AuditEventDistinctValues.UsersAsync(db), Is.EqualTo(expected));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () => await AuditEventDistinctValues.UsersAsync(db, cancellationToken: cancelled.Token));
    }

    private sealed class FilteredContext(DbContextOptions<AuditDbContext> options) : AuditDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<AuditEventEntity>().HasQueryFilter(e => e.User != null && e.User.StartsWith("User.0"));
        }
    }

    private sealed record Reading(List<string> Values, int Reads);
    private async Task<Reading> Measure(AuditDbContext db, Func<Task<List<string>>> read)
    {
        var values = await read();
        var captured = measuredContexts[db];
        var messages = new List<string>();
        // EF stops after the first result; SqlClient can discard the trailing STATISTICS message.
        // Replay the exact captured command/parameters, drain every result, and assert identical values.
        await using var sql = new SqlConnection(connection);
        sql.InfoMessage += (_, e) => messages.Add(e.Message);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = "SET STATISTICS IO ON;\n" + captured.Text;
        command.Parameters.AddRange(captured.Parameters.Select(parameter =>
            (SqlParameter)((ICloneable)parameter).Clone()).ToArray());
        var replay = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                while (await reader.ReadAsync()) replay.Add(reader.GetString(0));
            } while (await reader.NextResultAsync());
        }
        Assert.That(replay, Is.EqualTo(values), "The measured command must reproduce the EF result.");
        var reads = messages.Sum(message => Regex.Matches(message, @"(?<!lob )logical reads (\d+)")
            .Sum(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
        return new Reading(values, reads);
    }

    private sealed class StatisticsCommands : DbCommandInterceptor
    {
        internal string Text { get; private set; } = "";
        internal SqlParameter[] Parameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Text = command.CommandText;
            Parameters = command.Parameters.Cast<SqlParameter>()
                .Select(parameter => (SqlParameter)((ICloneable)parameter).Clone()).ToArray();
            return ValueTask.FromResult(result);
        }
    }

    private static async Task Execute(string connectionString, string text)
    {
        await using var sql = new SqlConnection(connectionString);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = text;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }
}
