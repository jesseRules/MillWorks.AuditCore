using Microsoft.Extensions.Hosting;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.AspNetCore.Services;

/// <summary>
/// Wrapper that delegates IHostedService to InProcessRequestAuditDispatcher.
/// Registered by ImplementationType so UseRequestAuditDispatcher can target it for removal
/// without affecting other hosted services (factory-based registrations don't set ImplementationType).
/// </summary>
public sealed class InProcessRequestAuditDispatcherHostedService(
    InProcessRequestAuditDispatcher dispatcher) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => dispatcher.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => dispatcher.StopAsync(cancellationToken);
}
