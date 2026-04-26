using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Diagnostics;

namespace MillWorks.AuditCore.Tests.AspNetCore;

[TestFixture]
[Category("Unit")]
public sealed class IntegrityHealthCheckTests
{
    [Test]
    public async Task CheckHealthAsync_WithNoWorkItems_ReturnsHealthyWithActionableData()
    {
        using var provider = BuildProvider();
        var healthCheck = new IntegrityHealthCheck(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
        Assert.That(result.Data["pending_total"], Is.EqualTo(0));
        Assert.That(result.Data["failed"], Is.EqualTo(0));
        Assert.That(result.Data.ContainsKey("checked_at_utc"), Is.True);
        Assert.That(result.Data.ContainsKey("stale_threshold_minutes"), Is.True);
    }

    [Test]
    public async Task CheckHealthAsync_WithFailedItems_ReturnsUnhealthy()
    {
        using var provider = BuildProvider(static db =>
        {
            db.IntegrityWorkItems.Add(new AuditIntegrityWorkItemEntity
            {
                EventId = Guid.NewGuid(),
                Status = IntegrityStatus.Failed
            });
        });

        var healthCheck = new IntegrityHealthCheck(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
        Assert.That(result.Description, Does.Contain("permanently failed"));
        Assert.That(result.Data["failed"], Is.EqualTo(1));
    }

    [Test]
    public async Task CheckHealthAsync_WithManyStalePendingItems_ReturnsDegraded()
    {
        using var provider = BuildProvider(db =>
        {
            for (var i = 0; i < 11; i++)
            {
                db.IntegrityWorkItems.Add(new AuditIntegrityWorkItemEntity
                {
                    EventId = Guid.NewGuid(),
                    Status = IntegrityStatus.Pending,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20)
                });
            }
        });

        var diagnostics = new AuditDiagnostics();
        diagnostics.Increment(AuditDiagnosticCounter.IntegrityBatchFlush);
        diagnostics.Increment(AuditDiagnosticCounter.IntegrityReconciliationSuccess);

        var healthCheck = new IntegrityHealthCheck(
            provider.GetRequiredService<IServiceScopeFactory>(),
            diagnostics);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Degraded));
        Assert.That(result.Data["pending_stale"], Is.EqualTo(11));
        Assert.That(result.Data["batch_flush_count"], Is.EqualTo(1L));
        Assert.That(result.Data["reconciliation_successes"], Is.EqualTo(1L));
    }

    [Test]
    public void CheckHealthAsync_WithCanceledToken_PropagatesCancellation()
    {
        using var provider = BuildProvider();
        var healthCheck = new IntegrityHealthCheck(provider.GetRequiredService<IServiceScopeFactory>());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await healthCheck.CheckHealthAsync(new HealthCheckContext(), cts.Token));
    }

    [Test]
    public async Task CheckHealthAsync_WhenResolutionFails_ReturnsSanitizedUnhealthyResult()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var scope = new Mock<IServiceScope>();
        var services = new Mock<IServiceProvider>();

        scope.Setup(x => x.ServiceProvider).Returns(services.Object);
        scopeFactory.Setup(x => x.CreateScope()).Returns(scope.Object);
        services.Setup(x => x.GetService(typeof(AuditDbContext)))
            .Throws(new InvalidOperationException("Server=prod;Password=secret"));

        var healthCheck = new IntegrityHealthCheck(scopeFactory.Object);

        var result = await healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
        Assert.That(result.Description, Is.EqualTo("Failed to query integrity work item status."));
        Assert.That(result.Exception, Is.Null);
        Assert.That(result.Data["error_type"], Is.EqualTo("InvalidOperationException"));
        Assert.That(result.Description, Does.Not.Contain("Password"));
    }

    [Test]
    public async Task CheckHealthAsync_ConcurrentCalls_ReturnConsistentResults()
    {
        using var provider = BuildProvider(static db =>
        {
            db.IntegrityWorkItems.Add(new AuditIntegrityWorkItemEntity
            {
                EventId = Guid.NewGuid(),
                Status = IntegrityStatus.Pending
            });
        });

        var healthCheck = new IntegrityHealthCheck(provider.GetRequiredService<IServiceScopeFactory>());

        var results = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => healthCheck.CheckHealthAsync(new HealthCheckContext())));

        Assert.That(results.All(r => r.Status == HealthStatus.Healthy), Is.True);
        Assert.That(results.All(r => Equals(r.Data["pending_total"], 1)), Is.True);
    }

    private static ServiceProvider BuildProvider(Action<AuditDbContext>? seed = null)
    {
        var services = new ServiceCollection();
        var dbName = $"IntegrityHealth_{Guid.NewGuid()}";

        services.AddDbContext<AuditDbContext>(options =>
            options.UseInMemoryDatabase(dbName));

        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        seed?.Invoke(db);
        db.SaveChanges();

        return provider;
    }
}
