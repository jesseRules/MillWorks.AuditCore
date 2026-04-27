using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Constants;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Tests for FERPA-aware behavior in AuditSaveChangesInterceptor.
/// Validates that [FERPA]-decorated entities get enhanced audit logging
/// while non-FERPA entities remain unaffected.
/// </summary>
[TestFixture]
public class FerpaInterceptorTests
{
    private Mock<ILogger<AuditSaveChangesInterceptor>> _mockLogger = null!;
    private AuditSaveChangesInterceptor _interceptor = null!;
    private DbContextOptions<FerpaTestDbContext> _dbOptions = null!;
    private FerpaTestDbContext _dbContext = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        var dbName = $"FerpaTestDb_{Guid.NewGuid()}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAuditLogger>());
        services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
                .ConfigureWarnings(static w =>
                {
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning);
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning);
                }));
        services.AddScoped<IAuditEntityWriter, AuditDbContextEntityWriter>();
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddScoped<IAuditSink, ImmediateSink>();

        _provider = services.BuildServiceProvider();
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        _mockLogger = new Mock<ILogger<AuditSaveChangesInterceptor>>();
        _interceptor = new AuditSaveChangesInterceptor(
            _mockLogger.Object,
            scopeFactory: scopeFactory);

        _dbOptions = TestDbContextFactory.CreateInMemoryOptions<FerpaTestDbContext>(
            dbName: dbName,
            configure: builder => builder.AddInterceptors(_interceptor));

        _dbContext = new FerpaTestDbContext(_dbOptions);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
        _provider.Dispose();
    }

    // ── FERPA Entity: Added ──

    [Test]
    public async Task AddedFerpaEntity_HasFerpaEventTypeInAdditionalData()
    {
        var entity = new FerpaStudentEntity { Name = "Alice", StudentId = "S001" };
        _dbContext.FerpaStudents.Add(entity);

        await _dbContext.SaveChangesAsync();

        var auditLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaStudentEntity" && l.Action == AuditAction.Created)
            .FirstAsync();

        var expectedEventType = FerpaEventTypes.EventTypeBuilder.Build("FerpaStudentEntity", "Added");
        Assert.That(auditLog.AdditionalData, Does.Contain(expectedEventType));
    }

    [Test]
    public async Task AddedFerpaEntity_HasConsentRequiredInAdditionalData()
    {
        var entity = new FerpaStudentEntity { Name = "Bob", StudentId = "S002" };
        _dbContext.FerpaStudents.Add(entity);

        await _dbContext.SaveChangesAsync();

        var auditLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaStudentEntity" && l.Action == AuditAction.Created)
            .FirstAsync();

        // FerpaStudentEntity has RequiresConsent = true
        Assert.That(auditLog.AdditionalData, Does.Contain("_ConsentRequired"));
        Assert.That(auditLog.AdditionalData, Does.Contain("true"));
    }

    [Test]
    public async Task AddedFerpaEntity_HasRecordTypeInAdditionalData()
    {
        var entity = new FerpaStudentEntity { Name = "Charlie", StudentId = "S003" };
        _dbContext.FerpaStudents.Add(entity);

        await _dbContext.SaveChangesAsync();

        var auditLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaStudentEntity" && l.Action == AuditAction.Created)
            .FirstAsync();

        Assert.That(auditLog.AdditionalData, Does.Contain("_RecordType"));
        Assert.That(auditLog.AdditionalData, Does.Contain("StudentRecord"));
    }

    [Test]
    public async Task AddedFerpaEntity_DescriptionContainsFerpaTag()
    {
        var entity = new FerpaStudentEntity { Name = "Diana", StudentId = "S004" };
        _dbContext.FerpaStudents.Add(entity);

        await _dbContext.SaveChangesAsync();

        var auditLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaStudentEntity" && l.Action == AuditAction.Created)
            .FirstAsync();

        Assert.That(auditLog.Description, Does.Contain("[FERPA]"));
    }

    // ── FERPA Entity: Modified ──

    [Test]
    public async Task ModifiedFerpaEntity_DescriptionContainsFerpaTag()
    {
        var entity = new FerpaStudentEntity { Name = "Original", StudentId = "S005" };
        _dbContext.FerpaStudents.Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var tracked = await _dbContext.FerpaStudents.FindAsync(entity.Id);
        tracked!.Name = "Modified";

        await _dbContext.SaveChangesAsync();

        var updateLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Updated && l.PropertyName == "Name")
            .FirstAsync();

        Assert.That(updateLog.Description, Does.Contain("[FERPA]"));
    }

    [Test]
    public async Task ModifiedFerpaEntity_HasFerpaMetadataInAdditionalData()
    {
        var entity = new FerpaStudentEntity { Name = "Original", StudentId = "S006" };
        _dbContext.FerpaStudents.Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var tracked = await _dbContext.FerpaStudents.FindAsync(entity.Id);
        tracked!.Name = "Modified";

        await _dbContext.SaveChangesAsync();

        var updateLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Updated && l.PropertyName == "Name")
            .FirstAsync();

        Assert.That(updateLog.AdditionalData, Does.Contain("_FerpaEventType"));
        Assert.That(updateLog.AdditionalData, Does.Contain("_ConsentRequired"));
    }

    // ── FERPA Entity: Deleted ──

    [Test]
    public async Task DeletedFerpaEntity_DescriptionContainsFerpaTag()
    {
        var entity = new FerpaStudentEntity { Name = "ToDelete", StudentId = "S007" };
        _dbContext.FerpaStudents.Add(entity);
        await _dbContext.SaveChangesAsync();

        _dbContext.FerpaStudents.Remove(entity);
        await _dbContext.SaveChangesAsync();

        var deleteLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Deleted)
            .FirstAsync();

        Assert.That(deleteLog.Description, Does.Contain("[FERPA]"));
        Assert.That(deleteLog.AdditionalData, Does.Contain("_FerpaEventType"));
    }

    // ── Non-FERPA Entity: Unaffected ──

    [Test]
    public async Task AddedNonFerpaEntity_DoesNotHaveFerpaMetadata()
    {
        var entity = new RegularTestEntity { Name = "Regular" };
        _dbContext.RegularEntities.Add(entity);

        await _dbContext.SaveChangesAsync();

        var auditLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "RegularTestEntity" && l.Action == AuditAction.Created)
            .FirstAsync();

        Assert.That(auditLog.Description, Does.Not.Contain("[FERPA]"));
        Assert.That(auditLog.AdditionalData, Does.Not.Contain("_FerpaEventType"));
        Assert.That(auditLog.AdditionalData, Does.Not.Contain("_ConsentRequired"));
    }

    [Test]
    public async Task ModifiedNonFerpaEntity_DoesNotHaveFerpaMetadata()
    {
        var entity = new RegularTestEntity { Name = "Original" };
        _dbContext.RegularEntities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var tracked = await _dbContext.RegularEntities.FindAsync(entity.Id);
        tracked!.Name = "Modified";

        await _dbContext.SaveChangesAsync();

        var updateLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Updated && l.PropertyName == "Name")
            .FirstAsync();

        Assert.That(updateLog.Description, Does.Not.Contain("[FERPA]"));
        Assert.That(updateLog.AdditionalData, Is.Null);
    }

    // ── FERPA Entity with LogAllAccess = false ──

    [Test]
    public async Task FerpaEntity_LogAllAccessFalse_StillAudited()
    {
        // LogAllAccess = false means don't force logging — but since the entity
        // is not marked [NoAudit], normal audit behavior still applies
        var entity = new FerpaNoForceLogEntity { Name = "NoForce" };
        _dbContext.FerpaNoForceLogEntities.Add(entity);

        await _dbContext.SaveChangesAsync();

        var auditLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaNoForceLogEntity" && l.Action == AuditAction.Created)
            .FirstOrDefaultAsync();

        // Still audited (not [NoAudit]), and still has FERPA metadata
        Assert.That(auditLog, Is.Not.Null);
        Assert.That(auditLog!.Description, Does.Contain("[FERPA]"));
    }

    // ── FERPA Entity with RequiresConsent = false ──

    [Test]
    public async Task FerpaEntity_RequiresConsentFalse_ConsentRequiredIsFalse()
    {
        var entity = new FerpaNoConsentEntity { Name = "NoConsent" };
        _dbContext.FerpaNoConsentEntities.Add(entity);

        await _dbContext.SaveChangesAsync();

        var auditLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "FerpaNoConsentEntity" && l.Action == AuditAction.Created)
            .FirstAsync();

        Assert.That(auditLog.AdditionalData, Does.Contain("_ConsentRequired"));
        Assert.That(auditLog.AdditionalData, Does.Contain("false"));
    }

    // ── FerpaEventTypeBuilder ──

    [Test]
    public void EventTypeBuilder_BuildsCorrectFormat()
    {
        var result = FerpaEventTypes.EventTypeBuilder.Build("Student", "Updated");
        Assert.That(result, Is.EqualTo("FERPA.Student.Updated"));
    }

    [Test]
    public void EventTypeBuilder_UsesPrefix()
    {
        var result = FerpaEventTypes.EventTypeBuilder.Build("Enrollment", "Created");
        Assert.That(result, Does.StartWith(FerpaEventTypes.FerpaPrefix));
    }

    // ── Test DbContext and Entities ──

    private class FerpaTestDbContext(DbContextOptions<FerpaTestDbContext> options)
        : DbContext(options), IAuditBypassable
    {
        public DbSet<FerpaStudentEntity> FerpaStudents { get; set; } = null!;
        public DbSet<RegularTestEntity> RegularEntities { get; set; } = null!;
        public DbSet<FerpaNoForceLogEntity> FerpaNoForceLogEntities { get; set; } = null!;
        public DbSet<FerpaNoConsentEntity> FerpaNoConsentEntities { get; set; } = null!;
        public DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;
        public bool BypassAuditInterceptor { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<FerpaStudentEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<RegularTestEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<FerpaNoForceLogEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<FerpaNoConsentEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<AuditLogEntity>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    [FERPA(RecordType = "StudentRecord", RequiresConsent = true, LogAllAccess = true)]
    private class FerpaStudentEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string StudentId { get; set; } = string.Empty;
    }

    private class RegularTestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }

    [FERPA(LogAllAccess = false, RequiresConsent = true)]
    private class FerpaNoForceLogEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }

    [FERPA(RequiresConsent = false)]
    private class FerpaNoConsentEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }
}
