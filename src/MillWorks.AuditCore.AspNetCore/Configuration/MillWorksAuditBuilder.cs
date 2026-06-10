using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.AspNetCore.Services;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database;
using AuditProviderDispatcher = MillWorks.AuditCore.Services.Core.AuditProviderDispatcher;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.DeadLetterQueue.Services;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Query;
using MillWorks.AuditCore.Services.DistributedLocking.Implementations;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;
using MillWorks.AuditCore.Services.Redis;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;
using MillWorks.AuditCore.Services.Compliance;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.Validators;
using MillWorks.AuditCore.Services.Validators.Interfaces;
using StackExchange.Redis;
using Azure.Storage.Blobs;
using Mapster;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.AspNetCore.Configuration;

/// <summary>
/// MillWorks Audit Builder for configuring audit services with circular dependency prevention
/// </summary>
public sealed class MillWorksAuditBuilder
{
    /// <summary>
    /// Service collection for configuring audit services
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Options for audit configuration
    /// </summary>
    public AuditOptions Options { get; }

    /// <summary>
    /// Constructor
    /// </summary>
    public MillWorksAuditBuilder(IServiceCollection services, AuditOptions options)
    {
        Services = services;
        Options = options;
    }

    /// <summary>
    /// Replaces the default field redactor with a custom implementation.
    /// Call this to override the safe-by-default <see cref="MillWorks.AuditCore.Services.Core.DefaultAuditFieldRedactor"/>
    /// with a domain-specific redactor that selectively allows or masks fields.
    /// </summary>
    /// <typeparam name="TRedactor">Custom redactor type implementing <see cref="IAuditFieldRedactor"/>.</typeparam>
    public void UseRedactor<TRedactor>() where TRedactor : class, IAuditFieldRedactor
    {
        // Remove any existing redactor registration and replace with the custom one
        var existing = Services.FirstOrDefault(static s => s.ServiceType == typeof(IAuditFieldRedactor));
        if (existing != null)
            Services.Remove(existing);

        Services.AddSingleton<IAuditFieldRedactor, TRedactor>();
    }

    /// <summary>
    /// Configures request-audit middleware behavior.
    /// </summary>
    public void UseMiddleware(Action<AuditMiddlewareOptions> configure)
    {
        Services.AddOptions<AuditMiddlewareOptions>()
            .Configure(configure);
    }

    /// <summary>
    /// Replaces the default in-process request audit dispatcher with a custom implementation.
    /// This allows consuming apps to bridge deferred request audits into their own job system.
    /// </summary>
    public void UseRequestAuditDispatcher<TDispatcher>()
        where TDispatcher : class, IRequestAuditDispatcher
    {
        // Remove only the InProcessRequestAuditDispatcher registrations, not other hosted services.
        // The wrapper class enables targeting IHostedService by ImplementationType.
        foreach (var descriptor in Services
                     .Where(static s =>
                         s.ServiceType == typeof(IRequestAuditDispatcher) ||
                         s.ServiceType == typeof(InProcessRequestAuditDispatcher) ||
                         s.ServiceType == typeof(InProcessRequestAuditDispatcherHostedService) ||
                         (s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) &&
                          s.ImplementationType == typeof(InProcessRequestAuditDispatcherHostedService)))
                     .ToList())
        {
            Services.Remove(descriptor);
        }

