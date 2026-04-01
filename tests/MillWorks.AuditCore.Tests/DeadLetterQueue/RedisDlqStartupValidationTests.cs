using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Tests.DeadLetterQueue;

[TestFixture]
[Category("Unit")]
public sealed class RedisDlqStartupValidationTests
{
    private Mock<IConnectionMultiplexer> _mockRedis = null!;
    private Mock<IDatabase> _mockDb = null!;
    private Mock<IAuditFieldRedactor> _mockRedactor = null!;
    private Mock<ILogger<RedisAuditDeadLetterQueue>> _mockLogger = null!;

    [SetUp]
    public void SetUp()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockDb = new Mock<IDatabase>();
        _mockRedactor = new Mock<IAuditFieldRedactor>();
        _mockLogger = new Mock<ILogger<RedisAuditDeadLetterQueue>>();

        _mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDb.Object);
    }

    [Test]
    public void Constructor_RedisConnected_PingSucceeds_NoException()
    {
        _mockRedis.Setup(r => r.IsConnected).Returns(true);

        var mockServer = new Mock<IServer>();
        mockServer.Setup(s => s.Ping(It.IsAny<CommandFlags>())).Returns(TimeSpan.FromMilliseconds(1));
        _mockRedis.Setup(r => r.GetServers()).Returns([mockServer.Object]);

        _mockDb.Setup(d => d.StringSet(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<bool>(),
            It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns(true);
        _mockDb.Setup(d => d.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisValue)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        _mockDb.Setup(d => d.KeyDelete(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(true);

        Assert.DoesNotThrow(() => CreateDlq());
    }

    [Test]
    public void Constructor_RedisDisconnected_FailFastTrue_Throws()
    {
        _mockRedis.Setup(r => r.IsConnected).Returns(false);

        var options = new ResilienceOptions { FailFastOnDlqUnavailable = true };

        Assert.Throws<InvalidOperationException>(() => CreateDlq(options));
    }

    [Test]
    public void Constructor_RedisDisconnected_FailFastFalse_LogsCritical()
    {
        _mockRedis.Setup(r => r.IsConnected).Returns(false);

        var options = new ResilienceOptions { FailFastOnDlqUnavailable = false };

        Assert.DoesNotThrow(() => CreateDlq(options));

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("not connected")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public void Constructor_PingSucceeds_WriteProbeReadbackMismatch_FailFastTrue_Throws()
    {
        _mockRedis.Setup(r => r.IsConnected).Returns(true);

        var mockServer = new Mock<IServer>();
        mockServer.Setup(s => s.Ping(It.IsAny<CommandFlags>())).Returns(TimeSpan.FromMilliseconds(1));
        _mockRedis.Setup(r => r.GetServers()).Returns([mockServer.Object]);

        _mockDb.Setup(d => d.StringSet(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<bool>(),
            It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns(true);

        // Simulate read-back mismatch
        _mockDb.Setup(d => d.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisValue)"wrong-value");

        var options = new ResilienceOptions { FailFastOnDlqUnavailable = true };
        Assert.Throws<InvalidOperationException>(() => CreateDlq(options));
    }

    [Test]
    public void Constructor_PingSucceeds_WriteProbeReadbackMismatch_FailFastFalse_LogsCritical()
    {
        _mockRedis.Setup(r => r.IsConnected).Returns(true);

        var mockServer = new Mock<IServer>();
        mockServer.Setup(s => s.Ping(It.IsAny<CommandFlags>())).Returns(TimeSpan.FromMilliseconds(1));
        _mockRedis.Setup(r => r.GetServers()).Returns([mockServer.Object]);

        _mockDb.Setup(d => d.StringSet(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
            It.IsAny<TimeSpan?>(), It.IsAny<bool>(),
            It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns(true);

        _mockDb.Setup(d => d.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns((RedisValue)"wrong-value");

        var options = new ResilienceOptions { FailFastOnDlqUnavailable = false };
        Assert.DoesNotThrow(() => CreateDlq(options));

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("read-back mismatch")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private RedisAuditDeadLetterQueue CreateDlq(ResilienceOptions? options = null)
    {
        return new RedisAuditDeadLetterQueue(
            _mockRedis.Object,
            _mockRedactor.Object,
            _mockLogger.Object,
            resilienceOptions: options);
    }
}
