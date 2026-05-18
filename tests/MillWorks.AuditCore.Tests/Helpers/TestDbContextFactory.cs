using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Data;

namespace MillWorks.AuditCore.Tests.Helpers;

/// <summary>
/// Factory for creating DbContextOptions configured for test use.
/// Suppresses EF Core warnings that cause failures when many test fixtures run together.
/// </summary>
public static class TestDbContextFactory
{
    /// <summary>
    /// Creates InMemory DbContextOptions for AuditDbContext with standard test warnings suppressed.
    /// </summary>
    public static DbContextOptions<AuditDbContext> CreateInMemoryOptions(string? dbName = null)
    {
        return new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(dbName ?? $"TestDb_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .Options;
    }

    /// <summary>
    /// Creates InMemory DbContextOptions for any DbContext type with standard test warnings suppressed.
    /// Use this for custom test contexts (e.g., TestDbContext, EncryptionTestDbContext).
    /// </summary>
    public static DbContextOptions<T> CreateInMemoryOptions<T>(
        string? dbName = null,
        Action<DbContextOptionsBuilder<T>>? configure = null) where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>()
            .UseInMemoryDatabase(dbName ?? $"TestDb_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            });

        configure?.Invoke(builder);

        return builder.Options;
    }
}
