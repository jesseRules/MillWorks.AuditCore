using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Regression for the transaction guard gap in <c>Repository&lt;T&gt;.BeginTransactionAsync</c>.
/// <para>
/// Two repositories constructed against the same <see cref="AuditDbContext"/>
/// instance each keep their own <c>_currentTransaction</c> field. When repository A opens
/// a transaction, repository B's field stays <see langword="null"/>. Before the fix,
/// B's <c>BeginTransactionAsync</c> only checked its own field and would happily call
/// <c>_context.Database.BeginTransactionAsync</c> — whose behaviour on a DbContext with
/// an active transaction is at best provider-specific and at worst silently wrong. The
/// fix consults <c>_context.Database.CurrentTransaction</c> alongside the instance field
/// so the call fails loudly with a clear diagnostic.
/// </para>
/// <para>
/// Uses an on-disk SQLite in-memory database so <c>Database.CurrentTransaction</c> reflects
/// real ADO.NET transaction state, unlike the InMemory provider.
/// </para>
/// </summary>
[TestFixture]
public sealed class RepositorySharedContextTransactionTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AuditDbContext> _options = null!;
    private AuditDbContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new AuditDbContext(_options);
        _context.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    [Test]
    public async Task BeginTransactionAsync_WhenAnotherRepositoryHasTransactionOnSharedContext_Throws()
    {
        var repoA = new AuditEventRepository(_context);
        var repoB = new AuditEventRepository(_context);

        var txA = await repoA.BeginTransactionAsync();
        try
        {
            var ex = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await repoB.BeginTransactionAsync());

            Assert.Multiple(() =>
            {
                Assert.That(ex!.Message, Does.Contain("transaction is already in progress"),
                    "Message should retain the canonical phrase for callers matching on it.");
                Assert.That(ex.Message, Does.Contain("DbContext"),
                    "Diagnostic should point at the shared DbContext so the misuse is obvious.");
                Assert.That(ex.Message, Does.Contain("join").IgnoreCase,
                    "Guidance should direct callers to CurrentTransaction rather than nesting.");
            });
        }
        finally
        {
            await txA.DisposeAsync();
        }
    }

    [Test]
    public async Task CurrentTransaction_AfterPeerRepositoryOpensTransaction_ObservesSharedTransaction()
    {
        var repoA = new AuditEventRepository(_context);
        var repoB = new AuditEventRepository(_context);

        var txA = await repoA.BeginTransactionAsync();
        try
        {
            Assert.That(repoB.CurrentTransaction, Is.Not.Null,
                "CurrentTransaction falls back to Database.CurrentTransaction so peers on the " +
                "shared context can join the outer transaction instead of opening a nested one.");
            Assert.That(repoB.CurrentTransaction, Is.SameAs(txA),
                "The fallback must surface the exact transaction repo A opened on the shared context.");
        }
        finally
        {
            await txA.DisposeAsync();
        }
    }
}
