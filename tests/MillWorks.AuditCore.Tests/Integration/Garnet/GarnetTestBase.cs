using StackExchange.Redis;

namespace MillWorks.AuditCore.Tests.Integration.Garnet;

/// <summary>
/// Base class for Garnet-backed integration tests. Skips (Inconclusive) when Docker is
/// unavailable and flushes the keyspace before each test.
/// </summary>
public abstract class GarnetTestBase
{
    [SetUp]
    public async Task GarnetSetUpAsync()
    {
        if (!GarnetContainerFixture.DockerAvailable)
        {
            Assert.Inconclusive(
                "Garnet integration tests require Docker for Testcontainers. " +
                $"Reason: {GarnetContainerFixture.DockerSkipReason ?? "unknown"}");
        }

        await GarnetContainerFixture.ResetAsync();
    }

    protected static IConnectionMultiplexer Multiplexer => GarnetContainerFixture.Multiplexer;
}
