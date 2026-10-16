using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

public sealed partial class AuditSequenceIntegritySqlServerTests
{
    [TestCase(false, "append", true, true)]
    [TestCase(true, "append", true, true)]
    [TestCase(false, "append", false, true)]
    [TestCase(true, "append", false, true)]
    [TestCase(false, "prefix", true, true)]
    [TestCase(true, "prefix", true, true)]
    [TestCase(false, "interior", true, false)]
    [TestCase(true, "interior", true, false)]
    [TestCase(false, "interior", false, true)]
    [TestCase(true, "interior", false, true)]
    public async Task Summary_WaitsForWriterAndSeesCommittedState(bool rcsi, string mutation, bool commit, bool expected)
    {
        await PrepareConcurrencyAsync(rcsi);
        await using var writer = new SqlConnection(_testConnection);
        await writer.OpenAsync();
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync();
        await MutateAsync(writer, transaction, mutation);
        var probe = new SequenceReadProbe();
        await using var context = CreateProbeContext(probe);
        var timer = Stopwatch.StartNew();
        var scan = new AuditIntegrityRepository(context).IsSequenceContiguousAsync();
        try
        {
            await probe.Executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForBlockingAsync(probe.Connections.Single().ServerProcessId, writer.ServerProcessId, probe.Connections.Single().ServerProcessId);
            Assert.That(scan.IsCompleted, Is.False, "The summary must not bypass the uncommitted writer, even with RCSI on.");
            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
            Assert.That(await scan.WaitAsync(TimeSpan.FromSeconds(3)), Is.EqualTo(expected));
            Assert.That(probe.Isolations, Is.All.EqualTo(IsolationLevel.Serializable));
            Assert.That(probe.Timeouts, Is.All.EqualTo(2));
            Assert.That(probe.Connections, Has.Count.EqualTo(1), "Valid/invalid summaries are not retried.");
            Assert.That(context.ChangeTracker.Entries(), Is.Empty);
            TestContext.Out.WriteLine($"RCSI={rcsi}; mutation={mutation}; commit={commit}; observed writer blocking; scan={timer.Elapsed.TotalMilliseconds:F1}ms; isolation=Serializable");
        }
        finally
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync();
            try { await scan; } catch { /* Preserve the original test failure after releasing the writer. */ }
        }
    }

    [TestCase(false, "append", true)]
    [TestCase(true, "append", true)]
    [TestCase(false, "prefix", true)]
    [TestCase(true, "prefix", true)]
    [TestCase(false, "interior", false)]
    [TestCase(true, "interior", false)]
    [TestCase(false, "volumeAppend", true, true)]
    [TestCase(true, "volumeAppend", true, true)]
    public async Task Summary_HoldsSerializableLocksUntilCompletion(bool rcsi, string mutation, bool afterValid, bool originalVolume = false)
    {
        await PrepareConcurrencyAsync(rcsi);
        if (originalVolume) await SeedOriginalVolumeAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new SequenceReadProbe { AfterReader = ct => release.Task.WaitAsync(ct) };
        await using var context = CreateProbeContext(probe);
        var scanTimer = Stopwatch.StartNew();
        var scan = new AuditIntegrityRepository(context).IsSequenceContiguousAsync();
        await using var writer = new SqlConnection(_testConnection);
        await writer.OpenAsync();
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync();
        Task? write = null;
        try
        {
            await probe.ReaderReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var writerTimer = Stopwatch.StartNew();
            write = MutateAsync(writer, transaction, mutation);
            await WaitForBlockingAsync(writer.ServerProcessId, probe.Connections.Single().ServerProcessId, probe.Connections.Single().ServerProcessId);
            Assert.That(write.IsCompleted, Is.False, "Range/read locks must protect the whole summary until its transaction completes.");
            release.TrySetResult();
            Assert.That(await scan.WaitAsync(TimeSpan.FromSeconds(3)), Is.True, "The serialized pre-mutation sequence was valid.");
            await write.WaitAsync(TimeSpan.FromSeconds(3));
            await transaction.CommitAsync();
            TestContext.Out.WriteLine($"RCSI={rcsi}; rows={(originalVolume ? 217719 : 4)}; mutation={mutation}; reader-first scan={scanTimer.Elapsed.TotalMilliseconds:F1}ms; writer including lock wait/commit={writerTimer.Elapsed.TotalMilliseconds:F1}ms");
            Assert.That(scanTimer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "Include observed lock overlap in the per-scan budget.");
            Assert.That(writerTimer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)), "Record and bound writer interference in this fixture.");
            Assert.That(probe.Isolations, Is.All.EqualTo(IsolationLevel.Serializable));
            await using var verify = CreateProbeContext(new SequenceReadProbe());
            Assert.That(await new AuditIntegrityRepository(verify).IsSequenceContiguousAsync(), Is.EqualTo(afterValid));
        }
        finally
        {
            release.TrySetResult();
            try { await scan; } catch { }
            if (write is not null) { try { await write; } catch { } }
            if (transaction.Connection is not null) await transaction.RollbackAsync();
        }
    }

    [Test]
    public async Task Summary_UsesFreshSerializableTransactionDespiteWeakerCallerAndProviderRetries()
    {
        await PrepareConcurrencyAsync(true);
        var probe = new SequenceReadProbe();
        await using var context = CreateProbeContext(probe, enableRetries: true);
        // A caller's explicit transaction must not be reused by the independent read.
        await context.Database.OpenConnectionAsync();
        await using var caller = await context.Database.BeginTransactionAsync(IsolationLevel.ReadUncommitted);
        var source = (SqlConnection)context.Database.GetDbConnection();
        Assert.That(await new AuditIntegrityRepository(context).IsSequenceContiguousAsync(), Is.True);
        Assert.That(probe.Isolations, Is.EqualTo(new[] { IsolationLevel.Serializable }));
        Assert.That(probe.Connections.Single(), Is.Not.SameAs(source));
        Assert.That(caller.GetDbTransaction().IsolationLevel, Is.EqualTo(IsolationLevel.ReadUncommitted));
        Assert.That(source.State, Is.EqualTo(ConnectionState.Open));
        await caller.RollbackAsync();
    }

    [Test]
    public async Task Summary_SuppressesAmbientTransaction()
    {
        await PrepareConcurrencyAsync(true);
        var probe = new SequenceReadProbe();
        await using var context = CreateProbeContext(probe);
        using var ambient = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.RequiresNew,
            new System.Transactions.TransactionOptions { IsolationLevel = System.Transactions.IsolationLevel.ReadUncommitted },
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        Assert.That(await new AuditIntegrityRepository(context).IsSequenceContiguousAsync(), Is.True);
        Assert.That(probe.AmbientPresent, Is.All.False);
        Assert.That(System.Transactions.Transaction.Current, Is.Not.Null, "The caller's ambient scope is restored.");
        ambient.Complete();
    }

    [Test]
    public async Task Summary_LockTimeoutRetriesTwiceWithFreshConnectionsThenSucceeds()
    {
        await PrepareConcurrencyAsync(false);
        await using var writer = new SqlConnection(_testConnection);
        await writer.OpenAsync();
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync();
        await MutateAsync(writer, transaction, "append");
        var probe = new SequenceReadProbe();
        probe.BeforeReader = async _ =>
        {
            if (probe.Connections.Count == 3) await transaction.CommitAsync();
        };
        await using var context = CreateProbeContext(probe, enableRetries: true);
        var timer = Stopwatch.StartNew();
        try
        {
            Assert.That(await new AuditIntegrityRepository(context).IsSequenceContiguousAsync(), Is.True);
            Assert.That(probe.Connections, Has.Count.EqualTo(3));
            Assert.That(probe.Connections.Distinct().Count(), Is.EqualTo(3));
            Assert.That(probe.ClientConnectionIds.Distinct().Count(), Is.EqualTo(3));
            Assert.That(probe.ContextIds.Distinct().Count(), Is.EqualTo(3));
            Assert.That(probe.Connections.Select(c => c.State), Is.All.EqualTo(ConnectionState.Closed));
            Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(7)));
            TestContext.Out.WriteLine($"Two SQL lock timeouts then success: {timer.Elapsed.TotalMilliseconds:F1}ms; fresh contexts/connections=3");
        }
        finally
        {
            if (transaction.Connection is not null) await transaction.RollbackAsync();
        }
    }

    [Test]
    public async Task Summary_PersistentLockTimeoutThrowsInsteadOfReportingCorruption()
    {
        await PrepareConcurrencyAsync(false);
        await using var writer = new SqlConnection(_testConnection);
        await writer.OpenAsync();
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync();
        await MutateAsync(writer, transaction, "append");
        var probe = new SequenceReadProbe();
        await using var context = CreateProbeContext(probe, enableRetries: true);
        var timer = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<SqlException>(async () =>
            await new AuditIntegrityRepository(context).IsSequenceContiguousAsync());
        Assert.That(error!.Number, Is.EqualTo(1222));
        Assert.That(probe.Connections, Has.Count.EqualTo(3));
        Assert.That(timer.Elapsed, Is.LessThan(TimeSpan.FromSeconds(7)));
        Assert.That(probe.Connections.Select(c => c.State), Is.All.EqualTo(ConnectionState.Closed));
        TestContext.Out.WriteLine($"Exhausted SQL lock retries: {timer.Elapsed.TotalMilliseconds:F1}ms");
        await transaction.RollbackAsync();
    }

    [Test]
    public async Task Summary_CancellationWhileBlockedIsNotRetried()
    {
        await PrepareConcurrencyAsync(false);
        await using var writer = new SqlConnection(_testConnection);
        await writer.OpenAsync();
        await using var transaction = (SqlTransaction)await writer.BeginTransactionAsync();
        await MutateAsync(writer, transaction, "append");
        var probe = new SequenceReadProbe();
        await using var context = CreateProbeContext(probe);
        using var cancellation = new CancellationTokenSource();
        var scan = new AuditIntegrityRepository(context).IsSequenceContiguousAsync(cancellation.Token);
        try
        {
            await probe.Executing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForBlockingAsync(probe.Connections.Single().ServerProcessId, writer.ServerProcessId, probe.Connections.Single().ServerProcessId);
            cancellation.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await scan);
            Assert.That(probe.Connections, Has.Count.EqualTo(1));
            Assert.That(probe.Connections.Single().State, Is.EqualTo(ConnectionState.Closed));
        }
        finally
        {
            cancellation.Cancel();
            await transaction.RollbackAsync();
            try { await scan; } catch { }
        }
    }

    [Test]
    public async Task Summary_OverallDeadlineThrowsAndClosesConnection()
    {
        await PrepareConcurrencyAsync(true);
        var probe = new SequenceReadProbe { BeforeReader = ct => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        await using var context = CreateProbeContext(probe);
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await new AuditIntegrityRepository(context).IsSequenceContiguousAsync());
        Assert.That(timer.Elapsed, Is.InRange(TimeSpan.FromSeconds(6.5), TimeSpan.FromSeconds(7)));
        Assert.That(probe.Connections, Has.Count.EqualTo(1));
        Assert.That(probe.Connections.Single().State, Is.EqualTo(ConnectionState.Closed));
        TestContext.Out.WriteLine($"Overall deadline including cleanup: {timer.Elapsed.TotalMilliseconds:F1}ms (250 ms cleanup reserve)");
    }

    [Test]
    public async Task Summary_PreservesCustomSchemaAndDoesNotRetrySqlContractErrors()
    {
        await ExecuteAsync(_testConnection, "IF SCHEMA_ID('audit_sequence_custom') IS NULL EXEC('CREATE SCHEMA audit_sequence_custom');");
        await ExecuteAsync(_testConnection, "CREATE TABLE audit_sequence_custom.AuditIntegrity(SequenceNumber bigint NOT NULL); INSERT INTO audit_sequence_custom.AuditIntegrity VALUES(41),(42);");
        try
        {
            var validProbe = new SequenceReadProbe();
            await using (var context = CreateProbeContext(validProbe, schema: "audit_sequence_custom"))
                Assert.That(await new AuditIntegrityRepository(context).IsSequenceContiguousAsync(), Is.True);
            var invalidProbe = new SequenceReadProbe();
            await using var missing = CreateProbeContext(invalidProbe, schema: "absent_sequence_schema");
            var error = await Assert.ThrowsAsync<SqlException>(async () =>
                await new AuditIntegrityRepository(missing).IsSequenceContiguousAsync());
            Assert.That(error!.Number, Is.EqualTo(208));
            Assert.That(invalidProbe.Connections, Has.Count.EqualTo(1));
        }
        finally
        {
            await ExecuteAsync(_testConnection, "DROP TABLE audit_sequence_custom.AuditIntegrity;");
        }
    }

    private AuditDbContext CreateProbeContext(SequenceReadProbe probe, bool enableRetries = false, string schema = "audit")
    {
        var builder = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer(_testConnection, sql => { if (enableRetries) sql.EnableRetryOnFailure(); })
            .AddInterceptors(probe)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, AuditModelCacheKeyFactory>();
        return new AuditDbContext(builder.Options, efOptions: Microsoft.Extensions.Options.Options.Create(
            new MillWorks.AuditCore.EntityFramework.Options.EntityFrameworkOptions { Schema = schema }));
    }

    private async Task PrepareConcurrencyAsync(bool rcsi)
    {
        await SetRcsiAsync(rcsi);
        await ExecuteAsync(_testConnection, "TRUNCATE TABLE audit.AuditIntegrity; INSERT INTO audit.AuditIntegrity VALUES (1),(2),(3),(4);");
        TestContext.Out.WriteLine($"Fixture={_database}; RCSI={rcsi}; four committed starting rows; no application database mutation.");
    }

    private static async Task MutateAsync(SqlConnection connection, SqlTransaction transaction, string mutation)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 3;
        command.CommandText = mutation switch
        {
            "append" => "INSERT INTO audit.AuditIntegrity VALUES (5);",
            "volumeAppend" => "INSERT INTO audit.AuditIntegrity VALUES (217720);",
            "prefix" => "DELETE FROM audit.AuditIntegrity WHERE SequenceNumber <= 2;",
            "interior" => "DELETE FROM audit.AuditIntegrity WHERE SequenceNumber = 2;",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        await command.ExecuteNonQueryAsync();
    }

    private async Task WaitForBlockingAsync(int blocked, int blocker, int reader)
    {
        await using var monitor = new SqlConnection(_testConnection);
        await monitor.OpenAsync();
        var timer = Stopwatch.StartNew();
        do
        {
            await using var command = monitor.CreateCommand();
            command.CommandTimeout = 1;
            command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests r JOIN sys.dm_exec_sessions s ON s.session_id=@reader WHERE r.session_id=@blocked AND r.blocking_session_id=@blocker AND r.wait_type LIKE 'LCK_M_%' AND s.transaction_isolation_level=4;";
            command.Parameters.AddWithValue("@reader", reader);
            command.Parameters.AddWithValue("@blocked", blocked);
            command.Parameters.AddWithValue("@blocker", blocker);
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) == 1)
            {
                TestContext.Out.WriteLine("SQL DMVs confirm lock overlap and reader isolation level 4 (SERIALIZABLE).");
                return;
            }
            await Task.Delay(10);
        } while (timer.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Fail("SQL did not expose the expected lock wait; concurrency was not established.");
    }

    private sealed class SequenceReadProbe : DbCommandInterceptor
    {
        public TaskCompletionSource Executing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReaderReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<SqlConnection> Connections { get; } = [];
        public List<Guid> ClientConnectionIds { get; } = [];
        public List<Guid> ContextIds { get; } = [];
        public List<IsolationLevel> Isolations { get; } = [];
        public List<int> Timeouts { get; } = [];
        public List<bool> AmbientPresent { get; } = [];
        public Func<CancellationToken, Task>? BeforeReader { get; set; }
        public Func<CancellationToken, Task>? AfterReader { get; init; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT_BIG", StringComparison.Ordinal))
            {
                Connections.Add((SqlConnection)command.Connection!);
                ClientConnectionIds.Add(((SqlConnection)command.Connection!).ClientConnectionId);
                ContextIds.Add(eventData.Context!.ContextId.InstanceId);
                Isolations.Add(command.Transaction!.IsolationLevel);
                Timeouts.Add(command.CommandTimeout);
                AmbientPresent.Add(System.Transactions.Transaction.Current is not null);
                Executing.TrySetResult();
                if (BeforeReader is not null) await BeforeReader(cancellationToken);
            }
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("COUNT_BIG", StringComparison.Ordinal))
            {
                ReaderReturned.TrySetResult();
                if (AfterReader is not null) await AfterReader(cancellationToken);
            }
            return result;
        }

    }
}
