using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.AspNetCore.Services;

/// <summary>
/// On application startup, checks if PassThroughAuditFieldRedactor is being used and logs a warning if so.
/// </summary>
/// <param name="serviceProvider"></param>
/// <param name="auditOptions"></param>
internal sealed class PassThroughRedactorStartupWarningService(
    IServiceProvider serviceProvider,
    IOptions<AuditOptions> auditOptions) : IHostedService
{
    /// <summary>
    /// On application startup, checks if PassThroughAuditFieldRedactor is being used and logs a warning if so.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var redactor = serviceProvider.GetService(typeof(IAuditFieldRedactor));
        if (redactor is not PassThroughAuditFieldRedactor || !auditOptions.Value.AllowPassThroughRedactor)
            return Task.CompletedTask;

        var loggerFactory = serviceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        var logger = loggerFactory?.CreateLogger<PassThroughRedactorStartupWarningService>();
        logger?.LogWarning(
            "PassThroughAuditFieldRedactor is enabled with AllowPassThroughRedactor = true in environment {Environment}. Audit payloads may persist PHI, PII, or secrets without redaction.",
            auditOptions.Value.Environment);

        return Task.CompletedTask;
    }

    /// <summary>
    /// No cleanup needed on shutdown, so this is a no-op.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
