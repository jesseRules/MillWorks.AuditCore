using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Exercises the production aggregate against a nonce-owned database on the isolated test
/// instance. The narrow table intentionally permits duplicates to test damaged-index data.
/// This is provider correctness evidence, not application-schema or live API qualification.
/// </summary>
[TestFixture]
[NonParallelizable]
[Category("SqlServer")]
public sealed partial class AuditSequenceIntegritySqlServerTests
{
    private readonly string _database = $"AuditCoreSequence_{Guid.NewGuid():N}";
    private string _masterConnection = null!;
    private string _testConnection = null!;
    private bool _created;

    [OneTimeSetUp]
    public async Task CreateDatabaseAsync()
    {
        if (Environment.GetEnvironmentVariable("MILLWORKS_SQL_TESTS_ENABLED") != "1")
            Assert.Ignore("Enable isolated SQL fixtures with MILLWORKS_SQL_TESTS_ENABLED=1.");

        // Never fall back to the application server on port 1433. This fixture only owns
        // its random catalog on the explicit loopback test endpoint.
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "127.0.0.1,1435",
            InitialCatalog = "master",
            UserID = "sa",
            Password = Environment.GetEnvironmentVariable("MILLWORKS_SQL_TEST_PASSWORD") ?? "Dev@12345678",
            TrustServerCertificate = true,
            ConnectTimeout = 5,
            Pooling = false
        };
        _masterConnection = builder.ConnectionString;
        await ExecuteAsync(_masterConnection, $"CREATE DATABASE [{_database}];");
        _created = true;
        builder.InitialCatalog = _database;
        _testConnection = builder.ConnectionString;
        await ExecuteAsync(_testConnection, """
            CREATE SCHEMA audit;
            """);
        await ExecuteAsync(_testConnection, """
            CREATE TABLE audit.AuditIntegrity (SequenceNumber bigint NOT NULL);
            CREATE INDEX IX_SequenceFixture ON audit.AuditIntegrity(SequenceNumber);
            """);
        await using var connection = new SqlConnection(_testConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion'));";
        TestContext.Out.WriteLine($"SQL version: {await command.ExecuteScalarAsync()}; fixture database: {_database}");
    }

