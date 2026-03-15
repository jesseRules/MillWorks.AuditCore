using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Services.Compliance;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Tests for FERPA consent enforcement in AuditSaveChangesInterceptor.
/// Validates the 6-cell behavior matrix (3 modes x 2 consent statuses)
/// plus edge cases: non-FERPA entities, RequiresConsent=false, exception propagation.
/// </summary>
[TestFixture]
public class FerpaEnforcementTests
{
    private Mock<ILogger<AuditSaveChangesInterceptor>> _mockLogger = null!;
    private IMemoryCache _cache = null!;
    private ConsentVerificationService _consentService = null!;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<AuditSaveChangesInterceptor>>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _consentService = new ConsentVerificationService(_cache);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }

    // ── Helper: create interceptor + context for a given enforcement mode ──

    private (AuditSaveChangesInterceptor interceptor, EnforcementTestDbContext context) CreateContext(
        ComplianceEnforcementMode mode,
        string? userId = "test-user",
        IConsentVerificationService? consentServiceOverride = null)
    {
        var interceptor = new AuditSaveChangesInterceptor(
            _mockLogger.Object,
            mode,
            consentServiceOverride ?? _consentService);

        var options = new DbContextOptionsBuilder<EnforcementTestDbContext>()
            .UseInMemoryDatabase($"EnforcementTest_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .AddInterceptors(interceptor)
            .Options;

        var context = new EnforcementTestDbContext(options) { CurrentUserId = userId };
        return (interceptor, context);
    }

    // ═══════════════════════════════════════════════════════════════
    // Enforce Mode
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Enforce_ConsentGranted_AllowsSave()
    {
        // Arrange
        await _consentService.RecordConsentAsync("test-user", "FerpaConsentEntity", null, DateTimeOffset.MaxValue);
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Enforce);

        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "Alice" });

        // Act & Assert — no exception
        await ctx.SaveChangesAsync();

        var auditLog = await ctx.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaConsentEntity")
            .FirstOrDefaultAsync();
        Assert.That(auditLog, Is.Not.Null);

        ctx.Dispose();
    }

    [Test]
    public void Enforce_ConsentNotFound_ThrowsComplianceViolationException()
    {
        // Arrange — no consent recorded
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Enforce);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "Bob" });

        // Act & Assert
        var ex = Assert.ThrowsAsync<ComplianceViolationException>(() => ctx.SaveChangesAsync());
        Assert.That(ex!.Standard, Is.EqualTo("FERPA"));
        Assert.That(ex.EntityType, Is.EqualTo("FerpaConsentEntity"));
        Assert.That(ex.UserId, Is.EqualTo("test-user"));
        Assert.That(ex.RegulationReference, Is.EqualTo("34 CFR §99.30"));

        ctx.Dispose();
    }

    [Test]
    public void Enforce_ConsentServiceThrows_ThrowsComplianceViolationException()
    {
        // Arrange — consent service that blows up
        var failingService = new Mock<IConsentVerificationService>();
        failingService.Setup(s => s.HasActiveConsent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Throws(new InvalidOperationException("Cache corrupted"));

        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Enforce, consentServiceOverride: failingService.Object);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "Charlie" });

        // Act & Assert — fail-closed: service error in Enforce mode blocks
        var ex = Assert.ThrowsAsync<ComplianceViolationException>(() => ctx.SaveChangesAsync());
        Assert.That(ex!.Standard, Is.EqualTo("FERPA"));
        Assert.That(ex.InnerException, Is.TypeOf<InvalidOperationException>());

        ctx.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // AuditOnly Mode
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task AuditOnly_ConsentGranted_AllowsSave()
    {
        // Arrange
        await _consentService.RecordConsentAsync("test-user", "FerpaConsentEntity", null, DateTimeOffset.MaxValue);
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.AuditOnly);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "Diana" });

        // Act & Assert — no exception
        await ctx.SaveChangesAsync();

        ctx.Dispose();
    }

    [Test]
    public async Task AuditOnly_ConsentNotFound_AllowsSaveAndLogs()
    {
        // Arrange — no consent recorded
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.AuditOnly);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "Eve" });

        // Act — should NOT throw
        await ctx.SaveChangesAsync();

        // Assert — audit log was still created (save succeeded)
        var auditLog = await ctx.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaConsentEntity")
            .FirstOrDefaultAsync();
        Assert.That(auditLog, Is.Not.Null);

        ctx.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // Advisory Mode
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Advisory_ConsentGranted_AllowsSave()
    {
        // Arrange
        await _consentService.RecordConsentAsync("test-user", "FerpaConsentEntity", null, DateTimeOffset.MaxValue);
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Advisory);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "Frank" });

        // Act & Assert — no exception
        await ctx.SaveChangesAsync();

        ctx.Dispose();
    }

    [Test]
    public async Task Advisory_ConsentNotFound_AllowsSave()
    {
        // Arrange — no consent recorded
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Advisory);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "Grace" });

        // Act — should NOT throw
        await ctx.SaveChangesAsync();

        // Assert — audit log was still created
        var auditLog = await ctx.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaConsentEntity")
            .FirstOrDefaultAsync();
        Assert.That(auditLog, Is.Not.Null);

        ctx.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════
    // Edge Cases
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task NonFerpaEntity_Unaffected_InEnforceMode()
    {
        // Arrange — Enforce mode, but entity is not FERPA-decorated
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Enforce);
        ctx.RegularEntities.Add(new EnforcementRegularEntity { Name = "Regular" });

        // Act & Assert — no exception, no consent check
        await ctx.SaveChangesAsync();

        var auditLog = await ctx.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "EnforcementRegularEntity")
            .FirstOrDefaultAsync();
        Assert.That(auditLog, Is.Not.Null);

        ctx.Dispose();
    }

    [Test]
    public async Task FerpaEntity_RequiresConsentFalse_SkipsConsentCheck_InEnforceMode()
    {
        // Arrange — FERPA entity with RequiresConsent = false, no consent recorded
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Enforce);
        ctx.FerpaNoConsentEntities.Add(new FerpaNoConsentEnforcementEntity { Name = "NoConsent" });

        // Act & Assert — no exception even in Enforce mode
        await ctx.SaveChangesAsync();

        ctx.Dispose();
    }

    [Test]
    public void ComplianceViolationException_PropagatesThroughSaveChanges()
    {
        // This validates Architectural Constraint #6:
        // ComplianceViolationException is NOT swallowed by the interceptor's
        // generic catch block in ProcessAuditableEntries.
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Enforce);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "Propagation" });

        // Assert — the exception escapes SaveChangesAsync, proving it's not caught
        Assert.ThrowsAsync<ComplianceViolationException>(() => ctx.SaveChangesAsync());

        ctx.Dispose();
    }

    [Test]
    public async Task NullUserId_TreatedAsNotFound_InAdvisoryMode()
    {
        // Arrange — no user ID set on the context
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Advisory, userId: null);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "NoUser" });

        // Act — Advisory mode: allows even with null user ID
        await ctx.SaveChangesAsync();

        ctx.Dispose();
    }

    [Test]
    public void NullUserId_InEnforceMode_ThrowsComplianceViolationException()
    {
        // Arrange — no user ID, Enforce mode
        var (_, ctx) = CreateContext(ComplianceEnforcementMode.Enforce, userId: null);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "NoUser" });

        // Act & Assert — null user ID means consent can't be verified, fail-closed
        var ex = Assert.ThrowsAsync<ComplianceViolationException>(() => ctx.SaveChangesAsync());
        Assert.That(ex!.UserId, Is.Null);

        ctx.Dispose();
    }

    [Test]
    public async Task NoComplianceConfigured_InterceptorSkipsEnforcement()
    {
        // Arrange — interceptor with no enforcement mode (compliance not configured)
        var interceptor = new AuditSaveChangesInterceptor(_mockLogger.Object);

        var options = new DbContextOptionsBuilder<EnforcementTestDbContext>()
            .UseInMemoryDatabase($"NoCompliance_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .AddInterceptors(interceptor)
            .Options;

        using var ctx = new EnforcementTestDbContext(options);
        ctx.FerpaConsentEntities.Add(new FerpaConsentEntity { Name = "NoCompliance" });

        // Act — no exception, enforcement not active
        await ctx.SaveChangesAsync();

        var auditLog = await ctx.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaConsentEntity")
            .FirstOrDefaultAsync();
        Assert.That(auditLog, Is.Not.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    // Test entities and DbContext
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Test DbContext that inherits from AuditApplicationDbContext to provide
    /// CurrentUserId for consent enforcement. Uses InMemory provider.
    /// </summary>
    internal class EnforcementTestDbContext : AuditApplicationDbContext
    {
        public DbSet<FerpaConsentEntity> FerpaConsentEntities { get; set; } = null!;
        public DbSet<EnforcementRegularEntity> RegularEntities { get; set; } = null!;
        public DbSet<FerpaNoConsentEnforcementEntity> FerpaNoConsentEntities { get; set; } = null!;

        public EnforcementTestDbContext(DbContextOptions<EnforcementTestDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Configure test entities
            modelBuilder.Entity<FerpaConsentEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<EnforcementRegularEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<FerpaNoConsentEnforcementEntity>().HasKey(static e => e.Id);

            // Let the base class configure AuditLogEntity and other audit entities
            base.OnModelCreating(modelBuilder);
        }
    }

    [FERPA(RecordType = "StudentRecord", RequiresConsent = true)]
    internal class FerpaConsentEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }

    internal class EnforcementRegularEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }

    [FERPA(RequiresConsent = false)]
    internal class FerpaNoConsentEnforcementEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }
}
