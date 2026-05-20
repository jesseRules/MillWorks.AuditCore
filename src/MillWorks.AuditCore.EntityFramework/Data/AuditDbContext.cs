using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Extensions;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.EntityFramework.Data;

/// <summary>
/// Partial class for audit-related database configuration
/// </summary>
public class AuditDbContext : DbContext, IAuditBypassable, IAuditContextSource, IAuditProviderDispatchSource
{
    /// <summary>
    /// Thread-safe bypass flag using AsyncLocal for per-execution-context isolation
    /// </summary>
    private static readonly AsyncLocal<bool> _bypassAuditInterceptorStorage = new();

    /// <summary>
    /// Optional encryption service for field-level encryption via value converters
    /// </summary>
    private readonly IFieldEncryptionService? _encryptionService;

    /// <summary>
    /// Configured schema for audit tables. Defaults to "audit" when no options are injected
    /// (direct construction, design-time factory). Stored for use by <c>OnModelCreating</c>
    /// in a later step; not yet consumed.
    /// </summary>
    private readonly string _schema;

    /// <summary>
    /// Configured schema for audit tables. Exposed for <see cref="AuditModelCacheKeyFactory"/>
    /// so the compiled-model cache is keyed on schema alongside context type.
    /// </summary>
    internal string Schema => _schema;

    /// <inheritdoc />
    public bool BypassAuditInterceptor
    {
        get => _bypassAuditInterceptorStorage.Value;
        set => _bypassAuditInterceptorStorage.Value = value;
    }

    /// <summary>
    /// Constructor for dependency injection
    /// </summary>
    /// <param name="options">Database context options</param>
    /// <param name="encryptionService">
    /// Optional encryption service. When provided, properties marked with
    /// <see cref="Attributes.EncryptedFieldAttribute"/> are automatically encrypted/decrypted
    /// via EF Core value converters.
    /// </param>
    /// <param name="efOptions">
    /// Optional Entity Framework options. Supplies the configured schema name. When null,
    /// the schema defaults to "audit" for compatibility with design-time tooling and direct
    /// construction.
    /// </param>
    public AuditDbContext(
        DbContextOptions<AuditDbContext> options,
        IFieldEncryptionService? encryptionService = null,
        IOptions<EntityFrameworkOptions>? efOptions = null)
        : base(options)
    {
        _encryptionService = encryptionService;
        _schema = efOptions?.Value.Schema ?? "audit";
    }

    /// <summary>
    /// Protected constructor for derived contexts (standard EF Core inheritance pattern).
    /// </summary>
    protected AuditDbContext(
        DbContextOptions options,
        IFieldEncryptionService? encryptionService = null,
        IOptions<EntityFrameworkOptions>? efOptions = null)
        : base(options)
    {
        _encryptionService = encryptionService;
        _schema = efOptions?.Value.Schema ?? "audit";
    }

    /// <summary>
    /// Correlation ID for the current request scope. Set by middleware, read by the interceptor
    /// to stamp on AuditLogEntity records.
    /// </summary>
    public string? CurrentCorrelationId { get; set; }

    /// <summary>
    /// Client IP address for the current request scope. Set by middleware, read by the interceptor
    /// to stamp on AuditLogEntity records.
    /// </summary>
    public string? CurrentIpAddress { get; set; }

    /// <summary>
    /// User agent string for the current request scope. Set by middleware, read by the interceptor
    /// to stamp on AuditLogEntity records.
    /// </summary>
    public string? CurrentUserAgent { get; set; }

    /// <summary>
    /// User ID for the current request scope. Set by middleware, read by the interceptor
    /// for consent verification in FERPA enforcement mode.
    /// </summary>
    public string? CurrentUserId { get; set; }

    /// <summary>
    /// Scoped service provider for the current request. Used by the singleton interceptor
    /// to resolve scoped services (providers, loggers) via the DbContext instance.
    /// </summary>
    public IServiceProvider? ScopedServiceProvider { get; set; }

    /// <summary>
    /// Pending provider dispatches captured in SavingChanges, processed in SavedChanges.
    /// </summary>
    public IReadOnlyList<PendingProviderDispatch>? PendingProviderDispatches { get; set; }

