using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.Tests.AspNetCore;

[TestFixture]
[Category("Unit")]
public sealed class ApplicationBuilderExtensionsTests
{
    [Test]
    public async Task UseMillWorksAudit_AddsMiddlewareToPipeline()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTransient<IMiddlewareFactory, MiddlewareFactory>();
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped(static _ => Mock.Of<ILogger<AuditContextMiddleware>>());
        services.AddScoped(static _ => Mock.Of<IAuditEventFactory>(static factory =>
            factory.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()) ==
            new AuditEvent { EventType = "Http.GET" }));
        services.AddScoped(static _ => Mock.Of<IRequestAuditDispatcher>());
        services.AddSingleton(Options.Create(new AuditMiddlewareOptions()));
        services.AddScoped<AuditContextMiddleware>();

        await using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        app.UseMillWorksAudit();
        app.Run(static async context =>
        {
            var auditContext = context.RequestServices.GetRequiredService<IAuditContext>();
            await context.Response.WriteAsync(auditContext.CorrelationId ?? string.Empty);
        });

        var pipeline = app.Build();

        using var scope = provider.CreateScope();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            TraceIdentifier = "pipeline-correlation"
        };
        httpContext.Response.Body = new MemoryStream();

        await pipeline(httpContext);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.That(body, Is.EqualTo("pipeline-correlation"));
    }

    [Test]
    public async Task UseMillWorksAudit_WithoutMiddlewareRegistration_FailsAtRequestTime()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);

        app.UseMillWorksAudit();
        app.Run(static _ => Task.CompletedTask);
        var pipeline = app.Build();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline(httpContext));

        Assert.That(ex!.Message, Does.Contain(nameof(IMiddlewareFactory)));
    }

    [Test]
    public void RunAuditMigrations_WithInvalidProvider_SurfacesUnderlyingError()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuditDbContext>(static options =>
            options.UseInMemoryDatabase($"auditcore-appbuilder-{Guid.NewGuid():N}"));

        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        var ex = Assert.Throws<InvalidOperationException>(() => app.RunAuditMigrations());

        Assert.That(ex!.Message, Does.Contain("Relational-specific methods"));
    }
}
