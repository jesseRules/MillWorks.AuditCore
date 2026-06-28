using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Tests for <see cref="AppendOnlyInterceptor"/>: marked (<see cref="IAppendOnlyEntity"/>) entities
/// are insert-only through the change tracker; unmarked entities are unaffected; and the
/// ExecuteDelete/ExecuteUpdate change-tracker bypass passes the guard untouched.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AppendOnlyInterceptorTests
{
    private static AppendOnlyTestDbContext NewInMemoryContext()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions<AppendOnlyTestDbContext>(
            configure: static builder => builder.AddInterceptors(new AppendOnlyInterceptor()));
        return new AppendOnlyTestDbContext(options);
    }

    [Test]
    public void Insert_AppendOnly_Entity_Succeeds()
    {
        using var context = NewInMemoryContext();
        context.Marked.Add(new MarkedEntity { Value = "initial" });

        Assert.DoesNotThrow(() => context.SaveChanges());
        Assert.That(context.Marked.Count(), Is.EqualTo(1));
    }

    [Test]
    public void Modify_AppendOnly_Entity_Throws()
    {
        using var context = NewInMemoryContext();
        var entity = new MarkedEntity { Value = "initial" };
        context.Marked.Add(entity);
        context.SaveChanges();

        entity.Value = "mutated";

        var ex = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        Assert.That(ex!.Message, Does.Contain(nameof(MarkedEntity)));
    }

    [Test]
    public void Delete_AppendOnly_Entity_Throws()
    {
        using var context = NewInMemoryContext();
        var entity = new MarkedEntity { Value = "initial" };
        context.Marked.Add(entity);
        context.SaveChanges();

        context.Marked.Remove(entity);

        Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
    }

    [Test]
    public void Modify_NonMarked_Entity_Succeeds()
    {
        using var context = NewInMemoryContext();
        var entity = new UnmarkedEntity { Value = "initial" };
        context.Unmarked.Add(entity);
        context.SaveChanges();

        entity.Value = "mutated";

        Assert.DoesNotThrow(() => context.SaveChanges());
        Assert.That(context.Unmarked.Single().Value, Is.EqualTo("mutated"));
    }

    [Test]
    public void ExecuteDelete_AppendOnly_Entity_Succeeds()
    {
        // In-memory SQLite requires the connection to stay open for the database to persist.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppendOnlyTestDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .AddInterceptors(new AppendOnlyInterceptor())
            .Options;

        using (var seed = new AppendOnlyTestDbContext(options))
        {
            seed.Database.EnsureCreated();
            seed.Marked.Add(new MarkedEntity { Value = "initial" });
            seed.SaveChanges();
        }

        using var context = new AppendOnlyTestDbContext(options);

        // ExecuteDelete bypasses the change tracker, so the interceptor never sees the deletion.
        int deleted = 0;
        Assert.DoesNotThrow(() => deleted = context.Marked.ExecuteDelete());
        Assert.That(deleted, Is.EqualTo(1));
        Assert.That(context.Marked.Count(), Is.EqualTo(0));
    }

    private sealed class AppendOnlyTestDbContext(DbContextOptions<AppendOnlyTestDbContext> options)
        : DbContext(options)
    {
        public DbSet<MarkedEntity> Marked { get; set; } = null!;
        public DbSet<UnmarkedEntity> Unmarked { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MarkedEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<UnmarkedEntity>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class MarkedEntity : IAppendOnlyEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Value { get; set; } = string.Empty;
    }

    private sealed class UnmarkedEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Value { get; set; } = string.Empty;
    }
}