        Services.AddScoped<IRequestAuditDispatcher, TDispatcher>();
    }

    /// <summary>
    /// Use Entity Framework for audit storage with circular dependency prevention and automatic migration support
    /// </summary>
    public void UseEntityFramework(Action<EntityFrameworkOptions> configure)
    {
        Services.AddOptions<EntityFrameworkOptions>()
            .BindConfiguration("Audit:EntityFramework")
            .Configure(configure)
            .ValidateOnStart();

        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<EntityFrameworkOptions>, EntityFrameworkOptionsValidator>());

        // Configure Mapster
        ConfigureMapster();

        // Default audit failure policy. TryAdd lets consumers register a custom
        // IAuditFailurePolicy before AddMillWorksAudit to override regulated-entity detection.
        Services.TryAddSingleton<IAuditFailurePolicy, RegulatedEntityFailurePolicy>();

        // Register the audit interceptor as a singleton.
        // ComplianceOptions, IConsentVerificationService, and IAuditDiagnostics may not be registered
        // (UseCompliance() is optional, diagnostics is always registered). GetService returns null when not registered.
        // The IServiceScopeFactory is captured here so the singleton interceptor can resolve
        // a scoped IAuditSink per save without taking a scoped dependency directly.
        Services.AddSingleton<AuditSaveChangesInterceptor>(static sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AuditSaveChangesInterceptor>>();
            var complianceOptions = sp.GetService<ComplianceOptions>();
            var consentService = sp.GetService<IConsentVerificationService>();
            var diagnostics = sp.GetService<IAuditDiagnostics>();
            var auditOptions = sp.GetRequiredService<IOptions<AuditOptions>>().Value;
            var failurePolicy = sp.GetRequiredService<IAuditFailurePolicy>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            return new AuditSaveChangesInterceptor(
                logger,
                complianceOptions?.EnforcementMode,
                consentService,
                diagnostics,
                auditOptions.FailureMode,
                failurePolicy,
                scopeFactory);
        });

        // SQL metrics interceptor for Azure SQL observability (throttling, deadlock, connection pool errors)
        Services.AddSingleton<AuditSqlCommandInterceptor>();

        // Configure DbContext with interceptor and circular dependency prevention
        Services.AddDbContext<AuditDbContext>(static (serviceProvider, options) =>
        {
            var efOptions = serviceProvider.GetRequiredService<IOptions<EntityFrameworkOptions>>().Value;

            options.UseSqlServer(efOptions.ConnectionString, sqlOptions =>
            {
                // Note: EnableRetryOnFailure is intentionally NOT used here because
                // AuditCore uses explicit transactions (for tamper-detection atomicity,
                // archival, and compliance operations) which are incompatible with
                // SqlServerRetryingExecutionStrategy. Retry logic is handled at a
                // higher level by ResilientAuditLogger and the dead letter queue.
                // CommandTimeoutSeconds is for runtime queries; MigrationTimeoutSeconds
                // is applied separately in DatabaseInitializationService.
                sqlOptions.CommandTimeout(efOptions.CommandTimeoutSeconds);
                sqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "audit");
            });

            // Replace the default model-cache-key factory so the compiled-model cache is keyed
            // on schema alongside context type. Without this, processes that construct
            // AuditDbContext with different schemas could reuse the wrong model.
            options.ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>();

            var saveChangesInterceptor = serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>();
            var sqlCommandInterceptor = serviceProvider.GetRequiredService<AuditSqlCommandInterceptor>();
            options.AddInterceptors(saveChangesInterceptor, sqlCommandInterceptor);

            options.EnableSensitiveDataLogging(false);
            options.EnableDetailedErrors(false);
        });

        // Register ALL repositories
        Services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        Services.AddScoped<IAuditIntegrityRepository, AuditIntegrityRepository>();
        Services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        Services.AddScoped<IArchiveRecordRepository, ArchiveRecordRepository>();
        Services.AddScoped<ISecurityEventRepository, SecurityEventRepository>();

        // Register internal repository for circular dependency prevention
        Services.AddScoped<InternalAuditEventRepository>();

        // Ensure core infrastructure services are registered (no-op if AddMillWorksAudit already registered them)
        Services.TryAddScoped<IAuditContext, AuditContext>();
        Services.TryAddScoped<IAuditEventFactory, AuditEventFactory>();
        // AuditLogger is registered as the concrete type so ResilientAuditLogger can
        // resolve a fresh instance per retry scope (see UseResilience below). IAuditLogger
        // forwards to the same scoped AuditLogger — when Decorate<IAuditLogger,
        // ResilientAuditLogger>() runs later, Scrutor captures this forwarding factory as
        // the pre-decoration registration.
        Services.TryAddScoped<AuditLogger>();
        Services.TryAddScoped<IAuditLogger>(static sp => sp.GetRequiredService<AuditLogger>());

        Services.TryAddSingleton(TimeProvider.System);
        Services.AddScoped<IAuditEntityWriter, AuditDbContextEntityWriter>();
        Services.AddScoped<IAuditEntityBatchWriter, AuditEntityBatchWriter>();
        Services.AddScoped<IAuditEventBatchWriter, AuditEventBatchWriter>();
        Services.AddScoped<IAuditBatchProcessor, AuditBatchProcessor>();
        Services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        Services.AddScoped<IAuditOutboxWriter, AuditOutboxWriter>();

        // IAuditSink is selected at resolve time based on SecurityOptions.AuditSinkMode.
        // Default is ImmediateSink; TransactionalOutbox requires UseSecurity() to configure the mode.
        Services.AddScoped<ImmediateSink>();
        Services.AddScoped<TransactionalOutboxSink>();
        Services.AddScoped<IAuditSink>(static sp =>
        {
            var securityOptions = sp.GetService<IOptions<SecurityOptions>>();
            var mode = securityOptions?.Value.AuditSinkMode ?? AuditSinkMode.Immediate;

            return mode switch
            {
                AuditSinkMode.TransactionalOutbox => sp.GetRequiredService<TransactionalOutboxSink>(),
                _ => sp.GetRequiredService<ImmediateSink>(),
            };
        });

        // Register core audit services that depend on repositories
        Services.AddScoped<IAuditService, AuditService>();

        // Register query, search, report, and archival services
        Services.AddScoped<IAuditQueryService, AuditQueryService>();
        Services.AddScoped<IAuditSearchService, AuditSearchService>();
        Services.AddScoped<IAuditReportService, AuditReportService>();
        Services.AddScoped<IAuditArchivalService, AuditArchivalService>();
        Services.AddScoped<IAuditMaintenanceService, MillWorks.AuditCore.Services.Maintenance.AuditMaintenanceService>();
        Services.AddScoped<IAuditMetaTrackingService, AuditMetaTrackingService>();

        // DatabaseInitializationService self-gates on EntityFrameworkOptions.MigrateOnStartup /
        // EnsureDatabaseCreated inside StartAsync. Registered unconditionally.
        Services.AddHostedService<DatabaseInitializationService>();

        // AuditOutboxDrainer self-gates on AuditSinkMode.TransactionalOutbox inside ExecuteAsync.
        // Registered unconditionally — no-op when Immediate mode is active.
        Services.AddHostedService<AuditOutboxDrainer>();
    }

    /// <summary>
    /// Configure Mapster type adapter. Applies <see cref="AuditMappingConfiguration"/> onto
    /// <see cref="TypeAdapterConfig.GlobalSettings"/> — the same instance every other library's
    /// <c>IRegister</c> targets — and registers the global config via <c>TryAddSingleton</c> so
    /// a consumer-owned registration wins. A prior implementation created a fresh
    /// <see cref="TypeAdapterConfig"/>, applied only AuditCore's mappings, and registered it
    /// via <c>AddSingleton</c> — that won last-writer-wins for <c>IMapper</c> resolution and
    /// silently dropped every consumer mapping not defined in AuditCore. This is structurally
    /// the same shape as the <c>IConnectionMultiplexer</c> fix in UseSecurity.
    /// </summary>
    private void ConfigureMapster()
    {
        var config = TypeAdapterConfig.GlobalSettings;
        config.Apply(new AuditMappingConfiguration());

        Services.TryAddSingleton(config);
        Services.AddMapster();
    }

    /// <summary>
    /// Use security features for audit logging.
    /// </summary>
    /// <remarks>
    /// When <see cref="SecurityOptions.UseRedisLocking"/> is enabled, the consuming application
    /// must register an <see cref="IConnectionMultiplexer"/> in the service collection (typically
    /// via <c>services.AddSingleton&lt;IConnectionMultiplexer&gt;(...)</c>) before the
    /// <see cref="IAuditDistributedLockService"/> is resolved. AuditCore does not register
    /// <see cref="IConnectionMultiplexer"/> itself because consuming apps commonly own that
    /// registration for other components (token caches, rate limiters, etc.). When
    /// <see cref="SecurityOptions.UseRedisLocking"/> is false, distributed locking falls back
    /// to <c>InMemoryDistributedLockService</c> and <see cref="IConnectionMultiplexer"/> is
    /// not required.
    /// </remarks>
    public void UseSecurity(Action<SecurityOptions> configure)
    {
        Services.AddOptions<SecurityOptions>()
            .BindConfiguration("Audit:Security")
            .Configure(configure)
            .ValidateOnStart();

        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SecurityOptions>, SecurityOptionsValidator>());

        // Resolve-time forwarder so the remaining bare SecurityOptions consumer
        // (FerpaValidator) keeps working until that call site flips to IOptions<SecurityOptions>.
        Services.AddSingleton<SecurityOptions>(
            static sp => sp.GetRequiredService<IOptions<SecurityOptions>>().Value);

        // Security event service (required by tamper detection)
        Services.AddScoped<IAuditSecurityEventService, AuditSecurityEventService>();

        // Tamper detection service. Registered unconditionally; SecurityOptions.EnableTamperDetection
        // drives routing in consumers (AuditLogger, AuditArchivalService).
        Services.AddScoped<ITamperDetectionService, TamperDetectionService>();

        // Batched integrity writes infrastructure. Hosted services self-gate on
        // EnableTamperDetection && EnableBatchedIntegrityWrites inside ExecuteAsync (#12).
        Services.AddSingleton<IntegrityWriteBatcher>();
        Services.AddHostedService(static sp => sp.GetRequiredService<IntegrityWriteBatcher>());
        Services.AddHostedService<IntegrityReconciliationService>();

        // Health check for integrity pipeline status. The check self-gates on EnableTamperDetection (#12).
        Services.AddHealthChecks()
            .AddCheck<IntegrityHealthCheck>("audit_integrity_pipeline",
                tags: ["audit", "integrity"]);

        // IAuditDistributedLockService is registered as a singleton for callers that need a
        // general-purpose cross-process / cross-scope mutex (e.g. DeadLetterQueueProcessor's
        // leader election). A per-scope registration would give every caller its own lock
        // table and defeat mutual exclusion within a process.
        //
        // NOTE: the integrity-chain critical section in TamperDetectionService does NOT go
        // through this service. It serializes at the DB layer via SQL Server sp_getapplock
        // bound to the write transaction (see AuditIntegrityRepository.AcquireAppendLockAsync).
        // UseRedisLocking therefore has no effect on integrity-write correctness — it only
        // governs other coordination primitives above.
        //
        // When UseRedisLocking is true, the consumer is responsible for registering
        // IConnectionMultiplexer — AuditCore does not own that registration because consuming
        // apps commonly register it for their own components (token cache, rate limiter, etc.)
        // and an AuditCore-owned registration would collide last-writer-wins.
        Services.AddSingleton<IAuditDistributedLockService>(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<SecurityOptions>>().Value;

            if (!options.UseRedisLocking)
                return ActivatorUtilities.CreateInstance<InMemoryDistributedLockService>(sp);

            var multiplexer = sp.GetService<IConnectionMultiplexer>();
            if (multiplexer is null)
                throw new InvalidOperationException(
                    $"{nameof(SecurityOptions.UseRedisLocking)} is true but no " +
                    $"{nameof(IConnectionMultiplexer)} is registered in the service collection. " +
                    "Register one via services.AddSingleton<IConnectionMultiplexer>(...) before " +
                    "calling AddMillWorksAudit, or set " +
                    $"{nameof(SecurityOptions.UseRedisLocking)} = false to use the in-memory lock.");

            return ActivatorUtilities.CreateInstance<RedisDistributedLockService>(sp, multiplexer);
        });
    }

    /// <summary>
    /// Use compliance validators for audit logging
    /// </summary>
    public void UseCompliance(Action<ComplianceOptions> configure)
    {
        Services.AddOptions<ComplianceOptions>()
            .BindConfiguration("Audit:Compliance")
            .Configure(configure)
            .ValidateOnStart();

        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ComplianceOptions>, ComplianceOptionsValidator>());

        // Resolve-time forwarder so bare ComplianceOptions consumers (e.g., the
        // AuditSaveChangesInterceptor factory's sp.GetService<ComplianceOptions>() lookup)
        // keep working until every call site flips.
        Services.AddSingleton<ComplianceOptions>(
            static sp => sp.GetRequiredService<IOptions<ComplianceOptions>>().Value);

        // Register IMemoryCache if not already registered (needed by ConsentVerificationService)
        Services.AddMemoryCache();

        // Register consent verification service (singleton — IMemoryCache is sync-safe)
        Services.AddSingleton<IConsentVerificationService, ConsentVerificationService>();

        // Register attribute scanner as singleton (eagerly populates caches in constructor).
        // Reads the assembly list from IOptions<ComplianceOptions> at resolve time.
        Services.AddSingleton<IComplianceAttributeScanner>(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<ComplianceOptions>>().Value;
            var logger = sp.GetService<ILogger<ComplianceAttributeScanner>>();
            return new ComplianceAttributeScanner(
                options.AssembliesToScan.Count > 0 ? options.AssembliesToScan : null,
                logger);
        });

        // Register compliance service (will be used by AuditServiceExtensions)
        Services.AddScoped<IAuditComplianceService, AuditComplianceService>();

        // Register validators via TryAddEnumerable so consumers can add their own.
        // Each validator checks ComplianceOptions.Standards at runtime and short-circuits
        // if its standard isn't configured.
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IComplianceValidator, GdprValidator>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IComplianceValidator, Soc2Validator>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IComplianceValidator, HipaaValidator>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IComplianceValidator, Iso27001Validator>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IComplianceValidator, FerpaValidator>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IComplianceValidator, PciDssValidator>());
        Services.TryAddEnumerable(ServiceDescriptor.Scoped<IComplianceValidator, StigValidator>());
    }

    /// <summary>
    /// Use archival for audit logs
    /// </summary>
    public void UseArchival(Action<ArchivalOptions> configure)
    {
        Services.AddOptions<ArchivalOptions>()
            .BindConfiguration("Audit:Archival")
            .Configure(configure)
            .ValidateOnStart();

        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ArchivalOptions>, ArchivalOptionsValidator>());

        // Blob client factory. Only resolved when the AzureBlob provider is in use.
        // AzureBlob-requires-connection-string and AWSs3-not-implemented checks live in
        // ArchivalOptionsValidator (startup failures). Connection-string format errors are
        // wrapped here at resolve time.
        Services.AddSingleton<BlobServiceClient>(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<ArchivalOptions>>().Value;

            if (options.Provider != ArchivalProvider.AzureBlob)
                throw new InvalidOperationException(
                    $"{nameof(BlobServiceClient)} is only available when " +
                    $"{nameof(ArchivalOptions.Provider)} is {nameof(ArchivalProvider.AzureBlob)}.");

            try
            {
                return new BlobServiceClient(options.ConnectionString);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"Azure Blob archival connection string is invalid: {ex.Message}", ex);
            }
        });

        // Archive background services self-gate on EnableBackgroundArchival inside ExecuteAsync (#12).
        Services.AddHostedService<ArchiveCreationBackgroundService>();
        Services.AddHostedService<ArchiveVerificationBackgroundService>();
    }

    /// <summary>
    /// Register custom providers for audit logging
    /// </summary>
    public void RegisterProviders(Action<ProviderRegistry> configure)
    {
        var map = new AuditProviderTypeMap();
        var registry = new ProviderRegistry(Services, map);
        configure(registry);
        Services.AddSingleton(map);
        Services.AddScoped<IAuditProviderDispatcher, AuditProviderDispatcher>();
    }

    /// <summary>
    /// Use resilience features for audit logging with circular dependency prevention
    /// </summary>
    public void UseResilience(Action<ResilienceOptions> configure)
    {
        Services.AddOptions<ResilienceOptions>()
            .BindConfiguration("Audit:Resilience")
            .Configure(configure)
            .ValidateOnStart();

        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ResilienceOptions>, ResilienceOptionsValidator>());

        // Dead letter queue provider chosen at resolve time based on DeadLetterProvider.
        Services.AddSingleton<IAuditDeadLetterQueue>(static sp =>
        {
            var options = sp.GetRequiredService<IOptions<ResilienceOptions>>().Value;

            return options.DeadLetterProvider switch
            {
                DeadLetterProvider.InMemory => ActivatorUtilities.CreateInstance<InMemoryAuditDeadLetterQueue>(sp),
                DeadLetterProvider.FileSystem => ActivatorUtilities.CreateInstance<FileBasedAuditDeadLetterQueue>(sp),
                DeadLetterProvider.Redis => ActivatorUtilities.CreateInstance<RedisAuditDeadLetterQueue>(sp),
                _ => throw new InvalidOperationException(
                    $"Unknown {nameof(DeadLetterProvider)} value: {options.DeadLetterProvider}")
            };
        });

        // Dead letter queue background processor self-gates on EnableBackgroundProcessor inside
        // ExecuteAsync (#12).
        Services.AddHostedService<DeadLetterQueueProcessor>();

        // Decorate IAuditLogger with resilient version
        Services.Decorate<IAuditLogger, ResilientAuditLogger>();
    }

    /// <summary>
    /// Validates that the audit system is properly configured. Option-value checks
    /// (HmacKey / EnableDigitalSignatures / Production HMAC requirement) live in
    /// AuditOptionsValidator and fire via ValidateOnStart(). This method retains only
    /// the service-graph checks that cannot be expressed as <c>IValidateOptions&lt;T&gt;</c>.
    /// </summary>
    public void ValidateConfiguration()
    {
        // Check if at least one storage mechanism is configured
        var hasStorage = Services.Any(static s =>
            s.ServiceType == typeof(IAuditEventRepository) ||
            s.ServiceType == typeof(AuditDbContext));

        if (!hasStorage)
        {
            throw new InvalidOperationException(
                "No audit storage configured. Call UseEntityFramework() to configure storage.");
        }

        // Validate that the pass-through redactor is not registered unless explicitly allowed.
        // The pass-through redactor performs no redaction and will persist
        // PHI/PII/secrets in cleartext — this is unsafe for any environment handling real data.
        var redactorDescriptor = Services.FirstOrDefault(
            static s => s.ServiceType == typeof(IAuditFieldRedactor));

        if (redactorDescriptor?.ImplementationType == typeof(PassThroughAuditFieldRedactor)
            && !Options.AllowPassThroughRedactor)
        {
            if (Options.Environment.Equals("Production", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "PassThroughAuditFieldRedactor is not permitted in Production. " +
                    "Register a custom IAuditFieldRedactor before calling AddMillWorksAudit(), " +
                    "or set AllowPassThroughRedactor = true to explicitly accept unredacted audit storage.");
            }

            // Non-production environments: block by default too, with a clearer message.
            // PassThrough is only acceptable when AllowPassThroughRedactor is explicitly true.
            throw new InvalidOperationException(
                $"PassThroughAuditFieldRedactor is not permitted (Environment: {Options.Environment}). " +
                "The default redactor (DefaultAuditFieldRedactor) is safe-by-default. If you have " +
                "explicitly registered PassThroughAuditFieldRedactor, set AllowPassThroughRedactor = true " +
                "to confirm you accept unredacted audit storage, or register a custom IAuditFieldRedactor.");
        }
    }
}
