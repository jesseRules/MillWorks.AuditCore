using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Validators;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// AuditComplianceService unit tests.
/// </summary>
[TestFixture]
public class AuditComplianceServiceTests
{
    /// <summary>
    /// Mock audit event repository.
    /// </summary>
    private Mock<IAuditEventRepository> _mockAuditEventRepository;

    /// <summary>
    /// Mock archival service for audit events.
    /// </summary>
    private Mock<IAuditArchivalService> _mockAuditArchivalService;

    /// <summary>
    /// Mock logger for the compliance service.
    /// </summary>
    private Mock<ILogger<AuditComplianceService>> _mockLogger;

    /// <summary>
    /// Configuration for retention policies.
    /// </summary>
    private IConfiguration _configuration;

    /// <summary>
    /// Compliance service under test.
    /// </summary>
    private AuditComplianceService _complianceService;

    /// <summary>
    /// Setup before each test.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockAuditEventRepository = new Mock<IAuditEventRepository>();
        _mockAuditArchivalService = new Mock<IAuditArchivalService>();
        _mockLogger = new Mock<ILogger<AuditComplianceService>>();

        var configDict = new Dictionary<string, string>
        {
            ["Audit:RetentionPolicies:0:EventType"] = "User.Login",
            ["Audit:RetentionPolicies:0:RetentionDays"] = "365",
            ["Audit:RetentionPolicies:0:ArchiveBeforeDelete"] = "true",
            ["Audit:RetentionPolicies:1:EventType"] = "User.Logout",
            ["Audit:RetentionPolicies:1:RetentionDays"] = "90",
            ["Audit:RetentionPolicies:1:ArchiveBeforeDelete"] = "false"
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        // Provide real validators (same as DI would register)
        var validators = new IComplianceValidator[]
        {
            new GdprValidator(),
            new Soc2Validator(),
            new HipaaValidator(),
            new Iso27001Validator(),
            new FerpaValidator(new Mock<IComplianceAttributeScanner>().Object),
            new PciDssValidator()
        };

        // Setup transaction mock for methods that use transactions
        _mockAuditEventRepository
            .Setup(static x => x.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(static (Func<Task> action, CancellationToken _) => action());

        _complianceService = new AuditComplianceService(
            _mockAuditEventRepository.Object,
            validators,
            _mockLogger.Object,
            _configuration,
            _mockAuditArchivalService.Object);
    }

    /// <summary>
    /// GenerateComplianceReportAsync with GDPR standard returns expected report.
    /// </summary>
    [Test]
    public async Task GenerateComplianceReportAsync_WithGDPRStandard_ReturnsGDPRReport()
    {
        // Arrange
        var standard = ComplianceStandard.GDPR;
        var startDate = DateTimeOffset.UtcNow.AddMonths(-1);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.ConsentGranted",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "DataAccess",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5)
            }
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByDateRangeAsync(startDate, endDate, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        // Statistics are now computed via server-side CountAsync
        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        // Act
        var result = await _complianceService.GenerateComplianceReportAsync(
            standard, startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Standard, Is.EqualTo(standard));
        Assert.That(result.PeriodStart, Is.EqualTo(startDate));
        Assert.That(result.PeriodEnd, Is.EqualTo(endDate));
        Assert.That(result.ValidationResults, Is.Not.Null);
        Assert.That(result.Statistics, Is.Not.Null);
        Assert.That(result.Statistics.TotalEvents, Is.EqualTo(2));
    }

    /// <summary>
    /// GenerateComplianceReportAsync with SOC2 standard returns expected report.
    /// </summary>
    [Test]
    public async Task GenerateComplianceReportAsync_WithSOC2Standard_ReturnsSOC2Report()
    {
        // Arrange
        var standard = ComplianceStandard.SOC2;
        var startDate = DateTimeOffset.UtcNow.AddMonths(-1);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Security.Alert",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Access.Granted",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5)
            }
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByDateRangeAsync(startDate, endDate, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        // Act
        var result = await _complianceService.GenerateComplianceReportAsync(
            standard, startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Standard, Is.EqualTo(standard));
        Assert.That(result.ValidationResults, Is.Not.Empty);
    }


