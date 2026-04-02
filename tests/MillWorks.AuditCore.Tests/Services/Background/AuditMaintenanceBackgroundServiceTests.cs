using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Maintenance;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.Services.Background;

[TestFixture]
[Category("Unit")]
public class AuditMaintenanceBackgroundServiceTests
{
    private Mock<IAuditMaintenanceService> _mockMaintenanceService = null!;
    private Mock<IAuditComplianceService> _mockComplianceService = null!;
    private Mock<IAuditArchivalService> _mockArchivalService = null!;
    private Mock<ITamperDetectionService> _mockTamperService = null!;
    private Mock<ILogger<AuditMaintenanceBackgroundService>> _mockLogger = null!;

    [SetUp]
    public void Setup()
    {
        _mockMaintenanceService = new Mock<IAuditMaintenanceService>(MockBehavior.Strict);
        _mockComplianceService = new Mock<IAuditComplianceService>(MockBehavior.Strict);
        _mockArchivalService = new Mock<IAuditArchivalService>(MockBehavior.Strict);
        _mockTamperService = new Mock<ITamperDetectionService>(MockBehavior.Strict);
        _mockLogger = new Mock<ILogger<AuditMaintenanceBackgroundService>>();

        _mockArchivalService
            .Setup(x => x.ArchiveAuditEventsAsync(It.IsAny<DateTimeOffset>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchivalResult { Success = true, EventCount = 5 });

        _mockMaintenanceService
            .Setup(x => x.CleanupOldAuditEventsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        _mockComplianceService
            .Setup(x => x.ApplyRetentionPolicyAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockTamperService
            .Setup(x => x.DetectTamperingAsync(24, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        _mockMaintenanceService
            .Setup(x => x.OptimizeAuditTablesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockMaintenanceService
            .Setup(x => x.GetAuditStatisticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, object?>
            {
                ["TotalEvents"] = 100L,
                ["DatabaseSizeKB"] = 2048L
            });
    }

    [Test]
    public async Task StartAsync_ExecutesOneCycle_AndStopsCleanly()
    {
        var cycleCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockMaintenanceService
            .Setup(x => x.GetAuditStatisticsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, object?>
            {
                ["TotalEvents"] = 100L,
                ["DatabaseSizeKB"] = 2048L
            })
            .Callback(() => cycleCompleted.TrySetResult());

        using var serviceProvider = BuildServiceProvider();
        using var service = CreateService(serviceProvider, startupDelay: TimeSpan.Zero, intervalOverride: TimeSpan.FromSeconds(30));

        await service.StartAsync(CancellationToken.None);
        await WaitAsync(cycleCompleted.Task);
        await service.StopAsync(CancellationToken.None);

        _mockArchivalService.Verify(x => x.ArchiveAuditEventsAsync(It.IsAny<DateTimeOffset>(), null, It.IsAny<CancellationToken>()), Times.Once);
        _mockMaintenanceService.Verify(x => x.CleanupOldAuditEventsAsync(365, It.IsAny<CancellationToken>()), Times.Once);
        _mockComplianceService.Verify(x => x.ApplyRetentionPolicyAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockTamperService.Verify(x => x.DetectTamperingAsync(24, It.IsAny<CancellationToken>()), Times.Once);
        _mockMaintenanceService.Verify(x => x.OptimizeAuditTablesAsync(It.IsAny<CancellationToken>()), Times.Once);
        _mockMaintenanceService.Verify(x => x.GetAuditStatisticsAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(GetLogMessages(), Has.Some.Contains("cycle 1 completed").IgnoreCase);
    }

    [Test]
    public void StartAsync_WhenDependencyMissing_ThrowsMeaningfulError()
    {
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(_mockMaintenanceService.Object)
            .AddSingleton(_mockComplianceService.Object)
            .AddSingleton(_mockArchivalService.Object)
            .BuildServiceProvider();

        using var service = CreateService(serviceProvider, startupDelay: TimeSpan.Zero, intervalOverride: TimeSpan.FromMilliseconds(50));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await service.StartAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("ITamperDetectionService"));
    }

    [Test]
    public async Task ExecuteAsync_WhenCycleFails_LogsError_AndRetriesNextCycle()
    {
        var cleanupCalls = 0;
        var secondCycleReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockMaintenanceService
            .Setup(x => x.CleanupOldAuditEventsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((_, _) =>
            {
                cleanupCalls++;
                if (cleanupCalls == 1)
                    throw new InvalidOperationException("cleanup failed");

                secondCycleReached.TrySetResult();
                return Task.FromResult(3);
            });

        using var serviceProvider = BuildServiceProvider();
        using var service = CreateService(serviceProvider, startupDelay: TimeSpan.Zero, intervalOverride: TimeSpan.FromMilliseconds(40));

        await service.StartAsync(CancellationToken.None);
        await WaitAsync(secondCycleReached.Task);
        await service.StopAsync(CancellationToken.None);

        Assert.That(cleanupCalls, Is.GreaterThanOrEqualTo(2));
        Assert.That(GetLogMessages(), Has.Some.Contains("Error during audit maintenance cycle").IgnoreCase);
    }

    [Test]
    public async Task StopAsync_DuringActiveCycle_CancelsGracefully()
    {
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockMaintenanceService
            .Setup(x => x.CleanupOldAuditEventsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>(async (_, cancellationToken) =>
            {
                cleanupStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            });

        using var serviceProvider = BuildServiceProvider();
        using var service = CreateService(serviceProvider, startupDelay: TimeSpan.Zero, intervalOverride: TimeSpan.FromSeconds(30));

        await service.StartAsync(CancellationToken.None);
        await WaitAsync(cleanupStarted.Task);

        Assert.DoesNotThrowAsync(async () => await service.StopAsync(CancellationToken.None));
    }

    [Test]
    public async Task ExecuteAsync_RespectsConfiguredIntervalBetweenCycles()
    {
        var callTimes = new List<DateTimeOffset>();
        var secondCycleReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockMaintenanceService
            .Setup(x => x.CleanupOldAuditEventsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((_, _) =>
            {
                lock (callTimes)
                {
                    callTimes.Add(DateTimeOffset.UtcNow);
                    if (callTimes.Count == 2)
                        secondCycleReached.TrySetResult();
                }

                return Task.FromResult(3);
            });

        using var serviceProvider = BuildServiceProvider();
        using var service = CreateService(serviceProvider, startupDelay: TimeSpan.Zero, intervalOverride: TimeSpan.FromMilliseconds(80));

        await service.StartAsync(CancellationToken.None);
        await WaitAsync(secondCycleReached.Task);
        await service.StopAsync(CancellationToken.None);

        Assert.That(callTimes, Has.Count.GreaterThanOrEqualTo(2));
        Assert.That(callTimes[1] - callTimes[0], Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(60)));
        Assert.That(GetLogMessages(), Has.Some.Contains("Next scheduled run").IgnoreCase);
    }

    private ServiceProvider BuildServiceProvider()
    {
        return new ServiceCollection()
            .AddSingleton(_mockMaintenanceService.Object)
            .AddSingleton(_mockComplianceService.Object)
            .AddSingleton(_mockArchivalService.Object)
            .AddSingleton(_mockTamperService.Object)
            .BuildServiceProvider();
    }

    private AuditMaintenanceBackgroundService CreateService(
        ServiceProvider serviceProvider,
        TimeSpan startupDelay,
        TimeSpan intervalOverride)
    {
        return new AuditMaintenanceBackgroundService(
            serviceProvider,
            _mockLogger.Object,
            BuildConfiguration(),
            TimeProvider.System,
            startupDelay,
            intervalOverride);
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:MaintenanceIntervalHours"] = "24",
                ["Audit:Archive:Enabled"] = "true",
                ["Audit:Archive:ArchiveAfterDays"] = "90",
                ["Audit:RetentionDays"] = "365",
                ["Audit:OptimizationEnabled"] = "true"
            })
            .Build();
    }

    private IReadOnlyList<string> GetLogMessages()
    {
        return _mockLogger.Invocations
            .Where(x => x.Method.Name == nameof(ILogger.Log))
            .Select(x => x.Arguments[2]?.ToString() ?? string.Empty)
            .ToList();
    }

    private static async Task WaitAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(3)));
        if (completed != task)
            Assert.Fail("Timed out waiting for background service activity.");

        await task;
    }
}
