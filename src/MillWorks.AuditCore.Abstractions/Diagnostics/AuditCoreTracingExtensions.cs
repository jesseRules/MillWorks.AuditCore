using OpenTelemetry.Trace;

namespace MillWorks.AuditCore.Abstractions.Diagnostics;

/// <summary>
/// Extension methods for registering AuditCore tracing with OpenTelemetry.
/// </summary>
public static class AuditCoreTracingExtensions
{
    /// <summary>
    /// Adds the AuditCore ActivitySource to the OpenTelemetry tracing pipeline.
    /// </summary>
    /// <param name="builder">The TracerProviderBuilder to configure.</param>
    /// <returns>The builder for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(t => t
    ///         .AddAuditCoreInstrumentation()
    ///         .AddJaegerExporter());
    /// </code>
    /// </example>
    public static TracerProviderBuilder AddAuditCoreInstrumentation(this TracerProviderBuilder builder)
        => builder.AddSource(AuditActivitySource.Name);
}
