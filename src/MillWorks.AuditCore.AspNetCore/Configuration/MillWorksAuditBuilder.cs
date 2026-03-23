using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.AspNetCore.Configuration.Options;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.AspNetCore.Services;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Interceptors;
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
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;
using MillWorks.AuditCore.Services.Compliance;
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
    /// Use Entity Framework for audit storage with circular dependency prevention and automatic migration support
    /// </summary>
    public void UseEntityFramework(Action<EntityFrameworkOptions> configure)
    {
        var efOptions = new EntityFrameworkOptions();
        configure(efOptions);

        if (string.IsNullOrWhiteSpace(efOptions.ConnectionString))
        {
            throw new InvalidOperationException(
                "Entity Framework connection string is required. " +
                "Set ConnectionString in UseEntityFramework options.");
        }

        Services.AddSingleton(efOptions);

        // Configure Mapster
        ConfigureMapster();

        // Register the audit interceptor as a singleton.
        // ComplianceOptions and IConsentVerificationService may not be registered
        // (UseCompliance() is optional). GetService returns null when not registered.
        Services.AddSingleton<AuditSaveChangesInterceptor>(static sp =>
        {
            var logger = sp.GetRequiredService<ILogger<AuditSaveChangesInterceptor>>();
            var complianceOptions = sp.GetService<ComplianceOptions>();
            var consentService = sp.GetService<IConsentVerificationService>();
            return new AuditSaveChangesInterceptor(
                logger,
                complianceOptions?.EnforcementMode,
                consentService);
        });

        // Configure DbContext with interceptor and circular dependency prevention
        Services.AddDbContext<AuditApplicationDbContext>((serviceProvider, options) =>
        {
            options.UseSqlServer(efOptions.ConnectionString, sqlOptions =>
            {
                // Note: EnableRetryOnFailure is intentionally NOT used here because
                // AuditCore uses explicit transactions (for tamper-detection atomicity,
                // archival, and compliance operations) which are incompatible with
                // SqlServerRetryingExecutionStrategy. Retry logic is handled at a
                // higher level by ResilientAuditLogger and the dead letter queue.
                sqlOptions.CommandTimeout(efOptions.MigrationTimeoutSeconds);
                sqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "audit");
            });

            var interceptor = serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>();
            options.AddInterceptors(interceptor);

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
        Services.TryAddScoped<IAuditLogger, AuditLogger>();

        // Register core audit services that depend on repositories
        Services.AddScoped<IAuditService, AuditService>();

        // Register query, search, report, and archival services
        Services.AddScoped<IAuditQueryService, AuditQueryService>();
        Services.AddScoped<IAuditSearchService, AuditSearchService>();
        Services.AddScoped<IAuditReportService, AuditReportService>();
        Services.AddScoped<IAuditArchivalService, AuditArchivalService>();
        Services.AddScoped<IAuditMaintenanceService, MillWorks.AuditCore.Services.Maintenance.AuditMaintenanceService>();
        Services.AddScoped<IAuditMetaTrackingService, AuditMetaTrackingService>();

        // Register database initialization service if migrations are enabled
        if (efOptions.MigrateOnStartup || efOptions.EnsureDatabaseCreated)
        {
            Services.AddHostedService<DatabaseInitializationService>();
        }
    }

    /// <summary>
    /// Configure Mapster type adapter
    /// </summary>
    private void ConfigureMapster()
    {
        var config = new TypeAdapterConfig();

        // Apply mapping configurations
        config.Apply(new AuditMappingConfiguration());

        // Register using modern Mapster.DependencyInjection approach
        Services.AddSingleton(config);
        Services.AddMapster();
    }

    /// <summary>
    /// Use security features for audit logging
    /// </summary>
    public void UseSecurity(Action<SecurityOptions> configure)
    {
        var securityOptions = new SecurityOptions();
        configure(securityOptions);

        Services.AddSingleton(securityOptions);

        // Register security event service (required by tamper detection)
        Services.AddScoped<IAuditSecurityEventService, AuditSecurityEventService>();

        if (securityOptions.EnableTamperDetection)
        {
            Services.AddScoped<ITamperDetectionService, TamperDetectionService>();
        }

        if (securityOptions.UseRedisLocking && !string.IsNullOrEmpty(securityOptions.RedisConnectionString))
        {
            Services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var configOptions = ConfigurationOptions.Parse(securityOptions.RedisConnectionString);
                configOptions.AbortOnConnectFail = false;
                configOptions.ConnectTimeout = 5000;
                return ConnectionMultiplexer.Connect(configOptions);
            });
        }
    }

    /// <summary>
    /// Use compliance validators for audit logging
    /// </summary>
    public void UseCompliance(Action<ComplianceOptions> configure)
    {
        var complianceOptions = new ComplianceOptions();
        configure(complianceOptions);

        Services.AddSingleton(complianceOptions);

        // Register IMemoryCache if not already registered (needed by ConsentVerificationService)
        Services.AddMemoryCache();

        // Register consent verification service (singleton — IMemoryCache is sync-safe)
        Services.AddSingleton<IConsentVerificationService, ConsentVerificationService>();

        // Register attribute scanner as singleton (eagerly populates caches in constructor)
        Services.AddSingleton<IComplianceAttributeScanner>(sp =>
        {
            var logger = sp.GetService<ILogger<ComplianceAttributeScanner>>();
            return new ComplianceAttributeScanner(
                complianceOptions.AssembliesToScan.Count > 0 ? complianceOptions.AssembliesToScan : null,
                logger);
        });

        // Register compliance service (will be used by AuditServiceExtensions)
        Services.AddScoped<IAuditComplianceService, AuditComplianceService>();

        // Register validators based on standards
        foreach (var standard in complianceOptions.Standards)
        {
            switch (standard)
            {
                case ComplianceStandard.GDPR:
                    Services.AddScoped<IComplianceValidator, GdprValidator>();
                    break;
                case ComplianceStandard.SOC2:
                    Services.AddScoped<IComplianceValidator, Soc2Validator>();
                    break;
                case ComplianceStandard.HIPAA:
                    Services.AddScoped<IComplianceValidator, HipaaValidator>();
                    break;
                case ComplianceStandard.ISO27001:
                    Services.AddScoped<IComplianceValidator, Iso27001Validator>();
                    break;
                case ComplianceStandard.FERPA:
                    Services.AddScoped<IComplianceValidator, FerpaValidator>();
                    break;
                case ComplianceStandard.PCI_DSS:
                    Services.AddScoped<IComplianceValidator, PciDssValidator>();
                    break;
                case ComplianceStandard.STIG:
                    Services.AddScoped<IComplianceValidator, StigValidator>();
                    break;
            }
        }
    }

    /// <summary>
    /// Use archival for audit logs
    /// </summary>
    public void UseArchival(Action<ArchivalOptions> configure)
    {
        var archivalOptions = new ArchivalOptions();
        configure(archivalOptions);

        Services.AddSingleton(archivalOptions);

        // Register Azure Blob Storage client based on provider
        switch (archivalOptions.Provider)
        {
            case ArchivalProvider.AzureBlob:
                if (!string.IsNullOrEmpty(archivalOptions.ConnectionString))
                {
                    // Validate eagerly so invalid connection strings fail at startup, not at first resolve
                    try
                    {
                        var client = new BlobServiceClient(archivalOptions.ConnectionString);
                        Services.AddSingleton(client);
                    }
                    catch (FormatException ex)
                    {
                        throw new InvalidOperationException(
                            $"Azure Blob archival connection string is invalid: {ex.Message}", ex);
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "Azure Blob archival provider requires a connection string. " +
                        "Provide a valid Azure Storage connection string in ArchivalOptions.ConnectionString.");
                }

                break;

            case ArchivalProvider.FileSystem:
                // No special registration needed for file system
                break;

            case ArchivalProvider.AWSs3:
                // AWS S3 implementation would go here
                throw new NotImplementedException("AWS S3 archival provider not yet implemented");
        }

        // Register background archival service if enabled
        if (archivalOptions.EnableBackgroundArchival)
        {
            Services.AddHostedService<ArchiveVerificationBackgroundService>();
        }
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
        var resilienceOptions = new ResilienceOptions();
        configure(resilienceOptions);

        Services.AddSingleton(resilienceOptions);

        if (!resilienceOptions.EnableDeadLetterQueue)
        {
            throw new InvalidOperationException(
                "UseResilience() requires EnableDeadLetterQueue to be true. " +
                "ResilientAuditLogger depends on a dead letter queue for failure handling.");
        }

        // Verify IAuditLogger is registered before attempting to decorate it
        var hasLogger = Services.Any(static s => s.ServiceType == typeof(IAuditLogger));
        if (!hasLogger)
        {
            throw new InvalidOperationException(
                "IAuditLogger is not registered. Call UseEntityFramework() before UseResilience().");
        }

        switch (resilienceOptions.DeadLetterProvider)
        {
            case DeadLetterProvider.InMemory:
                Services.AddSingleton<IAuditDeadLetterQueue, InMemoryAuditDeadLetterQueue>();
                break;
            case DeadLetterProvider.FileSystem:
                Services.AddSingleton<IAuditDeadLetterQueue, FileBasedAuditDeadLetterQueue>();
                break;
            case DeadLetterProvider.Redis:
                Services.AddSingleton<IAuditDeadLetterQueue, RedisAuditDeadLetterQueue>();
                break;
        }

        if (resilienceOptions.EnableBackgroundProcessor)
        {
            Services.AddHostedService<DeadLetterQueueProcessor>();
        }

        // Decorate IAuditLogger with resilient version
        Services.Decorate<IAuditLogger, ResilientAuditLogger>();
    }

    /// <summary>
    /// Validates that the audit system is properly configured
    /// </summary>
    public void ValidateConfiguration()
    {
        // Validate audit options
        Options.Validate();

        // Check if at least one storage mechanism is configured
        var hasStorage = Services.Any(static s =>
            s.ServiceType == typeof(IAuditEventRepository) ||
            s.ServiceType == typeof(AuditApplicationDbContext));

        if (!hasStorage)
        {
            throw new InvalidOperationException(
                "No audit storage configured. Call UseEntityFramework() to configure storage.");
        }

        // Validate that if digital signatures are enabled, a key is provided
        if (Options.EnableDigitalSignatures && string.IsNullOrEmpty(Options.HmacKey))
        {
            throw new InvalidOperationException(
                "EnableDigitalSignatures is true but no HmacKey was provided");
        }
    }
}
