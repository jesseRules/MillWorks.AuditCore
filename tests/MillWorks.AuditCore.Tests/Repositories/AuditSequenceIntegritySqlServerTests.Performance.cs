using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

public sealed partial class AuditSequenceIntegritySqlServerTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    [TestCase(false, false, true)]
    [TestCase(false, true, true)]
    [TestCase(true, false, true)]
    [TestCase(true, true, true)]
    [Category("Performance")]
    [Explicit("Controlled isolated SQL profiling; opt in by this method's name.")]
    public async Task ProfileSequenceSummary(bool rcsi, bool pooling, bool uniqueIndex = false)
    {
        await SetRcsiAsync(rcsi);
        await SeedOriginalVolumeAsync();
        await ExecuteAsync(_testConnection, $"DROP INDEX IX_SequenceFixture ON audit.AuditIntegrity; CREATE {(uniqueIndex ? "UNIQUE" : "")} INDEX IX_SequenceFixture ON audit.AuditIntegrity(SequenceNumber);");
        try
        {
            var connectionString = new SqlConnectionStringBuilder(_testConnection) { Pooling = pooling }.ConnectionString;
            var capture = new SummarySqlCapture();
            await using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
                .UseSqlServer(connectionString).AddInterceptors(capture).Options);
            var repository = new AuditIntegrityRepository(context);
            Assert.That(await repository.IsSequenceContiguousAsync(), Is.True);
            var originalSql = capture.Sql!;
            var tableSql = originalSql.Replace("FROM [audit].[AuditIntegrity] AS [a]",
                "FROM [audit].[AuditIntegrity] AS [a] WITH (TABLOCK)", StringComparison.Ordinal);
            Assert.That(tableSql, Is.Not.EqualTo(originalSql));
            const string groupedSql = """
                SELECT SUM(g.Occurrences) AS [Count], COUNT_BIG(*) AS DistinctCount,
                       MIN(g.SequenceNumber) AS Minimum, MAX(g.SequenceNumber) AS Maximum
                FROM (SELECT SequenceNumber, COUNT_BIG(*) AS Occurrences
                      FROM audit.AuditIntegrity GROUP BY SequenceNumber) AS g;
                """;
            var samples = new Dictionary<string, List<double>>
            {
                ["protected_ef"] = [], ["original_unprotected_ef"] = [], ["serializable_raw"] = [], ["serializable_tablock_raw"] = [], ["serializable_grouped_raw"] = []
            };
            var names = samples.Keys.ToArray();
            // Warm all paths, then rotate order to reduce cache/order bias. Never clear shared caches.
            for (var iteration = -2; iteration < 9; iteration++)
            for (var offset = 0; offset < names.Length; offset++)
            {
                var name = names[(iteration + names.Length + offset) % names.Length];
                var timer = Stopwatch.StartNew();
                if (name == "protected_ef")
                    Assert.That(await repository.IsSequenceContiguousAsync(), Is.True);
                else if (name == "original_unprotected_ef")
                    Assert.That(await ProfileOriginalAggregateAsync(context), Is.True);
                else
                    await ProfileRawSummaryAsync(connectionString,
                        name == "serializable_raw" ? originalSql : name == "serializable_grouped_raw" ? groupedSql : tableSql);
                timer.Stop();
                if (iteration >= 0) samples[name].Add(timer.Elapsed.TotalMilliseconds);
            }
            var output = samples.ToDictionary(pair => pair.Key, pair => new
            {
                samplesMs = pair.Value,
                medianMs = pair.Value.Order().ElementAt(pair.Value.Count / 2),
                minimumMs = pair.Value.Min(), maximumMs = pair.Value.Max()
            });
            TestContext.Out.WriteLine(JsonSerializer.Serialize(new { rcsi, pooling, uniqueIndex, rows = 217719, output },
                new JsonSerializerOptions { WriteIndented = true }));
            foreach (var (name, sql) in new[] { ("row_locks", originalSql), ("table_lock", tableSql), ("grouped", groupedSql) })
                await RetainSummaryPlanAsync(connectionString, sql, $"rcsi-{rcsi}-pool-{pooling}-unique-{uniqueIndex}-{name}");
        }
        finally
        {
            // Keep corruption fixtures valid if a wider test selection also runs this profile.
            SqlConnection.ClearAllPools();
            await ExecuteAsync(_testConnection,
                "DROP INDEX IX_SequenceFixture ON audit.AuditIntegrity; CREATE INDEX IX_SequenceFixture ON audit.AuditIntegrity(SequenceNumber);");
        }
    }

    // Diagnostic baseline only: the pre-protection query, with no concurrent writers in this
    // explicit benchmark. Never select this path in production or concurrency qualification.
    private static async Task<bool> ProfileOriginalAggregateAsync(AuditDbContext context)
    {
        var summary = await context.AuditIntegrity.AsNoTracking().GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.LongCount(),
                DistinctCount = group.Select(row => row.SequenceNumber).Distinct().LongCount(),
                Minimum = group.Min(row => row.SequenceNumber),
                Maximum = group.Max(row => row.SequenceNumber)
            }).SingleOrDefaultAsync();
        return summary is not null && summary.Count == 217719 && summary.DistinctCount == 217719
            && summary.Minimum == 1 && summary.Maximum == 217719;
    }

    private static async Task ProfileRawSummaryAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "SET LOCK_TIMEOUT 1500;";
            await setup.ExecuteNonQueryAsync();
        }
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = 2;
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.That(await reader.ReadAsync(), Is.True);
            Assert.That(reader.GetInt64(0), Is.EqualTo(217719));
            Assert.That(reader.GetInt64(1), Is.EqualTo(217719));
            Assert.That(await reader.ReadAsync(), Is.False);
        }
        await transaction.CommitAsync();
    }

    private static async Task RetainSummaryPlanAsync(string connectionString, string sql, string name)
    {
        var messages = new List<string>();
        await using var connection = new SqlConnection(connectionString);
        connection.InfoMessage += (_, args) => messages.Add(args.Message);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SET STATISTICS IO ON; SET STATISTICS XML ON;\n" + sql;
            await using var reader = await command.ExecuteReaderAsync();
            do
            {
                while (await reader.ReadAsync())
                {
                    if (reader.FieldCount == 1 && reader.GetValue(0) is string xml && xml.Contains("ShowPlanXML", StringComparison.Ordinal))
                    {
                        var folder = Path.Combine(Path.GetTempPath(), "audit-sequence-performance-20261016");
                        Directory.CreateDirectory(folder);
                        var path = Path.Combine(folder, name + ".sqlplan");
                        await File.WriteAllTextAsync(path, xml);
                        TestContext.AddTestAttachment(path, name);
                    }
                }
            } while (await reader.NextResultAsync());
        }
        await transaction.CommitAsync();
        TestContext.Out.WriteLine(name + "\n" + string.Join("\n", messages));
    }

    private sealed class SummarySqlCapture : DbCommandInterceptor
    {
        public string? Sql { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT_BIG", StringComparison.Ordinal)) Sql = command.CommandText;
            return ValueTask.FromResult(result);
        }
    }
}
