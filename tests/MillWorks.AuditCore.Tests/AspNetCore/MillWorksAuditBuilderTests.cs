using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Providers.Base;
using MillWorks.AuditCore.AspNetCore.Configuration;
using MillWorks.AuditCore.AspNetCore.Configuration.Options;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Redis;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;
using MillWorks.AuditCore.Services.Validators.Interfaces;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Tests.AspNetCore;

[TestFixture]
[Category("Unit")]
public class MillWorksAuditBuilderTests
{
    private IServiceCollection _services;
    private AuditOptions _options;
    private MillWorksAuditBuilder _builder;

    [SetUp]
    public void Setup()
    {
        _services = new ServiceCollection();
        _services.AddLogging();
        _options = new AuditOptions { ApplicationName = "TestApp" };
        _builder = new MillWorksAuditBuilder(_services, _options);
    }

    [Test]
    public void Constructor_SetsServicesAndOptions()
    {
        Assert.That(_builder.Services, Is.SameAs(_services));
        Assert.That(_builder.Options, Is.SameAs(_options));
    }

    private sealed class TestRequestAuditDispatcher : IRequestAuditDispatcher
    {
        public ValueTask DispatchAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    [Test]
    public void UseEntityFramework_RegistersDbContextAndRepositories()
    {
        _builder.UseEntityFramework(static ef =>
        {
            ef.ConnectionString = "Server=test;Database=test;";
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(AuditApplicationDbContext)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditEventRepository)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditIntegrityRepository)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditLogRepository)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IArchiveRecordRepository)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(ISecurityEventRepository)), Is.True);
    }

    [Test]
    public void UseEntityFramework_RegistersCoreServices()
    {
        _builder.UseEntityFramework(static ef =>
        {
            ef.ConnectionString = "Server=test;Database=test;";
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditService)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditQueryService)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditSearchService)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditReportService)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditArchivalService)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditMaintenanceService)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditMetaTrackingService)), Is.True);
    }

    [Test]
    public void UseMiddleware_RegistersMiddlewareOptionsConfiguration()
    {
        _builder.UseMiddleware(options =>
        {
            options.AuditWritesOnly = true;
            options.ExcludedReadPaths.Add("/dashboard");
        });

        var provider = _services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<AuditMiddlewareOptions>>().Value;

        Assert.That(resolved.AuditWritesOnly, Is.True);
        Assert.That(resolved.ExcludedReadPaths, Does.Contain("/dashboard"));
    }

    [Test]
    public void UseRequestAuditDispatcher_ReplacesDefaultDispatcherRegistration()
    {
        _services.AddSingleton<IRequestAuditDispatcher, InProcessRequestAuditDispatcher>();

        _builder.UseRequestAuditDispatcher<TestRequestAuditDispatcher>();

        var descriptor = _services.Last(static s => s.ServiceType == typeof(IRequestAuditDispatcher));
        Assert.That(descriptor.ImplementationType, Is.EqualTo(typeof(TestRequestAuditDispatcher)));
    }

    [Test]
    public void UseEntityFramework_MigrateOnStartup_RegistersDatabaseInitService()
    {
        _builder.UseEntityFramework(static ef =>
        {
            ef.ConnectionString = "Server=test;Database=test;";
            ef.MigrateOnStartup = true;
        });

        // DatabaseInitializationService is registered as IHostedService
        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)), Is.True);
    }

    [Test]
    public void UseEntityFramework_NoMigration_DoesNotRegisterHostedService()
    {
        _builder.UseEntityFramework(static ef =>
        {
            ef.ConnectionString = "Server=test;Database=test;";
            ef.MigrateOnStartup = false;
            ef.EnsureDatabaseCreated = false;
        });

        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)), Is.False);
    }

    [Test]
    public void UseSecurity_RegistersSecurityEventService()
    {
        _builder.UseSecurity(static security =>
        {
            security.EnableTamperDetection = false;
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditSecurityEventService)), Is.True);
    }

    [Test]
    public void UseSecurity_EnableTamperDetection_RegistersTamperService()
    {
        _builder.UseSecurity(static security =>
        {
            security.EnableTamperDetection = true;
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(ITamperDetectionService)), Is.True);
    }

    [Test]
    public void UseSecurity_TamperDetectionDisabled_DoesNotRegisterTamperService()
    {
        _builder.UseSecurity(static security =>
        {
            security.EnableTamperDetection = false;
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(ITamperDetectionService)), Is.False);
    }

    [Test]
    public void UseCompliance_RegistersComplianceService()
    {
        _builder.UseCompliance(static compliance =>
        {
            compliance.Standards.Add(ComplianceStandard.GDPR);
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditComplianceService)), Is.True);
    }

    [Test]
    public void UseCompliance_RegistersValidatorsForEachStandard()
    {
        _builder.UseCompliance(static compliance =>
        {
            compliance.Standards.Add(ComplianceStandard.GDPR);
            compliance.Standards.Add(ComplianceStandard.SOC2);
            compliance.Standards.Add(ComplianceStandard.HIPAA);
        });

        var validatorRegistrations = _services
            .Where(static s => s.ServiceType == typeof(IComplianceValidator))
            .ToList();

        Assert.That(validatorRegistrations, Has.Count.EqualTo(3));
    }

    [Test]
    public void UseSecurity_WithRedisLocking_RegistersRedisLockServices()
    {
        _builder.UseSecurity(static security =>
        {
            security.EnableTamperDetection = false;
            security.UseRedisLocking = true;
            security.RedisConnectionString = "localhost:6379";
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(IConnectionMultiplexer)), Is.True);
        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(MillWorks.AuditCore.Services.DistributedLocking.Interfaces.IAuditDistributedLockService)
            && s.ImplementationType == typeof(RedisDistributedLockService)), Is.True);
    }

    [Test]
    public void UseSecurity_WithoutRedisConnection_RegistersInMemoryLockService()
    {
        _builder.UseSecurity(static security =>
        {
            security.EnableTamperDetection = false;
            security.UseRedisLocking = true;
            security.RedisConnectionString = "";
        });

        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(MillWorks.AuditCore.Services.DistributedLocking.Interfaces.IAuditDistributedLockService)
            && s.ImplementationType == typeof(MillWorks.AuditCore.Services.DistributedLocking.Implementations.InMemoryDistributedLockService)), Is.True);
    }

    [Test]
    public void UseResilience_EnableDLQ_InMemory_RegistersInMemoryQueue()
    {
        // UseResilience calls Decorate<IAuditLogger>, which requires IAuditLogger to be registered
        _builder.UseEntityFramework(static ef => ef.ConnectionString = "Server=test;Database=test;");

        _builder.UseResilience(static resilience =>
        {
            resilience.EnableDeadLetterQueue = true;
            resilience.DeadLetterProvider = DeadLetterProvider.InMemory;
            resilience.EnableBackgroundProcessor = false;
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditDeadLetterQueue)), Is.True);
    }

    [Test]
    public void UseResilience_DLQDisabled_ThrowsInvalidOperation()
    {
        _builder.UseEntityFramework(static ef => ef.ConnectionString = "Server=test;Database=test;");

        Assert.Throws<InvalidOperationException>(() =>
        {
            _builder.UseResilience(static resilience =>
            {
                resilience.EnableDeadLetterQueue = false;
            });
        });
    }

    [Test]
    public void UseResilience_WithoutAuditLoggerRegistration_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            _builder.UseResilience(static resilience =>
            {
                resilience.EnableDeadLetterQueue = true;
                resilience.DeadLetterProvider = DeadLetterProvider.InMemory;
            });
        });
    }

    [Test]
    public void UseResilience_FileSystemProvider_RegistersFileQueue()
    {
        _builder.UseEntityFramework(static ef => ef.ConnectionString = "Server=test;Database=test;");

        _builder.UseResilience(static resilience =>
        {
            resilience.EnableDeadLetterQueue = true;
            resilience.EnableBackgroundProcessor = false;
            resilience.DeadLetterProvider = DeadLetterProvider.FileSystem;
        });

        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(IAuditDeadLetterQueue)
            && s.ImplementationType == typeof(MillWorks.AuditCore.Services.DeadLetterQueue.Implementations.FileBasedAuditDeadLetterQueue)), Is.True);
    }

    [Test]
    public void UseResilience_RedisProvider_RegistersRedisQueue()
    {
        _builder.UseEntityFramework(static ef => ef.ConnectionString = "Server=test;Database=test;");

        _builder.UseResilience(static resilience =>
        {
            resilience.EnableDeadLetterQueue = true;
            resilience.EnableBackgroundProcessor = false;
            resilience.DeadLetterProvider = DeadLetterProvider.Redis;
        });

        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(IAuditDeadLetterQueue)
            && s.ImplementationType == typeof(MillWorks.AuditCore.Services.DeadLetterQueue.Implementations.RedisAuditDeadLetterQueue)), Is.True);
    }

    [Test]
    public void UseResilience_BackgroundProcessorDisabled_DoesNotRegisterHostedService()
    {
        _builder.UseEntityFramework(static ef => ef.ConnectionString = "Server=test;Database=test;");

        _builder.UseResilience(static resilience =>
        {
            resilience.EnableDeadLetterQueue = true;
            resilience.EnableBackgroundProcessor = false;
            resilience.DeadLetterProvider = DeadLetterProvider.InMemory;
        });

        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
            && s.ImplementationType == typeof(MillWorks.AuditCore.Services.DeadLetterQueue.Models.DeadLetterQueueProcessor)), Is.False);
    }

    [Test]
    public void RegisterProviders_RegistersMapAndDispatcher()
    {
        _builder.RegisterProviders(static registry =>
        {
            // No providers to register in this test
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(AuditProviderTypeMap)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditProviderDispatcher)), Is.True);
    }

    [Test]
    public void RegisterProviders_AddProvider_RegistersProviderTypeAndMapping()
    {
        _builder.RegisterProviders(registry => registry.AddProvider<TestAuditProvider>("Order"));

        using var provider = _services.BuildServiceProvider();
        var typeMap = provider.GetRequiredService<AuditProviderTypeMap>();

        Assert.That(_services.Any(static s => s.ServiceType == typeof(TestAuditProvider)), Is.True);
        Assert.That(typeMap.GetProviderType("Order"), Is.EqualTo(typeof(TestAuditProvider)));
    }

    [Test]
    public void ValidateConfiguration_NoStorage_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(() => _builder.ValidateConfiguration());
    }

    [Test]
    public void ValidateConfiguration_WithStorage_DoesNotThrow()
    {
        _builder.UseEntityFramework(static ef =>
        {
            ef.ConnectionString = "Server=test;Database=test;";
        });

        Assert.DoesNotThrow(() => _builder.ValidateConfiguration());
    }

    [Test]
    public void ValidateConfiguration_DigitalSignaturesWithoutHmacKey_Throws()
    {
        _builder.UseEntityFramework(static ef =>
        {
            ef.ConnectionString = "Server=test;Database=test;";
        });

        _options.EnableDigitalSignatures = true;
        _options.HmacKey = null;

        Assert.Throws<InvalidOperationException>(() => _builder.ValidateConfiguration());
    }

    [Test]
    public void ValidateConfiguration_DigitalSignaturesWithValidKey_DoesNotThrow()
    {
        _builder.UseEntityFramework(static ef =>
        {
            ef.ConnectionString = "Server=test;Database=test;";
        });

        _options.EnableDigitalSignatures = true;
        _options.HmacKey = "this-is-a-very-long-hmac-key-that-is-at-least-32-characters";

        Assert.DoesNotThrow(() => _builder.ValidateConfiguration());
    }

    [Test]
    public void UseArchival_FileSystem_DoesNotThrow()
    {
        _builder.UseArchival(static archival =>
        {
            archival.Provider = ArchivalProvider.FileSystem;
            archival.EnableBackgroundArchival = false;
        });

        // FileSystem provider requires no special registration
        Assert.Pass();
    }

    [Test]
    public void UseArchival_AzureBlob_MissingConnectionString_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            _builder.UseArchival(static archival =>
            {
                archival.Provider = ArchivalProvider.AzureBlob;
                archival.ConnectionString = null;
            });
        });
    }

    [Test]
    public void UseArchival_AzureBlob_WithValidConnectionString_RegistersBlobServiceClient()
    {
        _builder.UseArchival(static archival =>
        {
            archival.Provider = ArchivalProvider.AzureBlob;
            archival.ConnectionString =
                "DefaultEndpointsProtocol=https;AccountName=testaccount;AccountKey=dGVzdGtleQ==;EndpointSuffix=core.windows.net";
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(Azure.Storage.Blobs.BlobServiceClient)), Is.True);
    }

    [Test]
    public void UseArchival_AwsS3_ThrowsNotImplemented()
    {
        Assert.Throws<NotImplementedException>(() =>
        {
            _builder.UseArchival(static archival =>
            {
                archival.Provider = ArchivalProvider.AWSs3;
            });
        });
    }

    [Test]
    public void UseArchival_EnableBackgroundArchival_RegistersHostedService()
    {
        _builder.UseArchival(static archival =>
        {
            archival.Provider = ArchivalProvider.FileSystem;
            archival.EnableBackgroundArchival = true;
        });

        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)), Is.True);
    }

    private sealed class TestAuditProvider : IAuditProvider
    {
        public string EntityType => "Order";
        public Task<AuditEvent> CreateAuditEventAsync(string action, object? entity, object? oldValues = null)
            => Task.FromResult(new AuditEvent { EntityName = EntityType });
        public Task<bool> ShouldAuditAsync(string action, object entity) => Task.FromResult(true);
        public Task EnrichAuditEventAsync(AuditEvent auditEvent, object? entity) => Task.CompletedTask;
        public Dictionary<string, object?> GetChanges(object? oldValues, object? newValues) => new();
    }
}
