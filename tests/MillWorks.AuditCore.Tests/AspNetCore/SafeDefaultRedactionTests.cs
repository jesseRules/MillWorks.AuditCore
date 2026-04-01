using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Tests.AspNetCore;

[TestFixture]
[Category("Unit")]
public sealed class SafeDefaultRedactionTests
{
    [Test]
    public void AddMillWorksAudit_DefaultRedactor_IsDefaultAuditFieldRedactor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillWorksAudit(builder =>
        {
            builder.Options.ApplicationName = "TestApp";
            builder.Options.Environment = "Development";
            builder.UseEntityFramework(ef => ef.ConnectionString = "Server=.;Database=Test;");
        });

        var redactorDescriptor = services.FirstOrDefault(
            s => s.ServiceType == typeof(IAuditFieldRedactor));

        Assert.That(redactorDescriptor, Is.Not.Null);
        Assert.That(redactorDescriptor!.ImplementationType, Is.EqualTo(typeof(DefaultAuditFieldRedactor)));
    }

    [Test]
    public void AddMillWorksAudit_CustomRedactorRegisteredFirst_IsNotOverwritten()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditFieldRedactor, PassThroughAuditFieldRedactor>();

        services.AddMillWorksAudit(builder =>
        {
            builder.Options.ApplicationName = "TestApp";
            builder.Options.Environment = "Development";
            builder.Options.AllowPassThroughRedactor = true;
            builder.UseEntityFramework(ef => ef.ConnectionString = "Server=.;Database=Test;");
        });

        var redactorDescriptor = services.FirstOrDefault(
            s => s.ServiceType == typeof(IAuditFieldRedactor));

        // TryAdd should not overwrite the consumer-registered PassThrough
        Assert.That(redactorDescriptor!.ImplementationType, Is.EqualTo(typeof(PassThroughAuditFieldRedactor)));
    }

    [Test]
    public void ValidateConfiguration_PassThroughInProduction_Throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditFieldRedactor, PassThroughAuditFieldRedactor>();

        Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddMillWorksAudit(builder =>
            {
                builder.Options.ApplicationName = "TestApp";
                builder.Options.Environment = "Production";
                builder.UseEntityFramework(ef => ef.ConnectionString = "Server=.;Database=Test;");
            });
        });
    }

    [Test]
    public void ValidateConfiguration_PassThroughInDevelopment_AlsoThrows()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditFieldRedactor, PassThroughAuditFieldRedactor>();

        Assert.Throws<InvalidOperationException>(() =>
        {
            services.AddMillWorksAudit(builder =>
            {
                builder.Options.ApplicationName = "TestApp";
                builder.Options.Environment = "Development";
                builder.UseEntityFramework(ef => ef.ConnectionString = "Server=.;Database=Test;");
            });
        });
    }

    [Test]
    public void ValidateConfiguration_PassThroughWithExplicitAllow_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditFieldRedactor, PassThroughAuditFieldRedactor>();

        Assert.DoesNotThrow(() =>
        {
            services.AddMillWorksAudit(builder =>
            {
                builder.Options.ApplicationName = "TestApp";
                builder.Options.Environment = "Production";
                builder.Options.AllowPassThroughRedactor = true;
                builder.UseEntityFramework(ef => ef.ConnectionString = "Server=.;Database=Test;");
            });
        });
    }

    [Test]
    public void ValidateConfiguration_DefaultRedactorInProduction_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // No custom redactor — DefaultAuditFieldRedactor will be registered
        Assert.DoesNotThrow(() =>
        {
            services.AddMillWorksAudit(builder =>
            {
                builder.Options.ApplicationName = "TestApp";
                builder.Options.Environment = "Production";
                builder.UseEntityFramework(ef => ef.ConnectionString = "Server=.;Database=Test;");
            });
        });
    }

    [Test]
    public void UseRedactor_ReplacesDefaultRedactor()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillWorksAudit(builder =>
        {
            builder.Options.ApplicationName = "TestApp";
            builder.Options.Environment = "Production";
            builder.UseRedactor<TestCustomRedactor>();
            builder.UseEntityFramework(ef => ef.ConnectionString = "Server=.;Database=Test;");
        });

        var redactorDescriptor = services.FirstOrDefault(
            s => s.ServiceType == typeof(IAuditFieldRedactor));

        Assert.That(redactorDescriptor!.ImplementationType, Is.EqualTo(typeof(TestCustomRedactor)));
    }

    [Test]
    public void AddMillWorksAudit_RegistersIAuditDiagnostics()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMillWorksAudit(builder =>
        {
            builder.Options.ApplicationName = "TestApp";
            builder.Options.Environment = "Development";
            builder.UseEntityFramework(ef => ef.ConnectionString = "Server=.;Database=Test;");
        });

        var diagnosticsDescriptor = services.FirstOrDefault(
            s => s.ServiceType == typeof(IAuditDiagnostics));

        Assert.That(diagnosticsDescriptor, Is.Not.Null);
        Assert.That(diagnosticsDescriptor!.Lifetime, Is.EqualTo(ServiceLifetime.Singleton));
    }

    private sealed class TestCustomRedactor : IAuditFieldRedactor
    {
        public Dictionary<string, object?> RedactFields(Dictionary<string, object?> fields) => fields;
        public string? RedactValue(string fieldName, string? value) => value;
    }
}
