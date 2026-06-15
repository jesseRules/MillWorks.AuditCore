using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.AspNetCore.Services;

/// <summary>
/// On application startup, checks if PassThroughAuditFieldRedactor is being used.
/// Throws in Production if not explicitly allowed; logs a warning otherwise.
/// This catches factory/instance registrations that bypass the DI-time check.
/// </summary>
/// <param name="serviceProvider"></param>
/// <param name="auditOptions"></param>
/// <param name="hostEnvironment"></param>
internal sealed class PassThroughRedactorStartupWarningService(
    IServiceProvider serviceProvider,
    IOptions<AuditOptions> auditOptions,
    IHostEnvironment? hostEnvironment = null) : IHostedService
{
    /// <summary>
    /// On application startup, checks if PassThroughAuditFieldRedactor is being used.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var redactor = serviceProvider.GetService(typeof(IAuditFieldRedactor));
        if (redactor is not PassThroughAuditFieldRedactor)
            return Task.CompletedTask;

        var options = auditOptions.Value;
        var isProduction = hostEnvironment?.IsProduction()
            ?? options.Environment.Equals("Production", StringComparison.OrdinalIgnoreCase);

        if (!options.AllowPassThroughRedactor)
        {
            // PassThrough without explicit opt-in is forbidden
            if (isProduction)
            {
                throw new InvalidOperationException(
                    "PassThroughAuditFieldRedactor is not permitted in Production. " +
                    "Register a custom IAuditFieldRedactor, or set AllowPassThroughRedactor = true " +
                    "to explicitly accept unredacted audit storage.");
            }

            throw new InvalidOperationException(
                $"PassThroughAuditFieldRedactor is not permitted (Environment: {options.Environment}). " +
                "Register a custom IAuditFieldRedactor, or set AllowPassThroughRedactor = true " +
                "to explicitly accept unredacted audit storage.");
        }

        // AllowPassThroughRedactor is true - log a warning
        var loggerFactory = serviceProvider.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        var logger = loggerFactory?.CreateLogger<PassThroughRedactorStartupWarningService>();
        logger?.LogWarning(
            "PassThroughAuditFieldRedactor is enabled with AllowPassThroughRedactor = true in environment {Environment}. Audit payloads may persist PHI, PII, or secrets without redaction.",
            options.Environment);

        return Task.CompletedTask;
    }

    /// <summary>
    /// No cleanup needed on shutdown, so this is a no-op.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
