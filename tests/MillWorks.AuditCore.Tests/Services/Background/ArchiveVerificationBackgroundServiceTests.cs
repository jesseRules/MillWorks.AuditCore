using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Services.Background;

[TestFixture]
[Category("Unit")]
public class ArchiveVerificationBackgroundServiceTests
{
    private Mock<IAuditArchivalService> _mockArchivalService = null!;
    private Mock<IArchiveRecordRepository> _mockArchiveRepository = null!;
    private Mock<ILogger<ArchiveVerificationBackgroundService>> _mockLogger = null!;

    [SetUp]
    public void Setup()
    {
        _mockArchivalService = new Mock<IAuditArchivalService>(MockBehavior.Strict);
        _mockArchiveRepository = new Mock<IArchiveRecordRepository>(MockBehavior.Strict);
        _mockLogger = new Mock<ILogger<ArchiveVerificationBackgroundService>>();

        _mockArchiveRepository
            .Setup(x => x.GetArchivesNeedingVerificationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    [Test]
    public async Task StartAsync_ExecutesVerificationCycle_AndStopsCleanly()
    {
        var cycleCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archives = new[]
        {
            new AuditArchiveRecordEntity { ArchiveId = "archive-1" },
            new AuditArchiveRecordEntity { ArchiveId = "archive-2" }
        };

        _mockArchiveRepository
            .Setup(x => x.GetArchivesNeedingVerificationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(archives);

        _mockArchivalService
            .Setup(x => x.ValidateArchiveIntegrityAsync("archive-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockArchivalService
            .Setup(x => x.ValidateArchiveIntegrityAsync("archive-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .Callback(() => cycleCompleted.TrySetResult());

        using var serviceProvider = BuildServiceProvider();
        using var service = CreateService(serviceProvider, startupDelay: TimeSpan.Zero, intervalOverride: TimeSpan.FromSeconds(30));

        await service.StartAsync(CancellationToken.None);
        await WaitAsync(cycleCompleted.Task);
        await service.StopAsync(CancellationToken.None);

        _mockArchiveRepository.Verify(x => x.GetArchivesNeedingVerificationAsync(24, It.IsAny<CancellationToken>()), Times.Once);
        _mockArchivalService.Verify(x => x.ValidateArchiveIntegrityAsync("archive-1", It.IsAny<CancellationToken>()), Times.Once);
        _mockArchivalService.Verify(x => x.ValidateArchiveIntegrityAsync("archive-2", It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(GetLogMessages(), Has.Some.Contains("2 failed").IgnoreCase.Or.Contains("1 failed").IgnoreCase);
    }

    [Test]
    public void StartAsync_WhenDependencyMissing_ThrowsMeaningfulError()
    {
        using var serviceProvider = new ServiceCollection()
            .AddSingleton(_mockArchivalService.Object)
            .BuildServiceProvider();

        using var service = CreateService(serviceProvider, startupDelay: TimeSpan.Zero, intervalOverride: TimeSpan.FromMilliseconds(50));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await service.StartAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("IArchiveRecordRepository"));
    }

    [Test]
    public async Task ExecuteAsync_WhenRepositoryFails_LogsError_AndRetriesNextCycle()
    {
        var calls = 0;
        var secondCycleReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockArchiveRepository
            .Setup(x => x.GetArchivesNeedingVerificationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((_, _) =>
            {
                calls++;
                if (calls == 1)
                    throw new InvalidOperationException("query failed");

                secondCycleReached.TrySetResult();
                return Task.FromResult<IEnumerable<AuditArchiveRecordEntity>>([]);
            });

        using var serviceProvider = BuildServiceProvider();
        using var service = CreateService(serviceProvider, startupDelay: TimeSpan.Zero, intervalOverride: TimeSpan.FromMilliseconds(40));

        await service.StartAsync(CancellationToken.None);
        await WaitAsync(secondCycleReached.Task);
        await service.StopAsync(CancellationToken.None);

        Assert.That(calls, Is.GreaterThanOrEqualTo(2));
        Assert.That(GetLogMessages(), Has.Some.Contains("Error during archive verification cycle").IgnoreCase);
    }

    [Test]
    public async Task StopAsync_DuringActiveVerification_CancelsGracefully()
    {
        var verificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archives = new[]
        {
            new AuditArchiveRecordEntity { ArchiveId = "archive-1" }
        };

        _mockArchiveRepository
            .Setup(x => x.GetArchivesNeedingVerificationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(archives);

        _mockArchivalService
            .Setup(x => x.ValidateArchiveIntegrityAsync("archive-1", It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>(async (_, cancellationToken) =>
            {
                verificationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return true;
            });

        using var serviceProvider = BuildServiceProvider();
        using var service = CreateService(serviceProvider, startupDelay: TimeSpan.Zero, intervalOverride: TimeSpan.FromSeconds(30));

        await service.StartAsync(CancellationToken.None);
        await WaitAsync(verificationStarted.Task);

        Assert.DoesNotThrowAsync(async () => await service.StopAsync(CancellationToken.None));
    }

    [Test]
    public async Task ExecuteAsync_RespectsConfiguredIntervalBetweenCycles()
    {
        var callTimes = new List<DateTimeOffset>();
        var secondCycleReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockArchiveRepository
            .Setup(x => x.GetArchivesNeedingVerificationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((_, _) =>
            {
                lock (callTimes)
                {
                    callTimes.Add(DateTimeOffset.UtcNow);
                    if (callTimes.Count == 2)
                        secondCycleReached.TrySetResult();
                }

                return Task.FromResult<IEnumerable<AuditArchiveRecordEntity>>([]);
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
            .AddSingleton(_mockArchivalService.Object)
            .AddSingleton(_mockArchiveRepository.Object)
            .BuildServiceProvider();
    }

    private ArchiveVerificationBackgroundService CreateService(
        ServiceProvider serviceProvider,
        TimeSpan startupDelay,
        TimeSpan intervalOverride)
    {
        return new ArchiveVerificationBackgroundService(
            serviceProvider,
            _mockLogger.Object,
            Options.Create(new ArchivalOptions
            {
                EnableBackgroundArchival = true,
                VerificationIntervalHours = 24
            }),
            TimeProvider.System,
            startupDelay,
            intervalOverride);
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
            Assert.Fail("Timed out waiting for archive verification service activity.");

        await task;
    }
}
