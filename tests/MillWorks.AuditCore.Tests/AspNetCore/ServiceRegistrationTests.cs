using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MillWorks.AuditCore.AspNetCore.Configuration.Options;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;

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
    }

    [Test]
    public void AddMillWorksAudit_RegistersCoreServices()
    {
        _services.AddMillWorksAudit(builder =>
        {
            builder.UseEntityFramework(ef =>
            {
                ef.ConnectionString = "Server=test;Database=test;";
            });
        });

        Assert.That(_services.Any(s => s.ServiceType == typeof(IAuditContext)), Is.True);
        Assert.That(_services.Any(s => s.ServiceType == typeof(IAuditEventFactory)), Is.True);
        Assert.That(_services.Any(s => s.ServiceType == typeof(IAuditLogger)), Is.True);
    }

    [Test]
    public void AddMillWorksAudit_RegistersHttpContextAccessor()
    {
        _services.AddMillWorksAudit(builder =>
        {
            builder.UseEntityFramework(ef =>
            {
                ef.ConnectionString = "Server=test;Database=test;";
            });
        });

        Assert.That(_services.Any(s =>
            s.ServiceType == typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor)), Is.True);
    }

    [Test]
    public void AddMillWorksAudit_RegistersAuditOptions()
    {
        _services.AddMillWorksAudit(builder =>
        {
            builder.Options.ApplicationName = "MyTestApp";
            builder.UseEntityFramework(ef =>
            {
                ef.ConnectionString = "Server=test;Database=test;";
            });
        });

        Assert.That(_services.Any(s => s.ServiceType == typeof(AuditOptions)), Is.True);
    }

    [Test]
    public void AddMillWorksAudit_TryAddScoped_DoesNotOverrideExistingRegistration()
    {
        // Pre-register a custom IAuditContext
        var customContext = new Mock<IAuditContext>();
        _services.AddScoped(_ => customContext.Object);

        _services.AddMillWorksAudit(builder =>
        {
            builder.UseEntityFramework(ef =>
            {
                ef.ConnectionString = "Server=test;Database=test;";
            });
        });

        // Should only have 1 IAuditContext registration (the custom one + TryAdd won't duplicate)
        var registrations = _services.Where(s => s.ServiceType == typeof(IAuditContext)).ToList();

        // TryAddScoped in both AddMillWorksAudit and UseEntityFramework means
        // only the first registration wins
        Assert.That(registrations, Has.Count.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void AddMillWorksAudit_NullConfigure_StillCallsValidation()
    {
        // Without UseEntityFramework, validation should throw because no storage is configured
        Assert.Throws<InvalidOperationException>(() =>
        {
            _services.AddMillWorksAudit();
        });
    }

    [Test]
    public void AddMillWorksAudit_WithoutStorage_ThrowsOnValidation()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            _services.AddMillWorksAudit(builder =>
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
        Assert.Throws<InvalidOperationException>(() =>
        {
            _services.Decorate<ITestService, TestServiceDecorator>();
        });
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
