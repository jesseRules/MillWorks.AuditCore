using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Helpers;

/// <summary>
/// Subclass of <see cref="AuditLogger"/> that forwards LogAsync / LogBatchAsync to
/// an <see cref="IAuditLogger"/> delegate. Lets the existing Moq-based
/// <c>ResilientAuditLogger</c> tests observe calls that flow through scope-per-retry
/// by routing every per-scope resolution back to the shared mock.
/// Base-class ctor dependencies are satisfied with a per-instance in-memory
/// <see cref="AuditDbContext"/> so <see cref="AuditLogger"/> construction
/// doesn't null-deref on the non-nullable <c>dbContext</c> primary-constructor parameter,
/// even though the overridden methods never touch it.
/// </summary>
public sealed class ScopeFactoryForwardingAuditLogger : AuditLogger
{
    private readonly IAuditLogger _delegate;

    public ScopeFactoryForwardingAuditLogger(IAuditLogger target)
        : base(
            logger: NullLogger<AuditLogger>.Instance,
            eventFactory: Mock.Of<IAuditEventFactory>(),
            auditEventRepository: Mock.Of<IAuditEventRepository>(),
            dbContext: new AuditDbContext(
                new DbContextOptionsBuilder<AuditDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options),
            auditContext: Mock.Of<IAuditContext>(),
            fieldRedactor: Mock.Of<IAuditFieldRedactor>())
    {
        _delegate = target;
    }

    public override Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        => _delegate.LogAsync(auditEvent, cancellationToken);

    public override Task<BatchAuditResult> LogBatchAsync(
        IReadOnlyList<AuditEvent> auditEvents,
        CancellationToken cancellationToken = default)
        => _delegate.LogBatchAsync(auditEvents, cancellationToken);

    /// <summary>
    /// Builds a <see cref="ServiceProvider"/> whose <see cref="IServiceScopeFactory"/>
    /// returns scopes resolving a fresh <see cref="ScopeFactoryForwardingAuditLogger"/>
    /// that delegates to <paramref name="target"/>. Caller disposes the returned provider.
    /// </summary>
    public static ServiceProvider BuildProviderForwarding(IAuditLogger target)
    {
        var services = new ServiceCollection();
        services.AddScoped<AuditLogger>(_ => new ScopeFactoryForwardingAuditLogger(target));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
