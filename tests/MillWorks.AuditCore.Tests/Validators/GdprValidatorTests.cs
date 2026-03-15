using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// GDPR Validator tests
/// </summary>
[TestFixture]
public class GdprValidatorTests
{
    /// <summary>
    /// Validator instance
    /// </summary>
    private GdprValidator _validator;

    /// <summary>
    /// Setup initializes before each test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _validator = new GdprValidator();
    }

    /// <summary>
    /// ValidateAsync_WithProcessingRecords_PassesValidation
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithProcessingRecords_PassesValidation()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.DataAccess",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var processingResult = results.FirstOrDefault(static r => r.RuleName == "Records of Processing (Article 30)");
        Assert.That(processingResult, Is.Not.Null);
        Assert.That(processingResult.Passed, Is.True);
        Assert.That(processingResult.ComplianceStandard, Is.EqualTo("GDPR"));
    }

    /// <summary>
    /// ValidateAsync_WithConsentEvents_PassesConsentValidation
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithConsentEvents_PassesConsentValidation()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.ConsentGranted",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            },
            new()
            {
                EventType = "User.ConsentWithdrawn",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var consentResult = results.FirstOrDefault(static r => r.RuleName == "User Consent Tracking (Article 7)");
        Assert.That(consentResult, Is.Not.Null);
        Assert.That(consentResult.Passed, Is.True);
        Assert.That(consentResult.Severity, Is.EqualTo(ValidationSeverity.Info));
    }

    /// <summary>
    /// ValidateAsync_WithoutConsentEvents_FailsConsentValidation
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithoutConsentEvents_FailsConsentValidation()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var consentResult = results.FirstOrDefault(static r => r.RuleName == "User Consent Tracking (Article 7)");
        Assert.That(consentResult, Is.Not.Null);
        Assert.That(consentResult.Passed, Is.False);
        Assert.That(consentResult.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    /// <summary>
    /// ValidateAsync_WithDataAccessEvents_PassesAccessLogging
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithDataAccessEvents_PassesAccessLogging()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "PersonalData.View",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var accessResult = results.FirstOrDefault(static r => r.RuleName == "Data Access Logging (Article 15)");
        Assert.That(accessResult, Is.Not.Null);
        Assert.That(accessResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithDeletionEvents_PassesErasureTracking
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithDeletionEvents_PassesErasureTracking()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Delete",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin"
            },
            new()
            {
                EventType = "Data.Anonymize",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var erasureResult = results.FirstOrDefault(static r => r.RuleName == "Right to Erasure Tracking (Article 17)");
        Assert.That(erasureResult, Is.Not.Null);
        Assert.That(erasureResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithExportEvents_PassesPortabilityTracking
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithExportEvents_PassesPortabilityTracking()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Data.Export",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var portabilityResult =
            results.FirstOrDefault(static r => r.RuleName == "Data Portability Tracking (Article 20)");
        Assert.That(portabilityResult, Is.Not.Null);
        Assert.That(portabilityResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithOldEvents_FailsRetentionValidation
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithOldEvents_FailsRetentionValidation()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow.AddYears(-8),
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var retentionResult = results.FirstOrDefault(static r => r.RuleName == "Data Retention Compliance (Article 5)");
        Assert.That(retentionResult, Is.Not.Null);
        Assert.That(retentionResult.Passed, Is.False);
        Assert.That(retentionResult.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    /// <summary>
    /// ValidateAsync_WithRecentEvents_PassesRetentionValidation
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithRecentEvents_PassesRetentionValidation()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow.AddYears(-2),
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var retentionResult = results.FirstOrDefault(static r => r.RuleName == "Data Retention Compliance (Article 5)");
        Assert.That(retentionResult, Is.Not.Null);
        Assert.That(retentionResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithUserInformation_PassesUserIdentification
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithUserInformation_PassesUserIdentification()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var userIdResult = results.FirstOrDefault(static r => r.RuleName == "User Identification (Article 32)");
        Assert.That(userIdResult, Is.Not.Null);
        Assert.That(userIdResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithoutUserInformation_FailsUserIdentification
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithoutUserInformation_FailsUserIdentification()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = null
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var userIdResult = results.FirstOrDefault(static r => r.RuleName == "User Identification (Article 32)");
        Assert.That(userIdResult, Is.Not.Null);
        Assert.That(userIdResult.Passed, Is.False);
        Assert.That(userIdResult.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    /// <summary>
    /// ValidateAsync_WithBreachEvents_TracksBreaches
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithBreachEvents_TracksBreaches()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Security.Breach",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "system"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var breachResult = results.FirstOrDefault(static r => r.RuleName == "Security Breach Tracking (Article 33)");
        Assert.That(breachResult, Is.Not.Null);
        Assert.That(breachResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithIntegrityProtection_PassesIntegrityCheck
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithIntegrityProtection_PassesIntegrityCheck()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                AuditIntegrity = new AuditIntegrityEntity
                {
                    EventId = Guid.NewGuid()
                }
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var integrityResult = results.FirstOrDefault(static r => r.RuleName == "Audit Log Integrity Protection");
        Assert.That(integrityResult, Is.Not.Null);
        Assert.That(integrityResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithoutIntegrityProtection_FailsIntegrityCheck
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithoutIntegrityProtection_FailsIntegrityCheck()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                AuditIntegrity = null
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var integrityResult = results.FirstOrDefault(static r => r.RuleName == "Audit Log Integrity Protection");
        Assert.That(integrityResult, Is.Not.Null);
        Assert.That(integrityResult.Passed, Is.False);
        Assert.That(integrityResult.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    /// <summary>
    /// GenerateRecommendations_WithAllPassing_ReturnsSuccessMessage
    /// </summary>
    [Test]
    public void GenerateRecommendations_WithAllPassing_ReturnsSuccessMessage()
    {
        // Arrange
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Test Rule",
                Passed = true,
                Message = "Test passed"
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations, Is.Not.Empty);
        Assert.That(recommendations[0], Does.Contain("GDPR: All validation checks passed"));
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
                RuleName = "Critical Rule",
                Passed = false,
                Message = "Critical failure",
                Severity = ValidationSeverity.Critical,
                Recommendations = ["Fix this immediately"]
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations, Is.Not.Empty);
        Assert.That(recommendations.Any(static r => r.Contains("CRITICAL")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("Critical Rule")), Is.True);
    }

    /// <summary>
    /// GenerateRecommendations_WithHighPriorityFailures_IncludesHighPrioritySection
    /// </summary>
    [Test]
    public void GenerateRecommendations_WithHighPriorityFailures_IncludesHighPrioritySection()
    {
        // Arrange
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "High Priority Rule",
                Passed = false,
                Message = "High priority issue",
                Severity = ValidationSeverity.High,
                Recommendations = ["Address within 30 days"]
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations, Is.Not.Empty);
        Assert.That(recommendations.Any(static r => r.Contains("HIGH PRIORITY")), Is.True);
    }

    /// <summary>
    /// GenerateRecommendations_WithMixedSeverities_GroupsBySeverity
    /// </summary>
    [Test]
    public void GenerateRecommendations_WithMixedSeverities_GroupsBySeverity()
    {
        // Arrange
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Critical Rule",
                Passed = false,
                Severity = ValidationSeverity.Critical,
                Message = "Critical",
                Recommendations = new List<string>()
            },
            new()
            {
                RuleName = "High Rule",
                Passed = false,
                Severity = ValidationSeverity.High,
                Message = "High",
                Recommendations = new List<string>()
            },
            new()
            {
                RuleName = "Medium Rule",
                Passed = false,
                Severity = ValidationSeverity.Medium,
                Message = "Medium",
                Recommendations = new List<string>()
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations.Any(static r => r.Contains("CRITICAL")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("HIGH PRIORITY")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("MEDIUM PRIORITY")), Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithEmptyEvents_FailsProcessingRecords
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithEmptyEvents_FailsProcessingRecords()
    {
        // Arrange
        var events = new List<AuditEventEntity>();

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var processingResult = results.FirstOrDefault(static r => r.RuleName == "Records of Processing (Article 30)");
        Assert.That(processingResult, Is.Not.Null);
        Assert.That(processingResult.Passed, Is.False);
        Assert.That(processingResult.Severity, Is.EqualTo(ValidationSeverity.Critical));
    }

    /// <summary>
    /// ValidateAsync_ReturnsAllRequiredValidations
    /// </summary>
    [Test]
    public async Task ValidateAsync_ReturnsAllRequiredValidations()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert - Should have all major GDPR article validations
        Assert.That(results.Count, Is.GreaterThan(5));
        Assert.That(results.Any(static r => r.RuleName.Contains("Article 30")), Is.True); // Processing records
        Assert.That(results.Any(static r => r.RuleName.Contains("Article 7")), Is.True); // Consent
        Assert.That(results.Any(static r => r.RuleName.Contains("Article 15")), Is.True); // Access
        Assert.That(results.Any(static r => r.RuleName.Contains("Article 17")), Is.True); // Erasure
        Assert.That(results.Any(static r => r.RuleName.Contains("Article 32")), Is.True); // Security
    }
}