    [OneTimeTearDown]
    public async Task DropOwnedDatabaseAsync()
    {
        if (!_created) return;
        await ExecuteAsync(_masterConnection,
            $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}];");
        TestContext.Out.WriteLine($"Dropped owned fixture database {_database}.");
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        (string Name, long[] Values, bool Valid)[] cases =
        [
            ("empty", [], true),
            ("singleton", [42], true),
            ("non_one_based", [41, 42, 43], true),
            ("negative", [-2, -1, 0, 1], true),
            ("gap", [1, 2, 4], false),
            ("duplicate_masking_gap", [1, 2, 2, 4], false),
            ("duplicate", [7, 7], false),
            ("int64_min", [long.MinValue, long.MinValue + 1], true),
            ("int64_max", [long.MaxValue - 1, long.MaxValue], true),
            ("int64_full_range", [long.MinValue, long.MaxValue], false)
        ];
        foreach (var rcsi in new[] { false, true })
        foreach (var item in cases)
            yield return new TestCaseData(item.Values, item.Valid, rcsi)
                .SetName($"Sequence_{item.Name}_RCSI_{rcsi}");
    }

    [TestCaseSource(nameof(Cases))]
    public async Task Summary_DetectsGapsAndDuplicates(long[] values, bool expected, bool rcsi)
    {
        await SetRcsiAsync(rcsi);
        await ExecuteAsync(_testConnection, "TRUNCATE TABLE audit.AuditIntegrity;");
        await using (var seed = new SqlConnection(_testConnection))
        {
            await seed.OpenAsync();
            foreach (var value in values)
            {
                await using var command = seed.CreateCommand();
                command.CommandText = "INSERT INTO audit.AuditIntegrity(SequenceNumber) VALUES (@sequence);";
                command.Parameters.AddWithValue("@sequence", value);
                await command.ExecuteNonQueryAsync();
            }
        }
        await VerifyAsync(expected);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Summary_OriginalRowVolume_ReturnsAggregate(bool rcsi)
    {
        await SetRcsiAsync(rcsi);
        await SeedOriginalVolumeAsync();
        await VerifyAsync(true);
        await ExecuteAsync(_testConnection, "DELETE FROM audit.AuditIntegrity WHERE SequenceNumber = 100000;");
        await VerifyAsync(false);
    }

    private Task SeedOriginalVolumeAsync() => ExecuteAsync(_testConnection, """
        TRUNCATE TABLE audit.AuditIntegrity;
        WITH digit(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) AS d(n))
        INSERT INTO audit.AuditIntegrity(SequenceNumber)
        SELECT TOP (217719) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
        FROM digit a CROSS JOIN digit b CROSS JOIN digit c CROSS JOIN digit d CROSS JOIN digit e CROSS JOIN digit f;
        """);

    private async Task VerifyAsync(bool expected)
    {
        var commands = new List<string>();
        var statistics = new List<string>();
        await using var connection = new SqlConnection(_testConnection);
        connection.InfoMessage += (_, args) => statistics.Add(args.Message);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "SET STATISTICS IO ON;";
            await setup.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer(connection, sql => sql.CommandTimeout(2))
            .LogTo(commands.Add, [RelationalEventId.CommandExecuted])
            .Options;
        await using var context = new AuditDbContext(options);
        var timer = Stopwatch.StartNew();
        var result = await new AuditIntegrityRepository(context).IsSequenceContiguousAsync();
        timer.Stop();
        var selects = commands.Where(command => command.Contains("SELECT TOP", StringComparison.Ordinal)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(selects, Has.Count.EqualTo(1));
            Assert.That(selects.Single(), Does.Contain("COUNT_BIG").And.Contain("DISTINCT")
                .And.Contain("MIN(").And.Contain("MAX("));
            Assert.That(context.ChangeTracker.Entries(), Is.Empty);
        });
        TestContext.Out.WriteLine($"Expected={expected}, aggregate elapsed={timer.Elapsed.TotalMilliseconds:F1}ms, aggregate selects={selects.Count}, control commands={commands.Count - selects.Count}");
        TestContext.Out.WriteLine(selects.Single());
        TestContext.Out.WriteLine($"SQL version={connection.ServerVersion}");
        // Consume all results from an instrumentation replay of the exact generated SQL.
        // EF's single-result reader does not retain the trailing STATISTICS IO messages.
        statistics.Clear();
        await using var instrumentationTransaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await using (var probe = connection.CreateCommand())
        {
            probe.Transaction = instrumentationTransaction;
            var sql = selects.Single();
            probe.CommandText = "SET STATISTICS IO ON;\n" + sql[sql.IndexOf("SELECT TOP", StringComparison.Ordinal)..];
            probe.CommandTimeout = 2;
            var summaryRows = 0;
            await using (var reader = await probe.ExecuteReaderAsync())
            {
                do
                {
                    while (await reader.ReadAsync()) summaryRows++;
                } while (await reader.NextResultAsync());
            }
            Assert.That(summaryRows, Is.LessThanOrEqualTo(1));
            TestContext.Out.WriteLine($"Identical-SQL instrumentation replay returned {summaryRows} summary row(s).");
        }
        await instrumentationTransaction.CommitAsync();
        Assert.That(statistics.Any(message => message.Contains("logical reads", StringComparison.Ordinal)), Is.True);
        foreach (var message in statistics) TestContext.Out.WriteLine(message);
    }

    private Task SetRcsiAsync(bool enabled) => ExecuteAsync(_masterConnection,
        $"ALTER DATABASE [{_database}] SET READ_COMMITTED_SNAPSHOT {(enabled ? "ON" : "OFF")} WITH ROLLBACK IMMEDIATE;");

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
