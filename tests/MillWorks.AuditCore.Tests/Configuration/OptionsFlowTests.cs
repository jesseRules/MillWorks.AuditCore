using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.AspNetCore.Configuration;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DistributedLocking.Implementations;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection;

namespace MillWorks.AuditCore.Tests.Configuration;

/// <summary>
/// Phase 1 acceptance tests for the options flow contract: fluent builder values must
/// reach IOptions&lt;AuditOptions&gt; and TamperDetectionService, IConfiguration binding
/// must survive as a fallback when the consumer does not fluent-set a property, fluent
/// wins where explicitly set, and Production without an HMAC key must fail at startup.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class OptionsFlowTests
{
    [SetUp]
    public void Setup()
    {
        // Chain state bleeds across TamperDetectionService instances via a static cache.
        TamperDetectionService.ResetPreviousHashCache();
    }

    [Test]
    public void FluentHmacKey_FlowsThroughOptionsPipeline()
    {
        const string fluentKey = "fluent-hmac-key-64chars-1234567890abcdef1234567890abcdef1234567";

        var services = BuildServices(
            config: null,
            configure: builder =>
            {
                builder.Options.HmacKey = fluentKey;
                builder.Options.EnableDigitalSignatures = true;
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.HmacKey, Is.EqualTo(fluentKey));
        Assert.That(options.EnableDigitalSignatures, Is.True);
    }

    [Test]
    public void ConfigurationBinding_FallbackResolves()
    {
        const string configKey = "config-hmac-key-64chars-1234567890abcdef1234567890abcdef1234567";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:HmacKey"] = configKey,
                ["Audit:EnableDigitalSignatures"] = "true"
            })
            .Build();

        var services = BuildServices(config: config, configure: null);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.HmacKey, Is.EqualTo(configKey));
        Assert.That(options.EnableDigitalSignatures, Is.True);
    }

    [Test]
    public void FluentConfigureOverridesBindConfiguration()
    {
        const string configKey = "config-hmac-key-64chars-1234567890abcdef1234567890abcdef1234567";
        const string fluentKey = "fluent-hmac-key-64chars-abcdef1234567890abcdef1234567890abcdef12";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:HmacKey"] = configKey
            })
            .Build();

        var services = BuildServices(
            config: config,
            configure: builder => builder.Options.HmacKey = fluentKey);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.HmacKey, Is.EqualTo(fluentKey));
    }

    [Test]
    public async Task TamperDetectionService_HmacSignature_MatchesAcrossFluentAndConfigPaths()
    {
        const string sharedKey = "shared-hmac-key-64chars-1234567890abcdef1234567890abcdef1234567";

        var fluentServices = BuildServices(
            config: null,
            configure: builder => builder.Options.HmacKey = sharedKey);

        using var fluentProvider = fluentServices.BuildServiceProvider();
        var fluentOptions = fluentProvider.GetRequiredService<IOptions<AuditOptions>>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:HmacKey"] = sharedKey
            })
            .Build();

        var configServices = BuildServices(config: config, configure: null);

        using var configProvider = configServices.BuildServiceProvider();
        var configOptions = configProvider.GetRequiredService<IOptions<AuditOptions>>();

        var dto = new AuditIntegrityDto
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow,
            JsonData = "{\"test\":\"same-data\"}"
        };

        TamperDetectionService.ResetPreviousHashCache();
        var fluentSignature = await CaptureHmacSignatureAsync(fluentOptions, dto);

        TamperDetectionService.ResetPreviousHashCache();
        var configSignature = await CaptureHmacSignatureAsync(configOptions, dto);

        Assert.That(fluentSignature, Is.Not.Null.And.Not.Empty);
        Assert.That(configSignature, Is.EqualTo(fluentSignature));
    }

    [Test]
    public void Production_NoHmacKey_FailsValidateOnStart()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddMillWorksAudit(static builder =>
        {
            builder.Options.Environment = "Production";
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
        });

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetRequiredService<IStartupValidator>();

        var ex = Assert.Throws<OptionsValidationException>(() => validator.Validate());
        Assert.That(ex!.Message, Does.Contain(nameof(AuditOptions.HmacKey)));
    }

    private static ServiceCollection BuildServices(
        IConfiguration? config,
        Action<MillWorksAuditBuilder>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config ?? new ConfigurationBuilder().Build());
        services.AddMillWorksAudit(builder =>
        {
            builder.Options.Environment = "Development";
            configure?.Invoke(builder);
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
        });
        return services;
    }

    private static async Task<string?> CaptureHmacSignatureAsync(
        IOptions<AuditOptions> auditOptions,
        AuditIntegrityDto dto)
    {
        var mockEventRepo = new Mock<IAuditEventRepository>();
        var mockIntegrityRepo = new Mock<IAuditIntegrityRepository>();
        var mockSecurityEventService = new Mock<IAuditSecurityEventService>();

        mockIntegrityRepo
            .Setup(static x => x.GetLatestBySequenceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditIntegrityEntity?)null);

        string? captured = null;
        mockIntegrityRepo
            .Setup(static x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Callback((AuditIntegrityEntity entity, CancellationToken _) => captured = entity.HmacSignature)
            .ReturnsAsync(static (AuditIntegrityEntity entity, CancellationToken _) => entity);

        mockIntegrityRepo
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        mockIntegrityRepo
            .Setup(static x => x.ClearChangeTrackerAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = new TamperDetectionService(
            mockEventRepo.Object,
            mockIntegrityRepo.Object,
            mockSecurityEventService.Object,
            NullLogger<TamperDetectionService>.Instance,
            auditOptions,
            Options.Create(new SecurityOptions()),
            new InMemoryDistributedLockService(NullLogger<InMemoryDistributedLockService>.Instance));

        await service.CreateIntegrityRecordAsync(dto);
        return captured;
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName)
        {
            EnvironmentName = environmentName;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "MillWorks.AuditCore.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
