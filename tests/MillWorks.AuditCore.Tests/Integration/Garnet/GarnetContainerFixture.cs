using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Tests.Integration.Garnet;

/// <summary>
/// Spins up an ephemeral Garnet (RESP-compatible) container for the integration tests that
/// exercise <c>RedisDistributedLockService</c> and <c>RedisAuditDeadLetterQueue</c> against a
/// real server. Mirrors <c>SqlServerContainerFixture</c>: if Docker is unavailable the tests
/// are skipped (Inconclusive) rather than failing the suite.
/// </summary>
[SetUpFixture]
public sealed class GarnetContainerFixture
{
    private const ushort GarnetPort = 6379;

    private static IContainer? _container;
    private static ConnectionMultiplexer? _multiplexer;

    public static bool DockerAvailable { get; private set; }

    public static string? DockerSkipReason { get; private set; }

    public static IConnectionMultiplexer Multiplexer =>
        _multiplexer ?? throw new InvalidOperationException(
            "Garnet container was not started. Check DockerAvailable first.");

    [OneTimeSetUp]
    public async Task StartContainerAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage("ghcr.io/microsoft/garnet:latest")
                // RedisDistributedLockService releases the lock via a Lua script (EVAL); Garnet
                // ships with Lua scripting disabled by default, so enable it.
                .WithCommand("--lua")
                .WithPortBinding(GarnetPort, assignRandomHostPort: true)
                .Build();

            await _container.StartAsync();

            var endpoint = $"{_container.Hostname}:{_container.GetMappedPublicPort(GarnetPort)}";
            _multiplexer = await ConnectWithRetryAsync(endpoint);

            DockerAvailable = true;
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            DockerAvailable = false;
            DockerSkipReason = ex.Message;
            await TestContext.Progress.WriteLineAsync(
                $"[GarnetIntegration] Docker unavailable, Garnet tests will be marked Inconclusive: {ex.Message}");
        }
    }

    [OneTimeTearDown]
    public async Task StopContainerAsync()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync();
            _multiplexer = null;
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    /// <summary>
    /// Flushes the keyspace between tests so each test sees a clean server.
    /// </summary>
    public static async Task ResetAsync()
    {
        if (!DockerAvailable || _multiplexer is null)
        {
            return;
        }

        foreach (var endpoint in _multiplexer.GetEndPoints())
        {
            await _multiplexer.GetServer(endpoint).FlushDatabaseAsync();
        }
    }

    /// <summary>
    /// Connects to Garnet, retrying with a <b>fresh</b> multiplexer each attempt until a PING
    /// succeeds. The container's port is published the instant it starts (docker-proxy accepts
    /// TCP immediately), but Garnet may not be serving yet; a multiplexer created in that window
    /// wedges with commands stuck in the backlog and does not recover, so each retry discards it
    /// and reconnects rather than reusing it.
    /// </summary>
    private static async Task<ConnectionMultiplexer> ConnectWithRetryAsync(string endpoint)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            ConnectionMultiplexer? multiplexer = null;
            try
            {
                multiplexer = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
                {
                    EndPoints = { endpoint },
                    AbortOnConnectFail = true,
                    AllowAdmin = true, // required for FLUSHDB between tests
                    ConnectRetry = 0,
                    ConnectTimeout = 2000,
                    SyncTimeout = 2000
                });
                await multiplexer.GetDatabase().PingAsync();
                return multiplexer;
            }
            catch (Exception ex)
            {
                last = ex;
                if (multiplexer is not null)
                {
                    await multiplexer.DisposeAsync();
                }

                await Task.Delay(250);
            }
        }

        throw new InvalidOperationException(
            "Garnet container did not become ready for connections in time.", last);
    }

    private static bool IsDockerUnavailable(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("Docker", StringComparison.OrdinalIgnoreCase)
               && (message.Contains("not", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("unable", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("connect", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("daemon", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("pipe", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("socket", StringComparison.OrdinalIgnoreCase));
    }
}
