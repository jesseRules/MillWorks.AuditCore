using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.AspNetCore.Services;

internal sealed class PassThroughRedactorStartupWarningService(
    IServiceProvider serviceProvider,
    IOptions<AuditOptions> auditOptions) : IHostedService
{
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
