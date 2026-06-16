using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Services.Redis;

namespace MillWorks.AuditCore.Tests.Integration.Garnet;

/// <summary>
/// Real-server tests for <see cref="RedisDistributedLockService"/> against Garnet. Mock-based
/// unit tests cannot verify <c>SET NX</c>/TTL/Lua compare-and-delete semantics; these do.
/// They also document RedisJobQueueDurability finding #4: the lock is an efficiency
/// optimization — when a holder's work outlives the TTL the key lapses and a second holder
/// acquires (overlap), while the token-checked release still refuses to evict that holder.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class RedisDistributedLockGarnetTests : GarnetTestBase
{
    private RedisDistributedLockService NewFastFailLockService() =>
        new(
            Multiplexer,
            NullLogger<RedisDistributedLockService>.Instance,
            maxRetries: 3,
            baseDelay: TimeSpan.FromMilliseconds(10),
            useJitter: false);

    [Test]
    public void Constructor_AgainstLuaEnabledGarnet_PassesScriptingValidation()
    {
        // The shared fixture starts Garnet with --lua, so the startup probe must succeed even
        // when configured to fail fast on missing scripting support.
        var construct = () => new RedisDistributedLockService(
            Multiplexer,
            NullLogger<RedisDistributedLockService>.Instance,
            failFastOnMissingScripting: true);

        construct.Should().NotThrow();
    }

    [Test]
    public async Task AcquireLock_WhileHeld_BlocksSecondAcquirer_ThenSucceedsAfterRelease()
    {
        var svc = NewFastFailLockService();
        var resource = "excl-" + Guid.NewGuid().ToString("N");

        var handle = await svc.AcquireLockAsync(resource, TimeSpan.FromSeconds(30));

        // While the lock is held, a second acquirer exhausts its retries and times out.
        var contended = async () => await svc.AcquireLockAsync(resource, TimeSpan.FromSeconds(30));
        await contended.Should().ThrowAsync<TimeoutException>();

        handle.Dispose();

        // After release the resource is free again.
        var reacquired = await svc.AcquireLockAsync(resource, TimeSpan.FromSeconds(30));
        reacquired.Should().NotBeNull();
        reacquired.Dispose();
    }

    [Test]
    public async Task LockTtlLapse_LetsSecondHolderAcquire_AndStaleReleaseDoesNotEvictIt()
    {
        // Finding #4, demonstrated end to end against a real server.
        var svc = NewFastFailLockService();
        var resource = "ttl-lapse-" + Guid.NewGuid().ToString("N");

        // First holder takes a short-lived lock, then its work outlives the TTL.
        var first = await svc.AcquireLockAsync(resource, TimeSpan.FromMilliseconds(300));
        await Task.Delay(TimeSpan.FromMilliseconds(700));

        // The lapsed key lets a second holder acquire — the overlap the finding describes.
        var second = await svc.AcquireLockAsync(resource, TimeSpan.FromSeconds(30));
        second.Should().NotBeNull(
            "a lapsed TTL lets a second holder acquire the same lock — documented finding #4 overlap");

        // The first holder finishing must NOT evict the second holder: release is a
        // token-checked compare-and-delete, and the key now carries the second holder's token.
        first.Dispose();

        // Proof the second holder still owns the lock: a fresh acquirer still times out.
        var contended = async () => await svc.AcquireLockAsync(resource, TimeSpan.FromSeconds(30));
        await contended.Should().ThrowAsync<TimeoutException>(
            "the stale release from the first holder must not have deleted the second holder's lock");

        // Once the real holder releases, the resource is free.
        second.Dispose();
        var third = await svc.AcquireLockAsync(resource, TimeSpan.FromSeconds(30));
        third.Should().NotBeNull();
        third.Dispose();
    }
}