    /// <summary>
    /// Re-entrancy guard — prevents infinite recursion if a provider triggers another save.
    /// </summary>
    public bool IsDispatchingProviders { get; set; }

    /// <summary>
    /// Audit events that track changes and actions within the application for security and compliance.
    /// </summary>
    public virtual DbSet<AuditEventEntity> AuditEvents { get; set; } = null!;

    /// <summary>
    /// Audit integrity entity that maintains the integrity of audit logs using cryptographic techniques.
    /// </summary>
    public virtual DbSet<AuditIntegrityEntity> AuditIntegrity { get; set; } = null!;

    /// <summary>
    /// Audit Logs table (legacy/alternative audit storage)
    /// </summary>
    public virtual DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;

    /// <summary>
    /// Archive records for storing archived audit collections in the database.
    /// </summary>
    public virtual DbSet<AuditArchiveRecordEntity> ArchiveRecords { get; set; } = null!;

    /// <summary>
    /// Security Events
    /// </summary>
    public virtual DbSet<AuditSecurityEventEntity> SecurityEvents { get; set; } = null!;

    /// <summary>
    /// Durable outbox for pending integrity record creation in batched mode
    /// </summary>
    public virtual DbSet<AuditIntegrityWorkItemEntity> IntegrityWorkItems { get; set; } = null!;

    /// <summary>
    /// Transactional outbox for audit envelope handoff. Written by
    /// <c>TransactionalOutboxSink</c> inside the consumer's transaction,
    /// drained by <c>AuditOutboxDrainer</c> to the audit DbContext.
    /// </summary>
    public virtual DbSet<AuditOutboxEntity> AuditOutbox { get; set; } = null!;

    /// <summary>
    /// Saves changes with automatic bypass detection for audit entities
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        PrepareConcurrencyTokens();

        // Detect if we're saving audit entities to prevent infinite loops
        var savingAuditEntities = ChangeTracker.Entries()
            .Any(static e => e.Entity is AuditEventEntity or AuditIntegrityEntity or AuditArchiveRecordEntity
                or AuditLogEntity or AuditSecurityEventEntity or AuditIntegrityWorkItemEntity
                or AuditOutboxEntity);

        if (!savingAuditEntities)
            return await base.SaveChangesAsync(cancellationToken);

