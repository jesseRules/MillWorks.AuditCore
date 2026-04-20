using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.AspNetCore.Services;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Mapping;
using MillWorks.AuditCore.Services.Query;
using Mapster;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Full DI container integration tests that build a real ServiceProvider with SQLite,
/// mirroring the UseEntityFramework() registrations. Verifies all services resolve and work end-to-end.
/// </summary>
[TestFixture]
[Category("Integration")]
public class FullPipelineIntegrationTests : IDisposable
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        // Infrastructure
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging();
        services.AddHttpContextAccessor();

        // EF options
        services.AddSingleton(new EntityFrameworkOptions());

        // Mapster (same as builder's ConfigureMapster)
        var typeAdapterConfig = new TypeAdapterConfig();
        typeAdapterConfig.Apply(new AuditMappingConfiguration());
        services.AddSingleton(typeAdapterConfig);
        services.AddMapster();

        // Interceptor
        services.AddSingleton<AuditSaveChangesInterceptor>();

        // DbContext with SQLite directly — avoids the dual-provider issue
        services.AddDbContext<AuditApplicationDbContext>((sp, options) =>
        {
            options.UseSqlite(_connection);
            options.ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

            var interceptor = sp.GetRequiredService<AuditSaveChangesInterceptor>();
            options.AddInterceptors(interceptor);
        });

        // Core infrastructure (same as AddMillWorksAudit + TryAddScoped in builder)
        services.AddScoped<IAuditContext, AuditContext>();
        services.AddScoped<IAuditEventFactory, AuditEventFactory>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddSingleton<IAuditFieldRedactor, PassThroughAuditFieldRedactor>();
        services.AddScoped<AuditContextMiddleware>();

        // Repositories (same as UseEntityFramework)
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IAuditIntegrityRepository, AuditIntegrityRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IArchiveRecordRepository, ArchiveRecordRepository>();
        services.AddScoped<ISecurityEventRepository, SecurityEventRepository>();
        services.AddScoped<InternalAuditEventRepository>();

        // Services (same as UseEntityFramework)
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IAuditSearchService, AuditSearchService>();
        services.AddScoped<IAuditReportService, AuditReportService>();
        services.AddScoped<IAuditArchivalService, AuditArchivalService>();
        services.AddScoped<IAuditMaintenanceService, MillWorks.AuditCore.Services.Maintenance.AuditMaintenanceService>();
        services.AddScoped<IAuditMetaTrackingService, AuditMetaTrackingService>();

        _provider = services.BuildServiceProvider();

        // Create schema
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditApplicationDbContext>();
        context.Database.EnsureCreated();
    }

    [Test]
    public void AllCoreServices_AreResolvable()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        Assert.Multiple(() =>
        {
            Assert.That(sp.GetService<IAuditService>(), Is.Not.Null, nameof(IAuditService));
            Assert.That(sp.GetService<IAuditQueryService>(), Is.Not.Null, nameof(IAuditQueryService));
            Assert.That(sp.GetService<IAuditSearchService>(), Is.Not.Null, nameof(IAuditSearchService));
            Assert.That(sp.GetService<IAuditReportService>(), Is.Not.Null, nameof(IAuditReportService));
            Assert.That(sp.GetService<IAuditArchivalService>(), Is.Not.Null, nameof(IAuditArchivalService));
            Assert.That(sp.GetService<IAuditMaintenanceService>(), Is.Not.Null, nameof(IAuditMaintenanceService));
            Assert.That(sp.GetService<IAuditMetaTrackingService>(), Is.Not.Null, nameof(IAuditMetaTrackingService));
            Assert.That(sp.GetService<IAuditLogger>(), Is.Not.Null, nameof(IAuditLogger));
        });
    }

    [Test]
    public async Task AuditService_CanCreateAndRetrieveEvent()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var service = sp.GetRequiredService<IAuditService>();
        var context = sp.GetRequiredService<AuditApplicationDbContext>();

        var eventId = Guid.NewGuid();
        context.AuditEvents.Add(new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Pipeline.Test",
            User = "pipeline@test.com",
            EntityType = "TestEntity",
            EntityId = "42",
            Environment = "Integration",
            InsertedDate = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var dto = await service.GetAuditEventById(eventId);

        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.EventType, Is.EqualTo("Pipeline.Test"));
        Assert.That(dto.User, Is.EqualTo("pipeline@test.com"));
    }

    [Test]
    public async Task AuditQueryService_CanPaginateEvents()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var queryService = sp.GetRequiredService<IAuditQueryService>();
        var context = sp.GetRequiredService<AuditApplicationDbContext>();

        for (int i = 0; i < 15; i++)
        {
            context.AuditEvents.Add(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Pagination.Test{i}",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }
        await context.SaveChangesAsync();

        var result = await queryService.GetAuditEventsAsync(offset: 0, limit: 10);

        Assert.That(result.Items, Has.Count.EqualTo(10));
        Assert.That(result.TotalItems, Is.EqualTo(15));
    }

    [Test]
    public void ScopedLifetime_DifferentScopesGetDifferentInstances()
    {
        using var scope1 = _provider.CreateScope();
        using var scope2 = _provider.CreateScope();

        var service1 = scope1.ServiceProvider.GetRequiredService<IAuditService>();
        var service2 = scope2.ServiceProvider.GetRequiredService<IAuditService>();

        Assert.That(service1, Is.Not.SameAs(service2));
    }

    [TearDown]
    public void CleanupData()
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditApplicationDbContext>();
        context.Database.ExecuteSqlRaw("DELETE FROM \"AuditEvents\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"AuditIntegrity\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"AuditLogs\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"ArchiveRecord\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"SecurityEvents\"");
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
    }
}
