using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Providers.Base;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Dispatches audit events to registered providers after entity changes are saved.
/// Resolved as scoped — the interceptor obtains it from ScopedServiceProvider on the DbContext.
/// </summary>
public sealed class AuditProviderDispatcher(
    AuditProviderTypeMap map,
    IServiceProvider serviceProvider,
    IAuditLogger logger,
    ILogger<AuditProviderDispatcher> log)
    : IAuditProviderDispatcher
{
    /// <inheritdoc />
    public async Task DispatchAsync(IReadOnlyList<PendingProviderDispatch> dispatches, CancellationToken cancellationToken)
    {
        foreach (var dispatch in dispatches)
        {
            try
            {
                var providerType = map.GetProviderType(dispatch.EntityTypeName);
                if (providerType == null) continue;

                var provider = (IAuditProvider)serviceProvider.GetRequiredService(providerType);

                if (!await provider.ShouldAuditAsync(dispatch.Action, dispatch.Entity))
                    continue;

                var auditEvent = await provider.CreateAuditEventAsync(
                    dispatch.Action, dispatch.Entity, dispatch.OldValues);

                await logger.LogAsync(auditEvent, cancellationToken);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to dispatch provider for {EntityType}.{Action}",
                    dispatch.EntityTypeName, dispatch.Action);
                // Non-fatal — the AuditLogEntity was already saved by ProcessAuditableEntries
            }
        }
    }
}
