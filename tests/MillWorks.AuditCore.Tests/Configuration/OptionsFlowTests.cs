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
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.Cryptography.Signing;

namespace MillWorks.AuditCore.Tests.Configuration;

/// <summary>
/// Phase 1 acceptance tests for the options flow contract: fluent builder values must
/// reach IOptions&lt;AuditOptions&gt;, IConfiguration binding must survive as a fallback when the
/// consumer does not fluent-set a property, fluent wins where explicitly set, and Production without
/// a configured integrity master key must fail closed when the signing-key backend is built.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class OptionsFlowTests
{
    // The integrity HMAC key no longer flows through AuditOptions (it resolves via the integrity
    // ISigningKeyProvider). These tests exercise the same fluent/config overlay contract on a still-
    // existing string option, ApplicationName.
    [Test]
    public void FluentApplicationName_FlowsThroughOptionsPipeline()
    {
        const string fluentName = "FluentAuditApp";

        var services = BuildServices(
            config: null,
            configure: static builder =>
            {
                builder.Options.ApplicationName = fluentName;
                builder.Options.EnableDigitalSignatures = true;
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.ApplicationName, Is.EqualTo(fluentName));
        Assert.That(options.EnableDigitalSignatures, Is.True);
    }

    [Test]
    public void ConfigurationBinding_FallbackResolves()
    {
        const string configName = "ConfigAuditApp";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:ApplicationName"] = configName,
                ["Audit:EnableDigitalSignatures"] = "true"
            })
            .Build();

        var services = BuildServices(config: config, configure: null);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.ApplicationName, Is.EqualTo(configName));
        Assert.That(options.EnableDigitalSignatures, Is.True);
    }

    [Test]
    public void FluentConfigureOverridesBindConfiguration()
    {
        const string configName = "ConfigAuditApp";
        const string fluentName = "FluentAuditApp";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:ApplicationName"] = configName
            })
            .Build();

        var services = BuildServices(
            config: config,
            configure: static builder => builder.Options.ApplicationName = fluentName);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.ApplicationName, Is.EqualTo(fluentName));
    }

    [Test]
    public void FluentFailureMode_FlowsThroughOptionsPipeline()
    {
        var services = BuildServices(
            config: null,
            configure: static builder => builder.Options.FailureMode = AuditFailureMode.FailClosedForRegulated);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.FailureMode, Is.EqualTo(AuditFailureMode.FailClosedForRegulated));
    }

    [Test]
    public void ConfigurationFailureMode_FallbackResolves()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:FailureMode"] = nameof(AuditFailureMode.FailClosedAlways)
            })
            .Build();

        var services = BuildServices(config: config, configure: null);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.FailureMode, Is.EqualTo(AuditFailureMode.FailClosedAlways));
    }

    [Test]
    public void FluentDefaultCustomFields_FlowThroughOptionsPipeline()
    {
        // The natural way to add defaults is mutating the dictionary via its indexer,
        // which goes through the getter and never trips the property setter. This must
        // still reach IOptions<AuditOptions>.Value (regression: the prior merge gated on
        // ExplicitlySetProperties, which only the setter populates, so these were dropped).
        var services = BuildServices(
            config: null,
            configure: static builder =>
            {
                builder.Options.DefaultCustomFields["TenantTag"] = "north";
                builder.Options.DefaultCustomFields["ComplianceTag"] = "HIPAA";
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.DefaultCustomFields["TenantTag"], Is.EqualTo("north"));
        Assert.That(options.DefaultCustomFields["ComplianceTag"], Is.EqualTo("HIPAA"));
    }

    [Test]
    public void AddDefaultCustomField_FlowsThroughOptionsPipeline()
    {
        var services = BuildServices(
            config: null,
            configure: static builder =>
            {
                builder.AddDefaultCustomField("TenantTag", "north");
                builder.AddDefaultCustomField("ComplianceTag", "HIPAA");
            });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.DefaultCustomFields["TenantTag"], Is.EqualTo("north"));
        Assert.That(options.DefaultCustomFields["ComplianceTag"], Is.EqualTo("HIPAA"));
    }

    [Test]
    public void DefaultCustomFields_ConfigAndFluentCoexist()
    {
        // Config-bound defaults form the base; builder-provided fields overlay per key.
        // Setting a field in code must not wipe defaults supplied via configuration.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:DefaultCustomFields:FromConfig"] = "config-value"
            })
            .Build();

        var services = BuildServices(
            config: config,
            configure: static builder => builder.Options.DefaultCustomFields["FromCode"] = "code-value");

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(options.DefaultCustomFields["FromConfig"], Is.EqualTo("config-value"));
        Assert.That(options.DefaultCustomFields["FromCode"], Is.EqualTo("code-value"));
    }

    [Test]
    public void Production_NoIntegrityMasterKey_FailsWhenSignerResolved()
    {
        // The old "HmacKey required in Production" rule moved: integrity keys now resolve via the
        // file-system ISigningKeyProvider, which fails closed in Production when no at-rest master key
        // is configured. The failure surfaces when the signer is resolved (its key backend is built).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment("Production"));
        services.AddMillWorksAudit(static builder =>
        {
            builder.Options.Environment = "Production";
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
            builder.UseSecurity(static _ => { /* no IntegrityKeyMasterKeyBase64 */ });
        });

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<HmacSha256Signer>());
        Assert.That(ex!.Message, Does.Contain(nameof(SecurityOptions.IntegrityKeyMasterKeyBase64)));
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
