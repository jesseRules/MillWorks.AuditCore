using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Interfaces;

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
        services.AddScoped(_ => Mock.Of<ILogger<AuditContextMiddleware>>());
        services.AddScoped<AuditContextMiddleware>();
        services.AddScoped(_ => Mock.Of<IAuditLogger>());

        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        app.UseMillWorksAudit();
        app.Run(async context =>
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
    public void UseMillWorksAudit_WithoutMiddlewareRegistration_FailsAtRequestTime()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var app = new ApplicationBuilder(services);

        app.UseMillWorksAudit();
        app.Run(_ => Task.CompletedTask);
        var pipeline = app.Build();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services
        };

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline(httpContext));

        Assert.That(ex!.Message, Does.Contain(nameof(IMiddlewareFactory)));
    }

    [Test]
    public void RunAuditMigrations_WithInvalidProvider_SurfacesUnderlyingError()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuditApplicationDbContext>(options =>
            options.UseInMemoryDatabase($"auditcore-appbuilder-{Guid.NewGuid():N}"));

        using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);

        var ex = Assert.Throws<InvalidOperationException>(() => app.RunAuditMigrations());

        Assert.That(ex!.Message, Does.Contain("Relational-specific methods"));
    }
}
