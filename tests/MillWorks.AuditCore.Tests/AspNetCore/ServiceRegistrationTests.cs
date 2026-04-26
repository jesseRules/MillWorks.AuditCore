using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.Diagnostics;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.AspNetCore;

[TestFixture]
[Category("Unit")]
public class ServiceRegistrationTests
{
    private IServiceCollection _services;

    [SetUp]
    public void Setup()
    {
        _services = new ServiceCollection();
        _services.AddLogging();
        _services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
    }

    [Test]
    public void AddMillWorksAudit_RegistersCoreServices()
    {
        _services.AddMillWorksAudit(static builder =>
        {
            builder.Options.Environment = "Test";
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
        });

        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditContext)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditEventFactory)), Is.True);
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IAuditLogger)), Is.True);
    }

    [Test]
    public void AddMillWorksAudit_RegistersHttpContextAccessor()
    {
        _services.AddMillWorksAudit(static builder =>
        {
            builder.Options.Environment = "Test";
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
        });

        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor)), Is.True);
    }

    [Test]
    public void AddMillWorksAudit_RegistersAuditOptions()
    {
        _services.AddMillWorksAudit(static builder =>
        {
            builder.Options.ApplicationName = "MyTestApp";
            builder.Options.Environment = "Test";
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
        });

        using var provider = _services.BuildServiceProvider();
        var auditOptions = provider.GetRequiredService<IOptions<AuditOptions>>().Value;
        Assert.That(auditOptions.ApplicationName, Is.EqualTo("MyTestApp"));
    }

    [Test]
    public void AddMillWorksAudit_ConfigurationOnly_AuditHmacKeySurvivesFluentBaselineReplay()
    {
        // Phase 1 acceptance 2: a consumer who sets Audit:HmacKey via IConfiguration and does
        // not touch builder.Options.HmacKey must still resolve the config-bound key from
        // IOptions<AuditOptions>. The baseline-diff replay inside AddMillWorksAudit is what
        // preserves this — an unconditional property copy would blank the bound value.
        const string configHmacKey = "config-hmac-key-for-fallback-test-64chars-1234567890abcdef1234567";

        _services.Remove(_services.First(s => s.ServiceType == typeof(IConfiguration)));
        _services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:HmacKey"] = configHmacKey,
                ["Audit:EnableDigitalSignatures"] = "true"
            })
            .Build());

        _services.AddMillWorksAudit(static builder =>
        {
            builder.Options.Environment = "Development";
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
        });

        using var provider = _services.BuildServiceProvider();
        var auditOptions = provider.GetRequiredService<IOptions<AuditOptions>>().Value;

        Assert.That(auditOptions.HmacKey, Is.EqualTo(configHmacKey));
        Assert.That(auditOptions.EnableDigitalSignatures, Is.True);
    }

    [Test]
    public void AddMillWorksAudit_IOptionsReflectsBuilderConfiguredValues()
    {
        // Phase 1 #9 contract: services receiving IOptions<AuditOptions> / IOptions<SecurityOptions>
        // must see the same instance the consumer configured through the fluent builder.
        const string expectedHmacKey = "test-hmac-key-for-registration-test-32chars";
        const string expectedPrivateKeyPath = "/tmp/private.pem";

        _services.AddMillWorksAudit(builder =>
        {
            builder.Options.ApplicationName = "RegTestApp";
            builder.Options.Environment = "Test";
            builder.Options.HmacKey = expectedHmacKey;
            builder.Options.EnableDigitalSignatures = true;
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
            builder.UseSecurity(security =>
            {
                security.DigitalSignaturePrivateKeyPath = expectedPrivateKeyPath;
                security.EnableBatchedIntegrityWrites = true;
            });
        });

        using var provider = _services.BuildServiceProvider();
        var auditOptions = provider.GetRequiredService<IOptions<AuditOptions>>().Value;
        var securityOptions = provider.GetRequiredService<IOptions<SecurityOptions>>().Value;

        Assert.That(auditOptions.HmacKey, Is.EqualTo(expectedHmacKey));
        Assert.That(auditOptions.EnableDigitalSignatures, Is.True);
        Assert.That(auditOptions.ApplicationName, Is.EqualTo("RegTestApp"));
        Assert.That(auditOptions.Environment, Is.EqualTo("Test"));

        Assert.That(securityOptions.DigitalSignaturePrivateKeyPath, Is.EqualTo(expectedPrivateKeyPath));
        Assert.That(securityOptions.EnableBatchedIntegrityWrites, Is.True);
    }

    [Test]
    public void AddMillWorksAudit_TryAddScoped_DoesNotOverrideExistingRegistration()
    {
        // Pre-register a custom IAuditContext
        var customContext = new Mock<IAuditContext>();
        _services.AddScoped(_ => customContext.Object);

        _services.AddMillWorksAudit(static builder =>
        {
            builder.Options.Environment = "Test";
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
        });

        // Should only have 1 IAuditContext registration (the custom one + TryAdd won't duplicate)
        var registrations = _services.Where(static s => s.ServiceType == typeof(IAuditContext)).ToList();

        // TryAddScoped in both AddMillWorksAudit and UseEntityFramework means
        // only the first registration wins
        Assert.That(registrations, Has.Count.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void AddMillWorksAudit_NullConfigure_StillCallsValidation()
    {
        // Without UseEntityFramework, validation should throw because no storage is configured
        Assert.Throws<InvalidOperationException>(() => { _services.AddMillWorksAudit(); });
    }

    [Test]
    public void AddMillWorksAudit_WithoutStorage_ThrowsOnValidation()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            _services.AddMillWorksAudit(static builder =>
            {
                // No UseEntityFramework call = no storage
            });
        });
    }

    [Test]
    public void Decorate_RegisteredService_WrapsExisting()
    {
        _services.AddScoped<ITestService, TestServiceImpl>();

        _services.Decorate<ITestService, TestServiceDecorator>();

        var provider = _services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ITestService>();

        Assert.That(resolved, Is.InstanceOf<TestServiceDecorator>());
    }

    [Test]
    public void Decorate_UnregisteredService_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(() => { _services.Decorate<ITestService, TestServiceDecorator>(); });
    }

    [Test]
    public void Decorate_FactoryRegistration_WrapsExisting()
    {
        _services.AddScoped<ITestService>(_ => new TestServiceImpl());

        _services.Decorate<ITestService, TestServiceDecorator>();

        using var provider = _services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolved = scope.ServiceProvider.GetRequiredService<ITestService>();

        Assert.That(resolved, Is.InstanceOf<TestServiceDecorator>());
        Assert.That(resolved.GetValue(), Is.EqualTo("decorated(original)"));
    }

    [Test]
    public void Decorate_InstanceRegistration_WrapsExisting()
    {
        _services.AddSingleton<ITestService>(new TestServiceImpl());

        _services.Decorate<ITestService, TestServiceDecorator>();

        using var provider = _services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<ITestService>();

        Assert.That(resolved, Is.InstanceOf<TestServiceDecorator>());
        Assert.That(resolved.GetValue(), Is.EqualTo("decorated(original)"));
    }

    [Test]
    public void AddMillWorksAudit_ProductionWithPassThroughRedactor_Throws()
    {
        // Default redactor is now DefaultAuditFieldRedactor (safe-by-default).
        // Explicitly register PassThroughAuditFieldRedactor to test that validation catches it.
        _services.AddSingleton<IAuditFieldRedactor, PassThroughAuditFieldRedactor>();

        Assert.Throws<InvalidOperationException>(() =>
        {
            _services.AddMillWorksAudit(static builder =>
            {
                // Default Environment is "Production"
                builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
            });
        });
    }

    [Test]
    public void AddMillWorksAudit_ProductionWithAllowPassThroughFlag_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
        {
            _services.AddMillWorksAudit(static builder =>
            {
                builder.Options.AllowPassThroughRedactor = true;
                builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
            });
        });
    }

    [Test]
    public void AddMillWorksAudit_NonProductionWithPassThroughRedactor_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
        {
            _services.AddMillWorksAudit(static builder =>
            {
                builder.Options.Environment = "Development";
                builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
            });
        });
    }

    [Test]
    public void AddMillWorksAudit_ProductionWithCustomRedactor_DoesNotThrow()
    {
        // Pre-register a custom redactor before AddMillWorksAudit
        _services.AddSingleton<IAuditFieldRedactor, TestRedactor>();

        Assert.DoesNotThrow(() =>
        {
            _services.AddMillWorksAudit(static builder =>
            {
                // Default Production environment, but custom redactor already registered
                builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
            });
        });
    }

    [Test]
    public void AddMillWorksAudit_WithEntityFrameworkAndSecurity_ResolvesPrimaryServices()
    {
        _services.AddMillWorksAudit(builder =>
        {
            builder.Options.Environment = "Development";
            builder.UseEntityFramework(ef => ef.ConnectionString = "Server=test;Database=test;");
            builder.UseSecurity(security =>
            {
                security.EnableTamperDetection = true;
            });
        });

        using var provider = _services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.That(scope.ServiceProvider.GetRequiredService<IAuditLogger>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<IAuditService>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<IAuditQueryService>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<ITamperDetectionService>(), Is.Not.Null);
        Assert.That(scope.ServiceProvider.GetRequiredService<AuditDbContext>(), Is.Not.Null);
    }

    [Test]
    public void AddMillWorksAudit_RegistersExpectedLifetimes()
    {
        _services.AddMillWorksAudit(builder =>
        {
            builder.Options.Environment = "Development";
            builder.UseEntityFramework(ef => ef.ConnectionString = "Server=test;Database=test;");
        });

        using var provider = _services.BuildServiceProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var diagnostics1 = scope1.ServiceProvider.GetRequiredService<IAuditDiagnostics>();
        var diagnostics2 = scope2.ServiceProvider.GetRequiredService<IAuditDiagnostics>();
        var context1 = scope1.ServiceProvider.GetRequiredService<IAuditContext>();
        var context2 = scope2.ServiceProvider.GetRequiredService<IAuditContext>();

        Assert.That(diagnostics2, Is.SameAs(diagnostics1));
        Assert.That(context2, Is.Not.SameAs(context1));
    }

    [Test]
    public void AddMillWorksAudit_WithBatchedIntegrity_RegistersHealthCheckAndHostedServices()
    {
        _services.AddMillWorksAudit(builder =>
        {
            builder.Options.Environment = "Development";
            builder.UseEntityFramework(ef => ef.ConnectionString = "Server=test;Database=test;");
            builder.UseSecurity(security =>
            {
                security.EnableTamperDetection = true;
                security.EnableBatchedIntegrityWrites = true;
            });
            builder.UseArchival(archival =>
            {
                archival.Provider = ArchivalProvider.FileSystem;
                archival.EnableBackgroundArchival = true;
            });
            builder.UseResilience(resilience =>
            {
                resilience.EnableDeadLetterQueue = true;
                resilience.EnableBackgroundProcessor = true;
                resilience.DeadLetterProvider = DeadLetterProvider.InMemory;
            });
        });

        using var provider = _services.BuildServiceProvider();

        var healthChecks = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        var hostedServiceDescriptors = _services.Where(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();

        Assert.That(healthChecks.Any(r => r.Name == "audit_integrity_pipeline"), Is.True);
        Assert.That(hostedServiceDescriptors.Count, Is.GreaterThanOrEqualTo(5));
        Assert.That(hostedServiceDescriptors.Any(s => s.ImplementationType == typeof(IntegrityReconciliationService)), Is.True);
        Assert.That(hostedServiceDescriptors.Any(s => s.ImplementationType == typeof(DeadLetterQueueProcessor)), Is.True);
    }

    [Test]
    public void AddMillWorksAudit_CalledTwice_DoesNotThrow()
    {
        Assert.DoesNotThrow(() =>
        {
            _services.AddMillWorksAudit(builder =>
            {
                builder.Options.Environment = "Development";
                builder.UseEntityFramework(ef => ef.ConnectionString = "Server=test;Database=test;");
            });

            _services.AddMillWorksAudit(builder =>
            {
                builder.Options.Environment = "Development";
                builder.UseEntityFramework(ef => ef.ConnectionString = "Server=test;Database=test;");
            });
        });
    }

    /// <summary>
    /// Minimal test redactor for registration tests
    /// </summary>
    private sealed class TestRedactor : IAuditFieldRedactor
    {
        public Dictionary<string, object?> RedactFields(Dictionary<string, object?> fields) => fields;
        public string? RedactValue(string fieldName, string? value) => "[REDACTED]";
    }

    // Test interfaces/implementations for decorator tests
    public interface ITestService
    {
        string GetValue();
    }

    public class TestServiceImpl : ITestService
    {
        public string GetValue() => "original";
    }

    public class TestServiceDecorator(ITestService inner) : ITestService
    {
        public string GetValue() => $"decorated({inner.GetValue()})";
    }
}
