using Microsoft.Extensions.Hosting;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.AspNetCore.Services;

/// <summary>
/// Freezes the <see cref="AuditProviderTypeMap"/> on startup to prevent
/// post-startup modifications and enable lock-free concurrent reads.
/// </summary>
internal sealed class AuditProviderTypeMapFreezeService(AuditProviderTypeMap? typeMap) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        typeMap?.Freeze();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
