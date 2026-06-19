using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.AspNetCore.Configuration;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.Redis;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Tests.Integration.Garnet;

/// <summary>
/// Verifies the <see cref="RedisDistributedLockService"/> startup probe against a Garnet
/// started <b>without</b> <c>--lua</c> (Lua scripting disabled) — the misconfiguration the
/// probe exists to catch — both via direct construction and via the DI registration driven by
/// <c>SecurityOptions.FailFastOnMissingLockScripting</c>. Uses a dedicated container because
/// the shared fixture enables Lua.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class RedisDistributedLockLuaValidationGarnetTests : GarnetTestBase
{
    private IContainer? _noLuaContainer;
    private ConnectionMultiplexer? _noLuaMultiplexer;

    private IConnectionMultiplexer NoLuaMultiplexer =>
        _noLuaMultiplexer ?? throw new InvalidOperationException("no-lua Garnet container was not started");

    [OneTimeSetUp]
    public async Task StartNoLuaGarnetAsync()
    {
        // The shared GarnetContainerFixture (SetUpFixture) has already determined DockerAvailable.
        if (!GarnetContainerFixture.DockerAvailable)
        {
            return; // per-test SetUp marks the tests Inconclusive
        }

        // Deliberately no "--lua": EVAL is disabled, so the lock-release script is unsupported.
        _noLuaContainer = new ContainerBuilder("ghcr.io/microsoft/garnet:latest")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .Build();

        await _noLuaContainer.StartAsync();
        var endpoint = $"{_noLuaContainer.Hostname}:{_noLuaContainer.GetMappedPublicPort(6379)}";
        _noLuaMultiplexer = await GarnetContainerFixture.ConnectWithRetryAsync(endpoint);
    }

    [OneTimeTearDown]
    public async Task StopNoLuaGarnetAsync()
    {
        if (_noLuaMultiplexer is not null)
        {
            await _noLuaMultiplexer.DisposeAsync();
        }

        if (_noLuaContainer is not null)
        {
            await _noLuaContainer.DisposeAsync();
        }
    }

    [Test]
    public void StartupValidation_AgainstGarnetWithoutLua_ThrowsOnlyWhenFailFast()
    {
        // failFast: surfaces the misconfiguration as a startup failure with actionable guidance.
        var failFast = () => new RedisDistributedLockService(
            NoLuaMultiplexer,
            NullLogger<RedisDistributedLockService>.Instance,
            failFastOnMissingScripting: true);

        failFast.Should().Throw<InvalidOperationException>()
            .WithMessage("*Lua*")
            .WithMessage("*--lua*");

        // default (failFast off): logs the problem but lets the host start.
        var lenient = () => new RedisDistributedLockService(
            NoLuaMultiplexer,
            NullLogger<RedisDistributedLockService>.Instance);

        lenient.Should().NotThrow();
    }

    [Test]
    public void Registration_FailFastFlagFromSecurityOptions_FlowsToLockService()
    {
        // End-to-end: SecurityOptions.FailFastOnMissingLockScripting must reach the lock service
        // through the AddMillWorksAudit registration (not just the constructor).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(NoLuaMultiplexer);

        var builder = new MillWorksAuditBuilder(services, new AuditOptions { ApplicationName = "TestApp" });
        builder.UseSecurity(security =>
        {
            security.EnableTamperDetection = false;
            security.UseRedisLocking = true;
            security.FailFastOnMissingLockScripting = true;
        });

        using var provider = services.BuildServiceProvider();
        var resolve = () => provider.GetRequiredService<IAuditDistributedLockService>();

        resolve.Should().Throw<InvalidOperationException>().WithMessage("*Lua*");
    }
}
