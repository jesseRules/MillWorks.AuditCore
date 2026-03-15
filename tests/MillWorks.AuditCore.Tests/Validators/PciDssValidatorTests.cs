using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// PCI DSS Validator tests
/// </summary>
[TestFixture]
public class PciDssValidatorTests
{
    /// <summary>
    /// Validator instance
    /// </summary>
    private PciDssValidator _validator;

    /// <summary>
    /// Setup
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _validator = new PciDssValidator();
    }

    /// <summary>
    /// ValidateAsync_WithAuditLogs_PassesAuditLogsImplementation
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithAuditLogs_PassesAuditLogsImplementation()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Payment.Process",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "cashier"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var auditResult = results.FirstOrDefault(static r => r.RuleName.Contains("Audit Logs Implementation"));
        Assert.That(auditResult, Is.Not.Null);
        Assert.That(auditResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithCardholderDataAccess_PassesCHDLogging
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithCardholderDataAccess_PassesCHDLogging()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Card.View",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "merchant",
                EntityType = "Payment"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var chdResult = results.FirstOrDefault(static r => r.RuleName.Contains("Cardholder Data Access"));
        Assert.That(chdResult, Is.Not.Null);
        Assert.That(chdResult.Passed, Is.True);
    }

    /// <summary>
    /// Validate WithPrivilegedUsers_PassesPrivilegedUserActions
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithPrivilegedUsers_PassesPrivilegedUserActions()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Admin.Action",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var privResult = results.FirstOrDefault(static r => r.RuleName.Contains("Privileged User Actions"));
        Assert.That(privResult, Is.Not.Null);
        Assert.That(privResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithRequiredDetails_PassesDetailRequirements
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithRequiredDetails_PassesDetailRequirements()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Payment",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "cashier"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var detailsResult = results.FirstOrDefault(static r => r.RuleName.Contains("Audit Log Detail Requirements"));
        Assert.That(detailsResult, Is.Not.Null);
        Assert.That(detailsResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithIntegrityProtection_PassesLogProtection
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithIntegrityProtection_PassesLogProtection()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "user",
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var protectionResult = results.FirstOrDefault(static r => r.RuleName.Contains("Audit Log Protection"));
        Assert.That(protectionResult, Is.Not.Null);
        Assert.That(protectionResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithShortRetention_FailsRetention
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithGoodRetention_PassesRetention()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow.AddYears(-2),
                User = "user"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var retentionResult = results.FirstOrDefault(static r => r.RuleName.Contains("Audit Log Retention"));
        Assert.That(retentionResult, Is.Not.Null);
        Assert.That(retentionResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithShortRetention_FailsRetention
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithShortRetention_FailsRetention()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-30),
                User = "user"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var retentionResult = results.FirstOrDefault(static r => r.RuleName.Contains("Audit Log Retention"));
        Assert.That(retentionResult, Is.Not.Null);
        Assert.That(retentionResult.Passed, Is.False);
    }

    /// <summary>
    /// GenerateRecommendations_WithAllPassing_ReturnsEmptyList
    /// </summary>
    [Test]
    public void GenerateRecommendations_WithAllPassing_ReturnsSuccessMessage()
    {
        // Arrange
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Test",
                Passed = true,
                Message = "Pass"
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations, Is.Not.Empty);
        Assert.That(recommendations[0], Does.Contain("PCI DSS COMPLIANCE"));
    }

    /// <summary>
    /// GenerateRecommendations_WithCriticalFailures_IncludesCriticalSection
    /// </summary>
    [Test]
    public void GenerateRecommendations_WithCriticalFailures_IncludesCriticalSection()
    {
        // Arrange
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Critical",
                Passed = false,
                Severity = ValidationSeverity.Critical,
                Message = "Critical failure",
                RegulationReference = "Req 10.2",
                Recommendations = ["Fix immediately"]
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations.Any(static r => r.Contains("CRITICAL")), Is.True);
    }
}