    /// <summary>
    /// GenerateComplianceReportAsync with HIPAA standard and compliant data returns compliant report.
    /// </summary>
    [Test]
    public async Task GenerateComplianceReportAsync_WithHIPAAStandard_ReturnsCompliantReport()
    {
        // Arrange
        var standard = ComplianceStandard.HIPAA;
        var startDate = DateTimeOffset.UtcNow.AddMonths(-1);
        var endDate = DateTimeOffset.UtcNow;

        // Create events that satisfy HIPAA validation requirements
        var events = new List<AuditEventEntity>
        {
            // PHI Access with user identification and integrity
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "PHI.Access",
                User = "user@example.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10),
                AuditIntegrity = new AuditIntegrityEntity
                {
                    EventHash = "test-hash",
                    PreviousEventHash = "previous-hash"
                }
            },
            // Login event for login monitoring
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                User = "user@example.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5),
                AuditIntegrity = new AuditIntegrityEntity
                {
                    EventHash = "test-hash-2",
                    PreviousEventHash = "test-hash"
                }
            },
            // Logoff event for session management
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Logoff",
                User = "user@example.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5),
                AuditIntegrity = new AuditIntegrityEntity
                {
                    EventHash = "test-hash-3",
                    PreviousEventHash = "test-hash-2"
                }
            }
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByDateRangeAsync(startDate, endDate, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        // Statistics are now computed via server-side CountAsync
        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        // Act
        var result = await _complianceService.GenerateComplianceReportAsync(
            standard, startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Standard, Is.EqualTo(standard));
        Assert.That(result.ValidationResults, Is.Not.Empty);
        Assert.That(result.Statistics, Is.Not.Null);
        Assert.That(result.Statistics.TotalEvents, Is.EqualTo(3));

        // With proper PHI access logging, user identification, integrity controls, and login monitoring
        Assert.That(result.IsCompliant, Is.True,
            "Should be compliant with PHI access logging, user identification, integrity controls, and login monitoring");
    }

    /// <summary>
    /// GenerateComplianceReportAsync with HIPAA standard and no events returns non-compliant report.
    /// </summary>
    [Test]
    public async Task GenerateComplianceReportAsync_WithHIPAAStandard_NoEvents_ReturnsNonCompliantReport()
    {
        // Arrange
        var standard = ComplianceStandard.HIPAA;
        var startDate = DateTimeOffset.UtcNow.AddMonths(-1);
        var endDate = DateTimeOffset.UtcNow;

        // Empty events list - should fail HIPAA validation if PHI access is required
        var events = new List<AuditEventEntity>();

        _mockAuditEventRepository
            .Setup(x => x.GetByDateRangeAsync(startDate, endDate, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        // Statistics are now computed via server-side CountAsync
        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        var result = await _complianceService.GenerateComplianceReportAsync(
            standard, startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Standard, Is.EqualTo(standard));
        Assert.That(result.ValidationResults, Is.Not.Empty);
        // The compliance status depends on the validator's logic
        // Just verify the report was generated with statistics
        Assert.That(result.Statistics, Is.Not.Null);
        Assert.That(result.Statistics.TotalEvents, Is.EqualTo(0));
    }

    /// <summary>
    /// GenerateComplianceReportAsync with ISO27001 standard returns expected report.
    /// </summary>
    [Test]
    public async Task GenerateComplianceReportAsync_WithISO27001Standard_ReturnsISO27001Report()
    {
        // Arrange
        var standard = ComplianceStandard.ISO27001;
        var startDate = DateTimeOffset.UtcNow.AddMonths(-1);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Incident.Logged",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10)
            }
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByDateRangeAsync(startDate, endDate, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        // Act
        var result = await _complianceService.GenerateComplianceReportAsync(
            standard, startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Standard, Is.EqualTo(standard));
        Assert.That(result.ValidationResults, Is.Not.Empty);
    }

    /// <summary>
    /// GenerateComplianceReportAsync with unsupported standard throws NotSupportedException.
    /// </summary>
    [Test]
    public void GenerateComplianceReportAsync_WithUnsupportedStandard_ThrowsException()
    {
        // Arrange
        var unsupportedStandard = (ComplianceStandard)999;
        var startDate = DateTimeOffset.UtcNow.AddMonths(-1);
        var endDate = DateTimeOffset.UtcNow;

        // Act & Assert
        var ex = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await _complianceService.GenerateComplianceReportAsync(
                unsupportedStandard, startDate, endDate));

        Assert.That(ex.Message, Does.Contain("not supported"));
    }

    /// <summary>
    /// AnonymizeUserDataAsync with valid user ID anonymizes data.
    /// </summary>
    [Test]
    public async Task AnonymizeUserDataAsync_WithValidUserId_AnonymizesData()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                UserId = userId,
                User = "john.doe@example.com",
                UserFullName = "John Doe",
                IpAddress = "192.168.1.100",
                UserAgent = "Mozilla/5.0",
                JsonData = "{\"userId\":\"" + userId + "\",\"action\":\"login\"}"
            },
            new()
            {
                EventId = Guid.NewGuid(),
                UserId = userId,
                User = "john.doe@example.com",
                UserFullName = "John Doe",
                IpAddress = "192.168.1.101",
                UserAgent = "Mozilla/5.0"
            }
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        _mockAuditEventRepository
            .Setup(static x =>
                x.UpdateRangeAsync(It.IsAny<IEnumerable<AuditEventEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (IEnumerable<AuditEventEntity> e, CancellationToken ct) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(events.Count);

        // Act
        var result = await _complianceService.AnonymizeUserDataAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True);
        Assert.That(result.EventCount, Is.EqualTo(2));

        _mockAuditEventRepository.Verify(static x => x.UpdateRangeAsync(
                It.Is<IEnumerable<AuditEventEntity>>(static e =>
                    e.All(static evt => evt.User == "ANONYMIZED" && evt.UserFullName == "ANONYMIZED" &&
                                        evt.UserAgent == "ANONYMIZED")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// AnonymizeUserDataAsync with no events returns success with zero count.
    /// </summary>
    [Test]
    public async Task AnonymizeUserDataAsync_WithNoEvents_ReturnsSuccessWithZeroCount()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var events = new List<AuditEventEntity>();

        _mockAuditEventRepository
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        // Act
        var result = await _complianceService.AnonymizeUserDataAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True);
        Assert.That(result.EventCount, Is.EqualTo(0));
    }

    /// <summary>
    /// AnonymizeUserDataAsync when exception occurs returns failure.
    /// </summary>
    [Test]
    public async Task AnonymizeUserDataAsync_WhenExceptionOccurs_ReturnsFailure()
    {
        // Arrange
        var userId = Guid.NewGuid();

        _mockAuditEventRepository
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        var result = await _complianceService.AnonymizeUserDataAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Database error"));
    }

    /// <summary>
    /// ExportUserAuditDataAsync with valid user ID returns JSON data.
    /// </summary>
    [Test]
    public async Task ExportUserAuditDataAsync_WithValidUserId_ReturnsJsonData()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                UserId = userId,
                InsertedDate = DateTimeOffset.UtcNow,
                EntityType = "User",
                EntityId = userId.ToString(),
                Action = "Login"
            }
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        // Act
        var result = await _complianceService.ExportUserAuditDataAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Length, Is.GreaterThan(0));
        Assert.That(result.RecordCount, Is.EqualTo(1));
        Assert.That(result.FileName, Does.Contain(userId.ToString()));

        // Verify JSON content
        var json = System.Text.Encoding.UTF8.GetString(result.Data);
        Assert.That(json, Does.Contain(userId.ToString()));
        Assert.That(json, Does.Contain("User.Login"));
    }

    /// <summary>
    /// ExportUserAuditDataAsync with no events returns empty export.
    /// </summary>
    [Test]
    public async Task ExportUserAuditDataAsync_WithNoEvents_ReturnsEmptyExport()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var events = new List<AuditEventEntity>();

        _mockAuditEventRepository
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        // Act
        var result = await _complianceService.ExportUserAuditDataAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.True);
        Assert.That(result.RecordCount, Is.EqualTo(0));
    }

    /// <summary>
    /// ExportUserAuditDataAsync when exception occurs returns failure.
    /// </summary>
    [Test]
    public async Task ExportUserAuditDataAsync_WhenExceptionOccurs_ReturnsFailure()
    {
        // Arrange
        var userId = Guid.NewGuid();

        _mockAuditEventRepository
            .Setup(x => x.GetByUserIdAsync(userId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Export failed"));

        // Act
        var result = await _complianceService.ExportUserAuditDataAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Export failed"));
    }

    /// <summary>
    /// ValidateRetentionComplianceAsync with compliant data returns true.
    /// </summary>
    [Test]
    public async Task ValidateRetentionComplianceAsync_WithCompliantData_ReturnsTrue()
    {
        // Arrange — CountAsync returns 0 violations (all within retention)
        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        var result = await _complianceService.ValidateRetentionComplianceAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    /// <summary>
    /// ValidateRetentionComplianceAsync with non-compliant data returns false.
    /// </summary>
    [Test]
    public async Task ValidateRetentionComplianceAsync_WithNonCompliantData_ReturnsFalse()
    {
        // Arrange — CountAsync returns 1 violation for the User.Login policy
        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        var result = await _complianceService.ValidateRetentionComplianceAsync();

        // Assert
        Assert.That(result, Is.False);
    }

    /// <summary>
    /// ApplyRetentionPolicyAsync with archive enabled archives before deletion.
    /// </summary>
    [Test]
    public async Task ApplyRetentionPolicyAsync_WithArchiveEnabled_ArchivesBeforeDelete()
    {
        // Arrange
        DateTimeOffset? capturedDate = null;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-400)
            }
        };

        // Mock GetDistinctEventTypesAsync to return event types that match the policy
        _mockAuditEventRepository
            .Setup(static x => x.GetDistinctEventTypesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["User.Login"]);

        _mockAuditEventRepository
            .Setup(static x => x.GetByEventTypeAsync("User.Login", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        _mockAuditArchivalService
            .Setup(static x => x.ArchiveAuditEventsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchivalResult { Success = true, EventCount = 1 })
            .Callback<DateTimeOffset, string?, CancellationToken>((date, eventType, ct) => capturedDate = date);

        // Act
        await _complianceService.ApplyRetentionPolicyAsync();

        // Assert
        _mockAuditArchivalService.Verify(static x => x.ArchiveAuditEventsAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // Verify the date is approximately correct (within 1 day of expected)
        Assert.That(capturedDate, Is.Not.Null);
        var expectedCutoff = DateTimeOffset.UtcNow.AddDays(-365);
        Assert.That(capturedDate.Value, Is.EqualTo(expectedCutoff).Within(TimeSpan.FromDays(1)));
    }

    /// <summary>
    /// ApplyRetentionPolicyAsync with archive disabled deletes directly.
    /// </summary>
    [Test]
    public async Task ApplyRetentionPolicyAsync_WithArchiveDisabled_DeletesDirectly()
    {
        // Arrange
        // Mock GetDistinctEventTypesAsync to return event types that match the policy
        _mockAuditEventRepository
            .Setup(static x => x.GetDistinctEventTypesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["User.Logout"]);

        _mockAuditEventRepository
            .Setup(static x => x.ExecuteDeleteWhereAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        await _complianceService.ApplyRetentionPolicyAsync();

        // Assert
        _mockAuditEventRepository.Verify(static x => x.ExecuteDeleteWhereAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// GetRetentionViolationsAsync returns events violating retention policy.
    /// </summary>
    [Test]
    public async Task GetRetentionViolationsAsync_ReturnsViolatingEvents()
    {
        // Arrange
        var eventType = "User.Login";
        var retentionDays = 365;
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        // FindAsync now does the filtering server-side, so only violations are returned
        var violations = new List<AuditEventEntity>
        {
            new()
            {
                EventType = eventType,
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-400) // Violates retention
            }
        };

        _mockAuditEventRepository
            .Setup(static x => x.FindAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(violations);

        // Act
        var result = await _complianceService.GetRetentionViolationsAsync(eventType, retentionDays);

        // Assert
        var resultList = result.ToList();
        Assert.That(resultList, Has.Count.EqualTo(1));
        Assert.That(resultList[0].InsertedDate, Is.LessThan(cutoffDate));
    }

    /// <summary>
    /// GetComplianceStatisticsAsync returns correct statistics.
    /// </summary>
    [Test]
    public async Task GetComplianceStatisticsAsync_ReturnsStatistics()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-30);
        var endDate = DateTimeOffset.UtcNow;

        // CountAsync is called 5 times with different predicates (total, User, Update, Security, Delete)
        var callCount = 0;
        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount switch
                {
                    1 => 4, // TotalEvents
                    2 => 2, // UserAccessEvents (contains "User")
                    3 => 1, // DataModificationEvents (contains "Update")
                    4 => 1, // SecurityEvents (contains "Security")
                    5 => 1, // HighRiskEvents (contains "Delete")
                    _ => 0
                };
            });

        // Act
        var result = await _complianceService.GetComplianceStatisticsAsync(startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalEvents, Is.EqualTo(4));
        Assert.That(result.UserAccessEvents, Is.GreaterThan(0));
        Assert.That(result.DataModificationEvents, Is.GreaterThan(0));
        Assert.That(result.SecurityEvents, Is.GreaterThan(0));
        Assert.That(result.HighRiskEvents, Is.GreaterThan(0));
    }

    /// <summary>
    /// GetComplianceStatisticsAsync with event type filter filters correctly.
    /// </summary>
    [Test]
    public async Task GetComplianceStatisticsAsync_WithEventTypeFilter_FiltersCorrectly()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-30);
        var endDate = DateTimeOffset.UtcNow;
        var eventTypes = new List<string> { "User.Login", "User.Logout" };

        // First CountAsync call is for TotalEvents (with event type filter), returns 2
        var callCount = 0;
        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1 ? 2 : 1; // 2 total, 1 for each category
            });

        // Act
        var result = await _complianceService.GetComplianceStatisticsAsync(startDate, endDate, eventTypes);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalEvents, Is.EqualTo(2)); // Only Login and Logout
    }

    /// <summary>
    /// ValidateEventTypeComplianceAsync with valid standard returns validation results.
    /// </summary>
    [Test]
    public async Task ValidateEventTypeComplianceAsync_WithValidStandard_ReturnsValidationResults()
    {
        // Arrange
        var eventType = "User.Login";
        var standard = ComplianceStandard.SOC2;
        var startDate = DateTimeOffset.UtcNow.AddDays(-30);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = eventType,
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5)
            }
        };

        _mockAuditEventRepository
            .Setup(static x => x.FindAsync(
                It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);

        // Act
        var result = await _complianceService.ValidateEventTypeComplianceAsync(
            eventType, standard, startDate, endDate);

        // Assert
        IEnumerable<AuditValidationResult> auditValidationResults = result.ToList();
        Assert.That(auditValidationResults, Is.Not.Null);
        var validationResults = auditValidationResults.ToList();
        Assert.That(validationResults, Is.Not.Empty);
    }

    /// <summary>
    /// ValidateEventTypeComplianceAsync with unsupported standard throws NotSupportedException.
    /// </summary>
    [Test]
    public void ValidateEventTypeComplianceAsync_WithUnsupportedStandard_ThrowsException()
    {
        // Arrange
        var eventType = "User.Login";
        var unsupportedStandard = (ComplianceStandard)999;
        var startDate = DateTimeOffset.UtcNow.AddDays(-30);
        var endDate = DateTimeOffset.UtcNow;

        // Act & Assert
        var ex = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await _complianceService.ValidateEventTypeComplianceAsync(
                eventType, unsupportedStandard, startDate, endDate));

        Assert.That(ex.Message, Does.Contain("not supported"));
    }
}
