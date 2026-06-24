using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

namespace MillWorks.AuditCore.Tests.Helpers;

/// <summary>
/// Base fixture that sets up common mocks used across service unit tests.
/// Inherit from this to avoid repeating mock declarations in every test class.
/// </summary>
public abstract class ServiceTestFixture
{
    protected Mock<IAuditLogRepository> MockAuditLogRepository { get; private set; } = null!;
    protected Mock<IAuditEventRepository> MockAuditEventRepository { get; private set; } = null!;
    protected Mock<IAuditContext> MockAuditContext { get; private set; } = null!;
    protected AuditContext RealAuditContext { get; private set; } = null!;
    protected IConfiguration Configuration { get; private set; } = null!;

    /// <summary>
    /// Override to supply additional configuration key/value pairs.
    /// </summary>
    protected virtual Dictionary<string, string?> ConfigurationValues => new();

    [SetUp]
    public virtual void BaseSetUp()
    {
        MockAuditLogRepository = new Mock<IAuditLogRepository>();
        MockAuditEventRepository = new Mock<IAuditEventRepository>();
        MockAuditContext = new Mock<IAuditContext>();
        RealAuditContext = new AuditContext();

        var configValues = new Dictionary<string, string?>
        {
            { "Audit:Enabled", "true" }
        };

        foreach (var kvp in ConfigurationValues)
            configValues[kvp.Key] = kvp.Value;

        Configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();
    }

    /// <summary>
    /// Creates a typed mock logger for the specified service class.
    /// </summary>
    protected static Mock<ILogger<T>> CreateMockLogger<T>() => new();
}
