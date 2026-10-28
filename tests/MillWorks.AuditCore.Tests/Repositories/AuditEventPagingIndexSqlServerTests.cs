using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>Compares the incremental paging index on an owned catalog on the existing test endpoint.</summary>
[TestFixture]
[NonParallelizable]
[Category("SqlServer")]
public sealed class AuditEventPagingIndexSqlServerTests
{
    private const string IndexName = "IX_AuditEvents_Date_EventId";
    private readonly string _database = $"AuditCorePaging_{Guid.NewGuid():N}";
    private string _master = null!;
    private string _connection = null!;
    private bool _created;

    [OneTimeSetUp]
    public async Task CreateOwnedDatabase()
    {
        if (Environment.GetEnvironmentVariable("MILLWORKS_SQL_TESTS_ENABLED") != "1")
            Assert.Ignore("Enable isolated SQL fixtures with MILLWORKS_SQL_TESTS_ENABLED=1.");
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "127.0.0.1,1435", InitialCatalog = "master", UserID = "sa",
            Password = Environment.GetEnvironmentVariable("MILLWORKS_SQL_TEST_PASSWORD") ?? "Dev@12345678",
            TrustServerCertificate = true, ConnectTimeout = 5, Pooling = false
        };
        _master = builder.ConnectionString;
        await Execute(_master, $"CREATE DATABASE [{_database}];");
        _created = true;
        builder.InitialCatalog = _database;
        _connection = builder.ConnectionString;
        await Execute(_connection, """
            CREATE SCHEMA audit;
            """);
        await Execute(_connection, """
            CREATE TABLE audit.AuditEvents (
                EventId uniqueidentifier NOT NULL PRIMARY KEY CLUSTERED,
                InsertedDate datetimeoffset NULL, EventType nvarchar(256) NULL,
                [User] nvarchar(256) NULL, JsonData nvarchar(max) NULL);
            WITH digit(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS d(n)),
            numbers AS (SELECT TOP(100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM digit a CROSS JOIN digit b CROSS JOIN digit c CROSS JOIN digit d CROSS JOIN digit e)
            INSERT audit.AuditEvents(EventId,InsertedDate,EventType,[User],JsonData)
            SELECT NEWID(), CASE WHEN n=100000 THEN NULL ELSE DATEADD(second,-CONVERT(int,n/10),
                CONVERT(datetimeoffset,'2026-10-19T12:00:00Z')) END,
                CONCAT('Event.',n%11,REPLICATE('e',60)),CONCAT('User.',n%31,REPLICATE('u',80)),
                CONCAT('{"value":"',REPLICATE('x',256),'"}') FROM numbers;
            CREATE INDEX IX_AuditEvents_Date_Type ON audit.AuditEvents(InsertedDate,EventType) INCLUDE([User]);
            """);
    }

    [OneTimeTearDown]
    public async Task DropOwnedDatabase()
    {
        if (!_created) return;
        await Execute(_master, $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}];");
        TestContext.Out.WriteLine($"Dropped owned fixture {_database}.");
    }

    [Test]
    public async Task MigrationRemovesKeySortAndReducesReadsWithoutChangingPagesOrCounts()
    {
        await using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer(_connection).Options);
        var cutoff = DateTimeOffset.Parse("2026-10-19T11:59:50Z", CultureInfo.InvariantCulture);
        var filtered = context.AuditEvents.Where(e => e.InsertedDate <= cutoff);
        var ordered = filtered.OrderByDescending(e => e.InsertedDate).ThenByDescending(e => e.EventId);
        var unfiltered = context.AuditEvents.OrderByDescending(e => e.InsertedDate).ThenByDescending(e => e.EventId);
        var queries = new[] { AuditEventPaging.SelectKeys(unfiltered, 0, 25).ToQueryString() }
            .Concat(new[] { 0, 25, 10000 }.Select(offset =>
                AuditEventPaging.SelectKeys(ordered, offset, 25).ToQueryString())).ToArray();
        const string countSql = "SELECT COUNT(*) FROM audit.AuditEvents WHERE InsertedDate <= '2026-10-19T11:59:50Z';";
        var before = new List<Measurement>();
        foreach (var sql in queries) before.Add(await Measure(sql));
        var countBefore = await Measure(countSql);

        // Execute the actual generated migration operations against this owned fixture only.
        var assembly = context.GetService<IMigrationsAssembly>();
        var migrationType = assembly.Migrations.Single(x => x.Key.EndsWith("_AddAuditEventPagingIndex", StringComparison.Ordinal)).Value;
        var migration = assembly.CreateMigration(migrationType, context.Database.ProviderName!);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.UpOperations, context.Model))
            await Execute(_connection, command.CommandText);
        var after = new List<Measurement>();
        foreach (var sql in queries) after.Add(await Measure(sql));
        var countAfter = await Measure(countSql);
        for (var i = 0; i < queries.Length; i++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(after[i].Ids, Is.EqualTo(before[i].Ids), $"page {i} IDs/order");
                Assert.That(after[i].Ids, Has.Count.EqualTo(25));
                Assert.That(after[i].Sorts, Is.Zero, $"page {i} sort");
                Assert.That(after[i].Indexes, Does.Contain(IndexName));
                Assert.That(after[i].LogicalReads, Is.LessThan(before[i].LogicalReads));
            });
        }
        Assert.Multiple(() =>
        {
            Assert.That(countAfter.Count, Is.EqualTo(countBefore.Count));
            Assert.That(countAfter.LogicalReads, Is.LessThan(countBefore.LogicalReads));
        });
        foreach (var command in generator.Generate(migration.DownOperations, context.Model))
            await Execute(_connection, command.CommandText);
        var rollback = await Measure(queries[0]);
        Assert.That(rollback.Ids, Is.EqualTo(before[0].Ids));
        Assert.That(rollback.Indexes, Does.Not.Contain(IndexName));
        TestContext.Out.WriteLine(JsonSerializer.Serialize(new { Before = before, After = after, CountBefore = countBefore,
            CountAfter = countAfter, Rollback = rollback, Rows = 100000, Fixture = _database }));
    }

    private sealed record Measurement(List<Guid> Ids, int? Count, int LogicalReads, int Sorts,
        string[] Indexes, long Milliseconds);

    private async Task<Measurement> Measure(string sql)
    {
        var messages = new List<string>();
        await using var connection = new SqlConnection(_connection);
        connection.InfoMessage += (_, args) => messages.Add(args.Message);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = "SET STATISTICS IO ON; SET STATISTICS XML ON;\n" + sql;
        var ids = new List<Guid>();
        int? count = null;
        var plans = new List<XDocument>();
        var timer = Stopwatch.StartNew();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            do
            {
                while (await reader.ReadAsync())
                {
                    var value = reader.GetValue(0);
                    if (value is Guid id) ids.Add(id);
                    else if (value is int result) count = result;
                    else if (value is string xml && xml.Contains("ShowPlanXML", StringComparison.Ordinal))
                        plans.Add(XDocument.Parse(xml));
                }
            } while (await reader.NextResultAsync());
        }
        timer.Stop();
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var indexes = plans.SelectMany(p => p.Descendants(ns + "Object"))
            .Select(e => e.Attribute("Index")?.Value.Trim('[', ']')).OfType<string>().Distinct().ToArray();
        var sorts = plans.Sum(p => p.Descendants(ns + "RelOp").Count(e => e.Attribute("PhysicalOp")?.Value == "Sort"));
        var reads = messages.Sum(message => Regex.Matches(message, @"(?<!lob )logical reads (\d+)")
            .Sum(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)));
        Assert.That(plans, Is.Not.Empty, "Actual SQL plan must be captured.");
        Assert.That(reads, Is.GreaterThan(0), "Actual logical reads must be captured.");
        return new Measurement(ids, count, reads, sorts, indexes, timer.ElapsedMilliseconds);
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }
}