        // Temporarily bypass the interceptor
        var previousBypassState = _bypassAuditInterceptorStorage.Value;
        try
        {
            _bypassAuditInterceptorStorage.Value = true;
            return await base.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _bypassAuditInterceptorStorage.Value = previousBypassState;
        }
    }

    /// <summary>
    /// Saves changes synchronously with automatic bypass detection
    /// </summary>
    public override int SaveChanges()
    {
        PrepareConcurrencyTokens();

        // Detect if we're saving audit entities
        var savingAuditEntities = ChangeTracker.Entries()
            .Any(static e => e.Entity is AuditEventEntity or AuditIntegrityEntity or AuditArchiveRecordEntity
                or AuditLogEntity or AuditSecurityEventEntity or AuditIntegrityWorkItemEntity
                or AuditOutboxEntity);

        if (!savingAuditEntities)
            return base.SaveChanges();

        var previousBypassState = _bypassAuditInterceptorStorage.Value;
        try
        {
            _bypassAuditInterceptorStorage.Value = true;
            return base.SaveChanges();
        }
        finally
        {
            _bypassAuditInterceptorStorage.Value = previousBypassState;
        }
    }

    /// <summary>
    /// Overrides the OnModelCreating method to configure the model.
    /// </summary>
    /// <param name="modelBuilder"></param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(_schema);

        ConfigureAudit(modelBuilder);
        ConfigureConcurrency(modelBuilder);

        if (_encryptionService is not null)
            modelBuilder.UseFieldEncryption(_encryptionService);
    }

    /// <summary>
    /// Configures optimistic concurrency for aggregate roots.
    /// Uses provider-native rowversion on SQL Server and application-managed tokens elsewhere.
    /// </summary>
    private void ConfigureConcurrency(ModelBuilder modelBuilder)
    {
        var isSqlServer = Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer";

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(static e => typeof(AuditAggregateRoot).IsAssignableFrom(e.ClrType)))
        {
            var propertyBuilder = modelBuilder.Entity(entityType.ClrType)
                .Property<byte[]>(nameof(AuditAggregateRoot.RowVersion))
                .IsRequired()
                .IsConcurrencyToken();

            if (isSqlServer)
            {
                propertyBuilder.IsRowVersion();
            }
        }
    }

    /// <summary>
    /// Configures audit-related entities
    /// </summary>
    /// <param name="modelBuilder">The model builder</param>
    private void ConfigureAudit(ModelBuilder modelBuilder)
    {
        var isInMemory = Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory";
        var isSqlite = Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";

        // AuditEventEntity configuration
        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            // IntegrityStatus enum stored as int
            entity.Property(static e => e.IntegrityStatus)
                .HasConversion<int>()
                .HasDefaultValue(IntegrityStatus.Pending)
                .HasSentinel((IntegrityStatus)0);

            if (isInMemory)
            {
                // InMemory: entity property initializers handle defaults (no HasDefaultValueSql support)
                entity.Property(static e => e.Environment)
                    .HasDefaultValue("Production");

                // In-memory doesn't support filtered indexes
                entity.HasIndex(static e => new { e.EventType, e.InsertedDate })
                    .HasDatabaseName("IX_AuditEvents_EventType_Date");
            }
            else if (isSqlite)
            {
                entity.Property(static e => e.InsertedDate)
                    .HasDefaultValueSql("datetime('now')");

                entity.Property(static e => e.Environment)
                    .HasDefaultValue("Production");

                entity.HasIndex(static e => new { e.EventType, e.InsertedDate })
                    .HasFilter("\"EventType\" IS NOT NULL")
                    .HasDatabaseName("IX_AuditEvents_EventType_Date_Filtered");
            }
            else
            {
                // SQL Server
                entity.Property(static e => e.InsertedDate)
                    .HasDefaultValueSql("GETUTCDATE()");

                entity.Property(static e => e.Environment)
                    .HasDefaultValue("Production");

                entity.HasIndex(static e => new { e.EventType, e.InsertedDate })
                    .HasFilter("[EventType] IS NOT NULL")
                    .HasDatabaseName("IX_AuditEvents_EventType_Date_Filtered");
            }
        });

        // AuditLogEntity configuration
        modelBuilder.Entity<AuditLogEntity>(entity =>
        {
            if (isInMemory)
            {
                // InMemory: entity property initializers handle defaults
            }
            else if (isSqlite)
            {
                entity.Property(static e => e.CreatedAt)
                    .HasDefaultValueSql("datetime('now')");

                entity.ToTable(static t => t.HasCheckConstraint("CK_AuditLogs_Action",
                    "\"Action\" >= 0 AND \"Action\" <= 10"));
            }
            else
            {
                // SQL Server
                entity.Property(static e => e.CreatedAt)
                    .HasDefaultValueSql("GETUTCDATE()");

                entity.ToTable(static t => t.HasCheckConstraint("CK_AuditLogs_Action",
                    "[Action] >= 0 AND [Action] <= 10"));
            }
        });

        // ArchiveRecordEntity configuration (merged from two separate blocks)
        modelBuilder.Entity<AuditArchiveRecordEntity>(entity =>
        {
            entity.HasKey(static e => e.Id);

            // Unique constraint on ArchiveId
            entity.HasIndex(static e => e.ArchiveId)
                .IsUnique()
                .HasDatabaseName("IX_Archive_Record_ArchiveId");

            // Indexes for performance
            entity.HasIndex(static e => e.CreatedAt)
                .HasDatabaseName("IX_Archive_Record_CreatedAt");

            entity.HasIndex(static e => e.Status)
                .HasDatabaseName("IX_Archive_Record_Status");

            entity.HasIndex(static e => new { e.DateRangeStart, e.DateRangeEnd })
                .HasDatabaseName("IX_Archive_Record_DateRange");

            entity.HasIndex(static e => e.ContainerName)
                .HasDatabaseName("IX_Archive_Record_ContainerName");

            entity.HasIndex(static e => e.LastVerifiedAt)
                .HasDatabaseName("IX_Archive_Record_LastVerifiedAt");

            entity.HasIndex(static e => e.CreatedByUserId)
                .HasDatabaseName("IX_Archive_Record_CreatedByUserId");

            // Configure enum conversion (int, not string)
            entity.Property(static e => e.Status)
                .HasConversion<int>()
                .HasDefaultValue(MillWorksArchiveStatus.InProgress);

            // Configure string lengths and types
            entity.Property(static e => e.ArchiveId)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(static e => e.BlobName)
                .HasMaxLength(255)
                .IsRequired();

            entity.Property(static e => e.ContainerName)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(static e => e.Hash)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(static e => e.ArchiveVersion)
                .HasMaxLength(20)
                .IsRequired()
                .HasDefaultValue("1.0");

            entity.Property(static e => e.CompressionType)
                .HasMaxLength(50)
                .IsRequired()
                .HasDefaultValue("gzip");

            entity.Property(static e => e.ErrorMessage)
                .HasMaxLength(1000);

            entity.Property(static e => e.CreatedByUserId)
                .HasMaxLength(450);

            // Provider-specific configuration
            if (isInMemory)
            {
                // InMemory: entity property initializers handle defaults
                entity.ToTable("ArchiveRecord");
            }
            else if (isSqlite)
            {
                entity.Property(static e => e.CreatedAt)
                    .HasDefaultValueSql("datetime('now')");

                entity.ToTable("ArchiveRecord", static t => t.HasCheckConstraint("CK_ArchiveRecord_DateRange",
                    "\"DateRangeEnd\" >= \"DateRangeStart\""));
            }
            else
            {
                // SQL Server
                entity.Property(static e => e.CreatedAt)
                    .HasDefaultValueSql("GETUTCDATE()");

                entity.ToTable("ArchiveRecord", static t => t.HasCheckConstraint("CK_ArchiveRecord_DateRange",
                    "[DateRangeEnd] >= [DateRangeStart]"));
            }
        });

        // AuditIntegrityEntity FK: Restrict delete to preserve tamper-evidence chain
        modelBuilder.Entity<AuditIntegrityEntity>(static entity =>
        {
            entity.HasOne(static e => e.AuditEvent)
                .WithOne(static e => e.AuditIntegrity)
                .HasForeignKey<AuditIntegrityEntity>(static e => e.EventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // AuditIntegrityWorkItemEntity configuration (durable outbox for batched integrity writes)
        modelBuilder.Entity<AuditIntegrityWorkItemEntity>(entity =>
        {
            entity.HasKey(static e => e.Id);

            // Restrict delete to preserve tamper-evidence chain
            entity.HasOne(static e => e.AuditEvent)
                .WithMany()
                .HasForeignKey(static e => e.EventId)
                .OnDelete(DeleteBehavior.Restrict);

            // Store enum as int
            entity.Property(static e => e.Status)
                .HasConversion<int>()
                .HasDefaultValue(IntegrityStatus.Pending)
                .HasSentinel((IntegrityStatus)0);

            if (isInMemory)
            {
                // InMemory: property initializers handle defaults
            }
            else if (isSqlite)
            {
                entity.Property(static e => e.CreatedAt)
                    .HasDefaultValueSql("datetime('now')");
            }
            else
            {
                // SQL Server
                entity.Property(static e => e.CreatedAt)
                    .HasDefaultValueSql("GETUTCDATE()");
            }
        });

        // AuditOutboxEntity configuration (transactional outbox for audit envelope handoff)
        modelBuilder.Entity<AuditOutboxEntity>(entity =>
        {
            entity.ToTable("AuditOutbox", _schema);
            entity.HasKey(static e => e.Id);

            entity.Property(static e => e.Status)
                .HasConversion<int>()
                .HasDefaultValue(AuditOutboxStatus.Pending)
                .HasSentinel((AuditOutboxStatus)0);

            entity.Property(static e => e.EnvelopeVersion)
                .HasDefaultValue(1);

            entity.HasIndex(static e => e.CreatedAt)
                .HasDatabaseName("IX_AuditOutbox_CreatedAt");

            entity.HasIndex(static e => e.IdempotencyKey)
                .IsUnique()
                .HasDatabaseName("UX_AuditOutbox_IdempotencyKey");

            entity.Property(static e => e.LeaseOwner)
                .HasMaxLength(100);

            entity.HasIndex(static e => new { e.Status, e.NextRetryAt, e.LeaseExpiresAt, e.CreatedAt })
                .HasDatabaseName("IX_AuditOutbox_Claimable");

            if (isInMemory)
            {
                // InMemory: property initializers handle defaults
            }
            else if (isSqlite)
            {
                entity.Property(static e => e.CreatedAt)
                    .HasDefaultValueSql("datetime('now')");
            }
            else
            {
                // SQL Server
                entity.Property(static e => e.CreatedAt)
                    .HasDefaultValueSql("GETUTCDATE()");
            }
        });

        // SQLite does not understand SQL Server column types from [Column(TypeName)] attributes.
        // Override them to SQLite-compatible types.
        if (isSqlite)
        {
            RemapSqlServerTypesForSqlite(modelBuilder);
        }
    }

    /// <summary>
    /// Remaps SQL Server-specific column types to SQLite-compatible types.
    /// Required because [Column(TypeName = "nvarchar(max)")] etc. are baked into entity attributes.
    /// </summary>
    private static void RemapSqlServerTypesForSqlite(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                // Convert DateTimeOffset properties to string for SQLite compatibility.
                // SQLite cannot ORDER BY or compare DateTimeOffset natively.
                if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetColumnType("TEXT");
                    if (property.ClrType == typeof(DateTimeOffset))
                    {
                        property.SetValueConverter(
                            new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset,
                                string>(static v => v.ToString("O"), static v => DateTimeOffset.Parse(v)));
                    }
                    else
                    {
                        property.SetValueConverter(
                            new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?,
                                string?>(static v => v.HasValue ? v.Value.ToString("O") : null,
                                static v => v != null ? DateTimeOffset.Parse(v) : null));
                    }
                }

                var columnType = property.GetColumnType();
                if (columnType is null) continue;

                // Remap SQL Server types to SQLite equivalents
                if (columnType.Contains("nvarchar", StringComparison.OrdinalIgnoreCase) ||
                    columnType.Contains("varchar", StringComparison.OrdinalIgnoreCase) ||
                    columnType.StartsWith("char", StringComparison.OrdinalIgnoreCase))
                {
                    property.SetColumnType("TEXT");
                }
                else if (columnType.Equals("uniqueidentifier", StringComparison.OrdinalIgnoreCase))
                {
                    property.SetColumnType("TEXT");
                }
                else if (columnType.Contains("varbinary", StringComparison.OrdinalIgnoreCase))
                {
                    property.SetColumnType("BLOB");
                }
                else if (columnType.Equals("bigint", StringComparison.OrdinalIgnoreCase))
                {
                    property.SetColumnType("INTEGER");
                }
            }
        }
    }

    /// <summary>
    /// Updates application-managed concurrency tokens before save on providers
    /// that do not support database-generated rowversion columns.
    /// </summary>
    private void PrepareConcurrencyTokens()
    {
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer")
            return;

        foreach (var entry in ChangeTracker.Entries<AuditAggregateRoot>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Property(nameof(AuditAggregateRoot.RowVersion)).CurrentValue = Guid.NewGuid().ToByteArray();
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Property(nameof(AuditAggregateRoot.RowVersion)).CurrentValue = Guid.NewGuid().ToByteArray();
            }
        }
    }
}

/// <summary>
/// Interface for DbContexts that support audit bypassing.
/// </summary>
public interface IAuditBypassable
{
    /// <summary>
    /// Bypasses the audit interceptor for the current async context
    /// </summary>
    bool BypassAuditInterceptor { get; set; }
}
