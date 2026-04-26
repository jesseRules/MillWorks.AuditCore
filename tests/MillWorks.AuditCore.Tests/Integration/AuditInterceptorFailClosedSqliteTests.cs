using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Services.Diagnostics;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Phase 4 acceptance: end-to-end proofs of the AuditSaveChangesInterceptor fail-closed
/// contract against a real SQLite context. A ThrowingLogger trips the interceptor's
/// ProcessAuditableEntries catch on LogDebug; the new catch-path consults the policy,
/// increments the diagnostic counter, and either rethrows AuditIntegrityException
/// (fail-closed) or swallows (permissive).
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class AuditInterceptorFailClosedSqliteTests
{
    private SqliteConnection _connection = null!;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var setupContext = new FailClosedTestDbContext(BuildOptions(interceptor: null));
        setupContext.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _connection?.Dispose();
    }

    [Test]
    public async Task Permissive_AuditFailure_SwallowsAndBusinessSaveSucceeds()
    {
        var diagnostics = new AuditDiagnostics();
        var interceptor = new AuditSaveChangesInterceptor(
            logger: new ThrowingLogger<AuditSaveChangesInterceptor>(),
            diagnostics: diagnostics,
            failureMode: AuditFailureMode.Permissive,
            failurePolicy: new RegulatedEntityFailurePolicy());

        await using (var context = new FailClosedTestDbContext(BuildOptions(interceptor)))
        {
            context.FerpaEntities.Add(new FerpaTestEntity { Name = "alice" });
            await context.SaveChangesAsync();
        }

        Assert.That(diagnostics.InterceptorAuditFailureCount, Is.EqualTo(1));
        await using var verify = new FailClosedTestDbContext(BuildOptions(interceptor: null));
        Assert.That(await verify.FerpaEntities.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public void FailClosedForRegulated_RegulatedEntity_ThrowsAndRollsBack()
    {
        var diagnostics = new AuditDiagnostics();
        var interceptor = new AuditSaveChangesInterceptor(
            logger: new ThrowingLogger<AuditSaveChangesInterceptor>(),
            diagnostics: diagnostics,
            failureMode: AuditFailureMode.FailClosedForRegulated,
            failurePolicy: new RegulatedEntityFailurePolicy());

        AuditIntegrityException? thrown = null;
        using (var context = new FailClosedTestDbContext(BuildOptions(interceptor)))
        {
            context.FerpaEntities.Add(new FerpaTestEntity { Name = "bob" });
            thrown = Assert.ThrowsAsync<AuditIntegrityException>(async () =>
                await context.SaveChangesAsync());
        }

        Assert.That(thrown, Is.Not.Null);
        Assert.That(thrown!.EntityName, Is.EqualTo(nameof(FerpaTestEntity)));
        Assert.That(thrown.Action, Is.EqualTo(nameof(AuditAction.Created)));
        Assert.That(thrown.FailureReason, Does.Contain("build audit log records"));
        Assert.That(thrown.InnerException, Is.InstanceOf<InvalidOperationException>());

        Assert.That(diagnostics.InterceptorAuditFailureCount, Is.EqualTo(1));

        using var verify = new FailClosedTestDbContext(BuildOptions(interceptor: null));
        Assert.That(verify.FerpaEntities.Count(), Is.EqualTo(0));
    }

    [Test]
    public async Task FailClosedForRegulated_NonRegulatedEntity_SwallowsAndBusinessSaveSucceeds()
    {
        var diagnostics = new AuditDiagnostics();
        var interceptor = new AuditSaveChangesInterceptor(
            logger: new ThrowingLogger<AuditSaveChangesInterceptor>(),
            diagnostics: diagnostics,
            failureMode: AuditFailureMode.FailClosedForRegulated,
            failurePolicy: new RegulatedEntityFailurePolicy());

        await using (var context = new FailClosedTestDbContext(BuildOptions(interceptor)))
        {
            context.PlainEntities.Add(new PlainTestEntity { Name = "carol" });
            await context.SaveChangesAsync();
        }

        Assert.That(diagnostics.InterceptorAuditFailureCount, Is.EqualTo(1));
        await using var verify = new FailClosedTestDbContext(BuildOptions(interceptor: null));
        Assert.That(await verify.PlainEntities.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public void FailClosedAlways_PlainEntity_ThrowsAndRollsBack()
    {
        var diagnostics = new AuditDiagnostics();
        var interceptor = new AuditSaveChangesInterceptor(
            logger: new ThrowingLogger<AuditSaveChangesInterceptor>(),
            diagnostics: diagnostics,
            failureMode: AuditFailureMode.FailClosedAlways,
            failurePolicy: new RegulatedEntityFailurePolicy());

        AuditIntegrityException? thrown = null;
        using (var context = new FailClosedTestDbContext(BuildOptions(interceptor)))
        {
            context.PlainEntities.Add(new PlainTestEntity { Name = "dave" });
            thrown = Assert.ThrowsAsync<AuditIntegrityException>(async () =>
                await context.SaveChangesAsync());
        }

        Assert.That(thrown, Is.Not.Null);
        Assert.That(thrown!.EntityName, Is.EqualTo(nameof(PlainTestEntity)));
        Assert.That(thrown.Action, Is.EqualTo(nameof(AuditAction.Created)));

        Assert.That(diagnostics.InterceptorAuditFailureCount, Is.EqualTo(1));

        using var verify = new FailClosedTestDbContext(BuildOptions(interceptor: null));
        Assert.That(verify.PlainEntities.Count(), Is.EqualTo(0));
    }

    private DbContextOptions<FailClosedTestDbContext> BuildOptions(AuditSaveChangesInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<FailClosedTestDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        return builder.Options;
    }

    // ── Test fixture types ───────────────────────────────────────────────────

    private sealed class FailClosedTestDbContext : AuditDbContext
    {
        public FailClosedTestDbContext(DbContextOptions<FailClosedTestDbContext> options)
            : base(options)
        {
        }

        public DbSet<PlainTestEntity> PlainEntities { get; set; } = null!;
        public DbSet<FerpaTestEntity> FerpaEntities { get; set; } = null!;
    }

    private class PlainTestEntity
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [FERPA]
    private class FerpaTestEntity
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
