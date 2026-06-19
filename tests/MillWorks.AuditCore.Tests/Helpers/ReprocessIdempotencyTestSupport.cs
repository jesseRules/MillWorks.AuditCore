using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;

namespace MillWorks.AuditCore.Tests.Helpers;

/// <summary>
/// Shared wiring for the dead-letter reprocess idempotency tests (Sqlite, Garnet, SQL Server).
/// Builds a real <see cref="AuditLogger"/> over a caller-supplied <see cref="AuditDbContext"/>
/// and a DI provider whose scoped logger writes to that same store — mirroring how the
/// dead-letter queue resolves the logger through a fresh scope per reprocess attempt.
/// </summary>
public static class ReprocessIdempotencyTestSupport
{
    /// <summary>
    /// Creates an <see cref="AuditLogger"/> writing to <paramref name="context"/>, with tamper
    /// detection disabled (the idempotency guarantee under test is the EventId primary key, not
    /// integrity records).
    /// </summary>
    public static AuditLogger CreateAuditLogger(AuditDbContext context) =>
        new(
            NullLogger<AuditLogger>.Instance,
            Mock.Of<IAuditEventFactory>(),
            new AuditEventRepository(context),
            context,
            Mock.Of<IAuditContext>(),
            new PassThroughAuditFieldRedactor(),
            tamperDetectionService: null,
            integrityWriteBatcher: null,
            securityOptions: Options.Create(new SecurityOptions
            {
                EnableTamperDetection = false,
                EnableBatchedIntegrityWrites = false
            }));

    /// <summary>
    /// Builds a DI provider whose scoped <see cref="AuditLogger"/> writes through a fresh
    /// <see cref="AuditDbContext"/> per scope (created by <paramref name="contextFactory"/>),
    /// all pointing at the same underlying database. The dead-letter queue resolves the logger
    /// via this provider's <see cref="IServiceScopeFactory"/>, matching production wiring.
    /// </summary>
    public static ServiceProvider BuildScopedAuditLoggerProvider(Func<AuditDbContext> contextFactory)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => contextFactory());
        services.AddScoped(sp => CreateAuditLogger(sp.GetRequiredService<AuditDbContext>()));
        return services.BuildServiceProvider();
    }
}
