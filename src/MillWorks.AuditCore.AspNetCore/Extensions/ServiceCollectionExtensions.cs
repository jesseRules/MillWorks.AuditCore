using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.AspNetCore.Configuration;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.AspNetCore.Services;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Diagnostics;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.AspNetCore.Extensions;

/// <summary>
/// ServiceCollectionExtensions provides extension methods for IServiceCollection to add MillWorks Audit services
/// with circular dependency prevention.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <param name="services">Service collection</param>
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds MillWorks Audit services to the service collection with circular dependency prevention.
        /// </summary>
        /// <param name="configure">Configuration action for the audit builder</param>
        /// <returns>Service collection for chaining</returns>
        public void AddMillWorksAudit(Action<MillWorksAuditBuilder>? configure = null)
        {
            // Register core services with proper lifetimes (TryAdd to prevent duplicates).
            // AuditLogger is registered as its concrete type so ResilientAuditLogger can
            // resolve a fresh instance per retry scope; IAuditLogger forwards to the same
            // AuditLogger within a scope so there's only one instance either way.
            services.TryAddScoped<IAuditContext, AuditContext>();
            services.TryAddScoped<IAuditEventFactory, AuditEventFactory>();
            services.TryAddScoped<AuditLogger>();
            services.TryAddScoped<IAuditLogger>(static sp => sp.GetRequiredService<AuditLogger>());
            services.TryAddScoped<IRequestAuditProcessor, RequestAuditProcessor>();

            // Aggregate diagnostic counters — singleton, thread-safe, queryable from health checks
            services.TryAddSingleton<IAuditDiagnostics, AuditDiagnostics>();

            // Redaction options — allows consumers to add CorrelationId/SessionId to safe fields
            // if their system guarantees these fields never contain PII.
            services.AddOptions<RedactionOptions>()
                .BindConfiguration("Audit:Redaction");

            // Safe-by-default redactor — masks all non-structural fields. Consumers can
            // register their own IAuditFieldRedactor before calling AddMillWorksAudit()
            // and TryAdd will not overwrite it. Use PassThroughAuditFieldRedactor only
            // when explicitly opting in via AllowPassThroughRedactor = true.
            services.TryAddSingleton<IAuditFieldRedactor, DefaultAuditFieldRedactor>();

            // Register HTTP context accessor for middleware
            services.AddHttpContextAccessor();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<AuditMiddlewareOptions>, AuditMiddlewareOptionsValidator>());

            services.AddOptions<AuditMiddlewareOptions>()
                .BindConfiguration("Audit")
                .ValidateOnStart();

            // Default deferred request-audit dispatcher. Consumers can replace IRequestAuditDispatcher
            // with an external implementation (for example, a Hangfire-like job bridge).
            // Registered as concrete type first, then forwarded to interfaces.
            services.TryAddSingleton<InProcessRequestAuditDispatcher>();
            services.TryAddSingleton<IRequestAuditDispatcher>(static sp =>
                sp.GetRequiredService<InProcessRequestAuditDispatcher>());
            // Wrapper class enables IHostedService to be registered with ImplementationType set,
            // allowing UseRequestAuditDispatcher to target it for removal by type. Factory-based
            // registrations in .NET 10 don't set ImplementationType, so the wrapper is necessary.
            services.TryAddSingleton<InProcessRequestAuditDispatcherHostedService>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton(
                typeof(IHostedService), typeof(InProcessRequestAuditDispatcherHostedService)));

            // Register middleware - CRITICAL: Must be scoped for per-request isolation
            services.TryAddScoped<AuditContextMiddleware>();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, PassThroughRedactorStartupWarningService>());
            // AuditProviderTypeMap may not be registered (RegisterProviders is optional).
            // The freeze service resolves it via IServiceProvider at startup to handle the optional case.
            services.TryAddEnumerable(ServiceDescriptor.Singleton(
                typeof(IHostedService), typeof(AuditProviderTypeMapFreezeService)));

            // Create and configure builder. AuditOptions.ExplicitlySetProperties tracks which
            // properties the consumer sets, letting IConfiguration binding persist for properties
            // that weren't fluent-set.
            var auditOptions = new AuditOptions();
            var builder = new MillWorksAuditBuilder(services, auditOptions);

            // Allow consumer to configure the builder
            configure?.Invoke(builder);

            // Validate configuration
            builder.ValidateConfiguration();

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<AuditOptions>, AuditOptionsValidator>());

            // Overlay fluent-set properties onto config-bound values. Uses ExplicitlySetProperties
            // to distinguish "set to default" from "not set", so setting Environment = "Production"
            // explicitly wins over config "Environment": "Development".
            services.AddOptions<AuditOptions>()
                .BindConfiguration("Audit")
                .Configure(opts =>
                {
                    var explicitlySet = auditOptions.ExplicitlySetProperties;

                    if (explicitlySet.Contains(nameof(AuditOptions.Enabled)))
                        opts.Enabled = auditOptions.Enabled;

                    if (explicitlySet.Contains(nameof(AuditOptions.ApplicationName)))
                        opts.ApplicationName = auditOptions.ApplicationName;

                    if (explicitlySet.Contains(nameof(AuditOptions.Environment)))
                        opts.Environment = auditOptions.Environment;

                    if (explicitlySet.Contains(nameof(AuditOptions.HmacKey)))
                        opts.HmacKey = auditOptions.HmacKey;

                    if (explicitlySet.Contains(nameof(AuditOptions.EnableDigitalSignatures)))
                        opts.EnableDigitalSignatures = auditOptions.EnableDigitalSignatures;

                    if (explicitlySet.Contains(nameof(AuditOptions.AllowPassThroughRedactor)))
                        opts.AllowPassThroughRedactor = auditOptions.AllowPassThroughRedactor;

                    if (explicitlySet.Contains(nameof(AuditOptions.FailureMode)))
                        opts.FailureMode = auditOptions.FailureMode;

                    // DefaultCustomFields overlays per key rather than replacing wholesale:
                    // config-bound defaults form the base, builder-provided fields overlay on
                    // top. Done unconditionally (not gated on ExplicitlySetProperties) because
                    // the natural way to add fields — builder.Options.DefaultCustomFields["x"] = y —
                    // mutates through the getter and never trips the setter that records the flag.
                    // An untouched dictionary is empty, so the overlay is a no-op when unused.
                    foreach (var field in auditOptions.DefaultCustomFields)
                        opts.DefaultCustomFields[field.Key] = field.Value;
                })
                .ValidateOnStart();
        }

        /// <summary>
        /// Decorates a registered service with a decorator implementation.
        /// Uses keyed services so the container manages the inner service's lifetime properly.
        /// </summary>
        public void Decorate<TInterface, TDecorator>()
            where TInterface : class
            where TDecorator : class, TInterface
        {
            var wrappedDescriptor = services.FirstOrDefault(static s => s.ServiceType == typeof(TInterface));

            if (wrappedDescriptor == null)
            {
                throw new InvalidOperationException(
                    $"Service {typeof(TInterface).Name} is not registered. " +
                    $"Register the service before calling Decorate.");
            }

            // Remove the original registration
            services.Remove(wrappedDescriptor);

            // Re-register the original as a keyed service so the container tracks its lifetime
            var innerKey = $"Decorator_Inner_{typeof(TInterface).FullName}";

            if (wrappedDescriptor.ImplementationFactory != null)
            {
                services.Add(new ServiceDescriptor(
                    typeof(TInterface), innerKey,
                    (sp, _) => wrappedDescriptor.ImplementationFactory(sp),
                    wrappedDescriptor.Lifetime));
            }
            else if (wrappedDescriptor.ImplementationInstance != null)
            {
                services.AddKeyedSingleton(typeof(TInterface), innerKey,
                    wrappedDescriptor.ImplementationInstance);
            }
            else if (wrappedDescriptor.ImplementationType != null)
            {
                services.Add(new ServiceDescriptor(
                    typeof(TInterface), innerKey,
                    wrappedDescriptor.ImplementationType,
                    wrappedDescriptor.Lifetime));
            }
            else
            {
                throw new InvalidOperationException(
                    $"Cannot resolve implementation for {typeof(TInterface).Name}");
            }

            // Register the decorator, resolving the inner from the keyed registration
            services.Add(ServiceDescriptor.Describe(
                typeof(TInterface),
                provider =>
                {
                    var innerService = provider.GetRequiredKeyedService<TInterface>(innerKey);
                    return ActivatorUtilities.CreateInstance(
                        provider,
                        typeof(TDecorator),
                        innerService);
                },
                wrappedDescriptor.Lifetime));
        }
    }
}
