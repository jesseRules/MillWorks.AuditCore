using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// SOC2 Validator tests
/// </summary>
[TestFixture]
public class Soc2ValidatorTests
{
    /// <summary>
    /// Validator instance
    /// </summary>
    private Soc2Validator _validator;

    /// <summary>
    /// Setup
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _validator = new Soc2Validator();
    }

    /// <summary>
    /// ValidateAsync_WithAccessControls_PassesAccessControlLogging
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithAccessControls_PassesAccessControlLogging()
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
        var accessResult = results.FirstOrDefault(static r => r.RuleName == "Access Control Logging (CC6.1)");
        Assert.That(accessResult, Is.Not.Null);
        Assert.That(accessResult.Passed, Is.True);
        Assert.That(accessResult.ComplianceStandard, Is.EqualTo("SOC 2"));
    }

    /// <summary>
    /// ValidateAsync_WithNoEvents_FailsAccessControlLogging
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithNoEvents_FailsAccessControlLogging()
    {
        // Arrange
        var events = new List<AuditEventEntity>();

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var accessResult = results.FirstOrDefault(static r => r.RuleName == "Access Control Logging (CC6.1)");
        Assert.That(accessResult, Is.Not.Null);
        Assert.That(accessResult.Passed, Is.False);
        Assert.That(accessResult.Severity, Is.EqualTo(ValidationSeverity.Critical));
    }

    /// <summary>
    /// ValidateAsync_WithAuthenticationEvents_PassesAuthenticationLogging
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithAuthenticationEvents_PassesAuthenticationLogging()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            },
            new()
            {
                EventType = "User.Authentication",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var authResult = results.FirstOrDefault(static r => r.RuleName == "Authentication Logging (CC6.2)");
        Assert.That(authResult, Is.Not.Null);
        Assert.That(authResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithAccountManagementEvents_PassesAccountManagement
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithAccountManagementEvents_PassesAccountManagement()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Created",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin"
            },
            new()
            {
                EventType = "Permission.Changed",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var accountResult = results.FirstOrDefault(static r => r.RuleName == "Account Management Logging (CC6.3)");
        Assert.That(accountResult, Is.Not.Null);
        Assert.That(accountResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithUserIdentification_PassesUserIdCheck
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithUserIdentification_PassesUserIdCheck()
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
        var userIdResult = results.FirstOrDefault(static r => r.RuleName == "User Identification (CC6.6)");
        Assert.That(userIdResult, Is.Not.Null);
        Assert.That(userIdResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithoutUserIdentification_FailsUserIdCheck
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithoutUserIdentification_FailsUserIdCheck()
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
        var userIdResult = results.FirstOrDefault(static r => r.RuleName == "User Identification (CC6.6)");
        Assert.That(userIdResult, Is.Not.Null);
        Assert.That(userIdResult.Passed, Is.False);
        Assert.That(userIdResult.Severity, Is.EqualTo(ValidationSeverity.Critical));
    }

    /// <summary>
    /// ValidateAsync_WithSecurityEvents_TracksSecurityEvents
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithSecurityEvents_TracksSecurityEvents()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Security.Incident",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "system"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var securityResult = results.FirstOrDefault(static r => r.RuleName == "Security Event Detection (CC7.2)");
        Assert.That(securityResult, Is.Not.Null);
        Assert.That(securityResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithSecurityEvents_TracksSecurityEvents
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithRecentEvents_PassesSecurityEvaluation()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10),
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var evalResult = results.FirstOrDefault(static r => r.RuleName == "Security Event Evaluation (CC7.3)");
        Assert.That(evalResult, Is.Not.Null);
        Assert.That(evalResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithOldEvents_FailsSecurityEvaluation
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithOldEvents_FailsSecurityEvaluation()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-60),
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var evalResult = results.FirstOrDefault(static r => r.RuleName == "Security Event Evaluation (CC7.3)");
        Assert.That(evalResult, Is.Not.Null);
        Assert.That(evalResult.Passed, Is.False);
    }

    /// <summary>
    /// ValidateAsync_WithChangeManagement_PassesChangeLogging
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithChangeManagement_PassesChangeLogging()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "System.Change",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin",
                Action = "Modified"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var changeResult = results.FirstOrDefault(static r => r.RuleName == "Change Management Logging (CC8.1)");
        Assert.That(changeResult, Is.Not.Null);
        Assert.That(changeResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithPrivilegedAccess_PassesPrivilegedMonitoring
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithPrivilegedAccess_PassesPrivilegedMonitoring()
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
        var privResult = results.FirstOrDefault(static r => r.RuleName == "Privileged Access Monitoring (CC6.7)");
        Assert.That(privResult, Is.Not.Null);
        Assert.That(privResult.Passed, Is.True);
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
    /// ValidateAsync_WithGoodRetention_PassesRetention
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
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-400),
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var retentionResult = results.FirstOrDefault(static r => r.RuleName == "Audit Log Retention");
        Assert.That(retentionResult, Is.Not.Null);
        Assert.That(retentionResult.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithGoodRetention_PassesRetention
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
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var retentionResult = results.FirstOrDefault(static r => r.RuleName == "Audit Log Retention");
        Assert.That(retentionResult, Is.Not.Null);
        Assert.That(retentionResult.Passed, Is.False);
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
                RuleName = "Test",
                Passed = true,
                Message = "Pass"
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations, Is.Not.Empty);
        Assert.That(recommendations[0], Does.Contain("SOC 2 COMPLIANCE"));
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
                Recommendations = new List<string>()
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations.Any(static r => r.Contains("CRITICAL")), Is.True);
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

        // Assert
        Assert.That(results.Count, Is.GreaterThan(8));
        Assert.That(results.Any(static r => r.RuleName.Contains("CC6")), Is.True); // Access controls
        Assert.That(results.Any(static r => r.RuleName.Contains("CC7")), Is.True); // System operations
        Assert.That(results.Any(static r => r.RuleName.Contains("CC8")), Is.True); // Change management
    }
}