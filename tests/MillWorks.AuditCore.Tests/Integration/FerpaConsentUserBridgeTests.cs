using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Compliance;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// End-to-end coverage for the user-identity bridge in <see cref="AuditContextMiddleware"/>.
/// <para>
/// The FERPA consent check in <see cref="AuditSaveChangesInterceptor"/> reads
/// <see cref="AuditDbContext.CurrentUserId"/>. Before the bridge existed, the middleware
/// populated correlation/IP/user-agent onto the context but never the user id, so consent
/// always resolved to <c>NotFound</c> — blocking valid authenticated writes in Enforce mode.
/// These tests drive the real middleware → interceptor → consent path and prove that an
/// authenticated, consented user now passes enforcement, while an anonymous request (the
/// only variable changed) is still blocked.
/// </para>
/// </summary>
[TestFixture]
public class FerpaConsentUserBridgeTests
{
    private IMemoryCache _cache = null!;
    private ConsentVerificationService _consentService = null!;
    private readonly List<ServiceProvider> _providers = [];

    [SetUp]
    public void Setup()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _consentService = new ConsentVerificationService(_cache);
        _providers.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
        foreach (var p in _providers)
            p.Dispose();
        _providers.Clear();
    }

    [Test]
    public async Task EnforceMode_AuthenticatedConsentedUser_BridgedByMiddleware_AllowsRegulatedWrite()
    {
        // Consent is recorded against the AppUserId — the canonical business identifier
        // the middleware bridges onto the DbContext.
        var appUserId = Guid.NewGuid();
        await _consentService.RecordConsentAsync(
            appUserId.ToString(), nameof(StudentRecordEntity), null, DateTimeOffset.MaxValue);

        var ctx = CreateAuditedContext(ComplianceEnforcementMode.Enforce);
        var (httpContext, middleware) = CreateMiddlewarePipeline(ctx, AuthenticatedUser(appUserId));

        // Run the real middleware; it must bridge the authenticated principal onto the context.
        await middleware.InvokeAsync(httpContext, static _ => Task.CompletedTask);

        Assert.That(ctx.CurrentUserId, Is.EqualTo(appUserId.ToString()),
            "Middleware must bridge the authenticated user onto AuditDbContext.CurrentUserId");

        // Consent enforcement now sees the user, so the regulated write is allowed.
        ctx.StudentRecords.Add(new StudentRecordEntity { Name = "Alice" });
        Assert.DoesNotThrowAsync(() => ctx.SaveChangesAsync());

        // And the write was audited (proving the save fully committed through the pipeline).
        var auditLog = await ctx.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == nameof(StudentRecordEntity))
            .FirstOrDefaultAsync();
        Assert.That(auditLog, Is.Not.Null);
    }

    [Test]
    public void EnforceMode_AnonymousRequest_LeavesUserNull_BlocksRegulatedWrite()
    {
        // Control: identical harness, but no authenticated user. CurrentUserId stays null,
        // consent resolves NotFound, and Enforce mode blocks — proving the authenticated
        // identity bridged by the middleware is what distinguishes the allowed case above.
        var ctx = CreateAuditedContext(ComplianceEnforcementMode.Enforce);
        var (httpContext, middleware) = CreateMiddlewarePipeline(
            ctx, new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.DoesNotThrowAsync(() => middleware.InvokeAsync(httpContext, static _ => Task.CompletedTask));
        Assert.That(ctx.CurrentUserId, Is.Null);

        ctx.StudentRecords.Add(new StudentRecordEntity { Name = "Bob" });
        var ex = Assert.ThrowsAsync<ComplianceViolationException>(() => ctx.SaveChangesAsync());
        Assert.That(ex!.Standard, Is.EqualTo("FERPA"));
        Assert.That(ex.EntityType, Is.EqualTo(nameof(StudentRecordEntity)));
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private static ClaimsPrincipal AuthenticatedUser(Guid appUserId) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, $"aspnet-{appUserId}"),
                new Claim("AppUserId", appUserId.ToString())
            ],
            "TestAuthType"));

    /// <summary>
    /// Builds an audited <see cref="AuditedTestDbContext"/> wired with the consent-enforcing
    /// interceptor. The interceptor's scoped sink path shares the same in-memory database
    /// (keyed on <c>dbName</c>) so written audit rows are visible through the returned context.
    /// </summary>
    private AuditedTestDbContext CreateAuditedContext(ComplianceEnforcementMode mode)
    {
        var dbName = $"FerpaBridge_{Guid.NewGuid()}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAuditLogger>());
        services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
                .ConfigureWarnings(static w =>
                {
                    w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                    w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
                }));
        services.AddScoped<IAuditEntityBatchWriter, AuditEntityBatchWriter>();
        services.AddScoped<IAuditEventBatchWriter, AuditEventBatchWriter>();
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddScoped<IAuditSink, ImmediateSink>();

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var interceptor = new AuditSaveChangesInterceptor(
            Mock.Of<ILogger<AuditSaveChangesInterceptor>>(),
            mode,
            _consentService,
            scopeFactory: scopeFactory);

        var options = new DbContextOptionsBuilder<AuditedTestDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .AddInterceptors(interceptor)
            .Options;

        return new AuditedTestDbContext(options);
    }

    /// <summary>
    /// Wires the real <see cref="AuditContextMiddleware"/> with a request service provider
    /// that resolves the supplied context as <see cref="AuditDbContext"/>, so the middleware
    /// writes request-scoped state onto the same instance the test saves through.
    /// </summary>
    private (DefaultHttpContext httpContext, AuditContextMiddleware middleware) CreateMiddlewarePipeline(
        AuditedTestDbContext ctx, ClaimsPrincipal user)
    {
        var eventFactory = new Mock<IAuditEventFactory>();
        eventFactory
            .Setup(x => x.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()))
            .Returns((string eventType, object? _, string _) => new AuditEvent { EventType = eventType });

        var middleware = new AuditContextMiddleware(
            new AuditContext(),
            eventFactory.Object,
            Mock.Of<IRequestAuditDispatcher>(),
            Options.Create(new AuditMiddlewareOptions()),
            Mock.Of<ILogger<AuditContextMiddleware>>());

        var requestServices = new ServiceCollection()
            .AddScoped(_ => ctx)
            .AddScoped<AuditDbContext>(sp => sp.GetRequiredService<AuditedTestDbContext>())
            .BuildServiceProvider();
        _providers.Add(requestServices);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = requestServices,
            User = user
        };
        httpContext.Request.Path = "/api/students";
        httpContext.Request.Method = "POST";

        return (httpContext, middleware);
    }

    // ── Test entities / context ──────────────────────────────────────────────

    [FERPA(RecordType = "StudentRecord", RequiresConsent = true)]
    internal class StudentRecordEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }

    internal class AuditedTestDbContext : AuditDbContext
    {
        public DbSet<StudentRecordEntity> StudentRecords { get; set; } = null!;

        public AuditedTestDbContext(DbContextOptions<AuditedTestDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<StudentRecordEntity>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }
}
