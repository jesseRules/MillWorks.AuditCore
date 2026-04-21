using MillWorks.AuditCore.EntityFramework.Data;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

public abstract class SqlServerTestBase
{
    [SetUp]
    public async Task SqlServerSetUpAsync()
    {
        if (!SqlServerContainerFixture.DockerAvailable)
        {
            Assert.Inconclusive(
                $"SQL Server integration tests require Docker for Testcontainers. " +
                $"Reason: {SqlServerContainerFixture.DockerSkipReason ?? "unknown"}");
        }

        await SqlServerContainerFixture.ResetAsync();
    }

    protected static string ConnectionString => SqlServerContainerFixture.ConnectionString;

    protected static AuditApplicationDbContext CreateContext()
        => SqlServerContainerFixture.CreateContext();

    protected static AuditApplicationDbContext CreateContext(string schema)
        => SqlServerContainerFixture.CreateContext(schema);
}
