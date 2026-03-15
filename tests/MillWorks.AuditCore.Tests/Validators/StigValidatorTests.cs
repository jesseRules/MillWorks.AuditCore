using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// DISA STIG Validator tests
/// </summary>
[TestFixture]
public class StigValidatorTests
{
    /// <summary>
    /// Validator instance
    /// </summary>
    private StigValidator _validator;

    /// <summary>
    /// Setup
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _validator = new StigValidator();
    }

    // ===== AU-12 / V-222582 - Audit Generation =====

    /// <summary>
    /// ValidateAsync_WithEvents_PassesAuditGeneration
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithEvents_PassesAuditGeneration()
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
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-12"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
        Assert.That(result.ComplianceStandard, Is.EqualTo("DISA STIG"));
    }

    /// <summary>
    /// ValidateAsync_WithNoEvents_FailsAuditGeneration
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithNoEvents_FailsAuditGeneration()
    {
        // Arrange
        var events = new List<AuditEventEntity>();

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-12"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Critical));
    }

    // ===== AU-3 / V-222576 - Content of Audit Records =====

    /// <summary>
    /// ValidateAsync_WithCompleteRecords_PassesAuditContent
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithCompleteRecords_PassesAuditContent()
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
        var result = results.FirstOrDefault(static r => r.RuleName == "Content of Audit Records (V-222576 / AU-3)");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithMissingUser_FailsAuditContent
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithMissingUser_FailsAuditContent()
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
        var result = results.FirstOrDefault(static r => r.RuleName == "Content of Audit Records (V-222576 / AU-3)");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Critical));
    }

    /// <summary>
    /// ValidateAsync_WithMissingEventType_FailsAuditContent
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithMissingEventType_FailsAuditContent()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = null,
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName == "Content of Audit Records (V-222576 / AU-3)");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
    }

    /// <summary>
    /// ValidateAsync_WithMissingTimestamp_FailsAuditContent
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithMissingTimestamp_FailsAuditContent()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = null,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName == "Content of Audit Records (V-222576 / AU-3)");
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
    }

    // ===== AU-3(1) / V-222577 - Additional Audit Information =====

    /// <summary>
    /// ValidateAsync_WithFullAdditionalInfo_PassesAdditionalAuditInfo
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithFullAdditionalInfo_PassesAdditionalAuditInfo()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                IpAddress = "192.168.1.1",
                MachineName = "web-server-01",
                Action = "Added"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-3(1)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithMissingIpAddress_FailsAdditionalAuditInfo
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithMissingIpAddress_FailsAdditionalAuditInfo()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                IpAddress = null,
                MachineName = "web-server-01",
                Action = "Added"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-3(1)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    /// <summary>
    /// ValidateAsync_WithAssemblyNameInsteadOfMachineName_PassesAdditionalAuditInfo
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithAssemblyNameInsteadOfMachineName_PassesAdditionalAuditInfo()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                IpAddress = "10.0.0.1",
                MachineName = null,
                AssemblyName = "MillWorks.AuditCore.Services",
                Action = "Modified"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-3(1)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    // ===== AU-8 / V-222578 - Time Stamps =====

    /// <summary>
    /// ValidateAsync_WithTimestamps_PassesTimeStampCheck
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithTimestamps_PassesTimeStampCheck()
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
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-8"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithMissingTimestamps_FailsTimeStampCheck
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithMissingTimestamps_FailsTimeStampCheck()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = null,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-8"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ===== AU-9 / V-222579 - Protection of Audit Information =====

    /// <summary>
    /// ValidateAsync_WithIntegrityProtection_PassesAuditProtection
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithIntegrityProtection_PassesAuditProtection()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-9)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithoutIntegrityProtection_FailsAuditProtection
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithoutIntegrityProtection_FailsAuditProtection()
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
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-9)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Critical));
    }

    // ===== AU-9(2) / V-222580 - Audit Backup =====

    /// <summary>
    /// ValidateAsync_WithArchiveEvents_PassesAuditBackup
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithArchiveEvents_PassesAuditBackup()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Audit.Archive",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "system"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-9(2)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithBackupEvents_PassesAuditBackup
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithBackupEvents_PassesAuditBackup()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "System.Backup",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "system"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-9(2)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithoutArchiveEvents_FailsAuditBackup
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithoutArchiveEvents_FailsAuditBackup()
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
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-9(2)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    // ===== AU-11 / V-222581 - Audit Record Retention =====

    /// <summary>
    /// ValidateAsync_WithOneYearRetention_PassesRetention
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithOneYearRetention_PassesRetention()
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
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-11"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
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
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-11"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ===== AU-12(1) / V-222583 - System-Wide Audit Trail =====

    /// <summary>
    /// ValidateAsync_WithCorrelationIds_PassesSystemWideAuditTrail
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithCorrelationIds_PassesSystemWideAuditTrail()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                CorrelationId = Guid.NewGuid().ToString()
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-12(1)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithoutCorrelationIds_FailsSystemWideAuditTrail
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithoutCorrelationIds_FailsSystemWideAuditTrail()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                CorrelationId = null
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-12(1)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
    }

    // ===== AU-2 / V-222569 - Auditable Events =====

    /// <summary>
    /// ValidateAsync_WithAllAuditableEventCategories_PassesAuditableEvents
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithAllAuditableEventCategories_PassesAuditableEvents()
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
                EventType = "User.Logout",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            },
            new()
            {
                EventType = "Admin.RoleChange",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin"
            },
            new()
            {
                EventType = "Record.Create",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                Action = "Added"
            },
            new()
            {
                EventType = "System.PolicyChange",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-2)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.Info));
    }

    /// <summary>
    /// ValidateAsync_WithOnlyLogonAndObjectAccess_PassesMinimumAuditableEvents
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithOnlyLogonAndObjectAccess_PassesMinimumAuditableEvents()
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
                EventType = "Record.Created",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                Action = "Added"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-2)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithNoLogonEvents_FailsAuditableEvents
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithNoLogonEvents_FailsAuditableEvents()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Record.Updated",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                Action = "Modified"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-2)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ===== AU-2(3) / V-222570 - Reviews of Auditable Events =====

    /// <summary>
    /// ValidateAsync_WithRecentEvents_PassesActiveMonitoring
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithRecentEvents_PassesActiveMonitoring()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5),
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-2(3)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithOnlyOldEvents_FailsActiveMonitoring
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithOnlyOldEvents_FailsActiveMonitoring()
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
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AU-2(3)"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
    }

    // ===== AU-3 / V-222574 - Privileged Function Execution =====

    /// <summary>
    /// ValidateAsync_WithAdminEvents_PassesPrivilegedFunctionExecution
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithAdminEvents_PassesPrivilegedFunctionExecution()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Admin.ConfigChange",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("Privileged Function Execution"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithAdminUser_PassesPrivilegedFunctionExecution
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithAdminUser_PassesPrivilegedFunctionExecution()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Record.Updated",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin@example.com"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("Privileged Function Execution"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithNoPrivilegedEvents_FailsPrivilegedFunctionExecution
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithNoPrivilegedEvents_FailsPrivilegedFunctionExecution()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Record.View",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "regularuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("Privileged Function Execution"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ===== AC-2 / V-222534 - Account Management =====

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
                EventType = "Account.Created",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-2"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithUserEntityChanges_PassesAccountManagement
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithUserEntityChanges_PassesAccountManagement()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Entity.Modified",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin",
                EntityType = "User",
                Action = "Added"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-2"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithNoAccountEvents_FailsAccountManagement
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithNoAccountEvents_FailsAccountManagement()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Record.View",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-2"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ===== AC-7 / V-222542 - Unsuccessful Logon Attempts =====

    /// <summary>
    /// ValidateAsync_WithFailedLogins_TracksFailedLogons
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithFailedLogins_TracksFailedLogons()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.LoginFailed",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "attacker"
            },
            new()
            {
                EventType = "User.AccountLocked",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "attacker"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-7"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithNoFailedLogins_StillPasses
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithNoFailedLogins_StillPasses()
    {
        // Arrange - no failed logins is acceptable (none may have occurred)
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
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-7"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    // ===== AC-8 / V-222543 - System Use Notification =====

    /// <summary>
    /// ValidateAsync_WithBannerAcknowledgment_PassesSystemUseNotification
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithBannerAcknowledgment_PassesSystemUseNotification()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "System.BannerAccepted",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-8"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithConsentEvents_PassesSystemUseNotification
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithConsentEvents_PassesSystemUseNotification()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.ConsentGranted",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-8"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithNoBannerEvents_FailsSystemUseNotification
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithNoBannerEvents_FailsSystemUseNotification()
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
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-8"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ===== AC-17 / V-222553 - Remote Access =====

    /// <summary>
    /// ValidateAsync_WithRemoteAccessEvents_PassesRemoteAccess
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithRemoteAccessEvents_PassesRemoteAccess()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Remote.VPNConnect",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-17"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithIpAddressTracking_PassesRemoteAccess
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithIpAddressTracking_PassesRemoteAccess()
    {
        // Arrange - no explicit remote events, but IP tracking is active
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                IpAddress = "10.0.0.50"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-17"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithApiEvents_PassesRemoteAccess
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithApiEvents_PassesRemoteAccess()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "API.Request",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "serviceaccount"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-17"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithNoRemoteAccessOrIpTracking_FailsRemoteAccess
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithNoRemoteAccessOrIpTracking_FailsRemoteAccess()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Record.View",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser",
                IpAddress = null
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("AC-17"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.False);
        Assert.That(result.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ===== SI-4 - Security Incident Tracking =====

    /// <summary>
    /// ValidateAsync_WithSecurityEvents_TracksIncidents
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithSecurityEvents_TracksIncidents()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Security.Alert",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "system"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("SI-4"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithNoSecurityEvents_StillPasses
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithNoSecurityEvents_StillPasses()
    {
        // Arrange - having no security incidents is acceptable
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
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("SI-4"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    // ===== IA-5 - Authenticator Management =====

    /// <summary>
    /// ValidateAsync_WithCredentialChangeEvents_PassesAuthenticatorManagement
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithCredentialChangeEvents_PassesAuthenticatorManagement()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.PasswordChanged",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("IA-5"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithMfaEvents_PassesAuthenticatorManagement
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithMfaEvents_PassesAuthenticatorManagement()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.MFA.Enrolled",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("IA-5"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithCacPkiEvents_PassesAuthenticatorManagement
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithCacPkiEvents_PassesAuthenticatorManagement()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.CAC.Registered",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert
        var result = results.FirstOrDefault(static r => r.RuleName.Contains("IA-5"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Passed, Is.True);
    }

    // ===== Standard Property =====

    /// <summary>
    /// Standard_ReturnsStig
    /// </summary>
    [Test]
    public void Standard_ReturnsStig()
    {
        Assert.That(_validator.Standard, Is.EqualTo(ComplianceStandard.STIG));
    }

    // ===== GenerateRecommendations =====

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
        Assert.That(recommendations[0], Does.Contain("DISA STIG COMPLIANCE"));
    }

    /// <summary>
    /// GenerateRecommendations_WithCriticalFailures_IncludesCatISection
    /// </summary>
    [Test]
    public void GenerateRecommendations_WithCriticalFailures_IncludesCatISection()
    {
        // Arrange
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Critical Finding",
                Passed = false,
                Severity = ValidationSeverity.Critical,
                Message = "Critical failure",
                RegulationReference = "NIST 800-53 AU-12",
                Recommendations = ["Fix immediately"]
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations.Any(static r => r.Contains("CAT I")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("CRITICAL")), Is.True);
    }

    /// <summary>
    /// GenerateRecommendations_WithHighFailures_IncludesCatIISection
    /// </summary>
    [Test]
    public void GenerateRecommendations_WithHighFailures_IncludesCatIISection()
    {
        // Arrange
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "High Finding",
                Passed = false,
                Severity = ValidationSeverity.High,
                Message = "High priority",
                RegulationReference = "NIST 800-53 AC-2",
                Recommendations = ["Address within 30 days"]
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations.Any(static r => r.Contains("CAT II")), Is.True);
    }

    /// <summary>
    /// GenerateRecommendations_WithMediumFailures_IncludesCatIIISection
    /// </summary>
    [Test]
    public void GenerateRecommendations_WithMediumFailures_IncludesCatIIISection()
    {
        // Arrange
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Medium Finding",
                Passed = false,
                Severity = ValidationSeverity.Medium,
                Message = "Medium priority",
                RegulationReference = "NIST 800-53 AU-9(2)",
                Recommendations = ["Address within 90 days"]
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations.Any(static r => r.Contains("CAT III")), Is.True);
    }

    /// <summary>
    /// GenerateRecommendations_WithFailures_IncludesAtoGuidance
    /// </summary>
    [Test]
    public void GenerateRecommendations_WithFailures_IncludesAtoGuidance()
    {
        // Arrange
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Test",
                Passed = false,
                Severity = ValidationSeverity.High,
                Message = "Fail",
                RegulationReference = "Test",
                Recommendations = ["Fix"]
            }
        };

        // Act
        var recommendations = _validator.GenerateRecommendations(results);

        // Assert
        Assert.That(recommendations.Any(static r => r.Contains("ATO")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("POA&M")), Is.True);
    }

    // ===== Comprehensive Validation =====

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
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "testuser"
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert - should have all 17 controls
        Assert.That(results.Count, Is.GreaterThanOrEqualTo(17));
        Assert.That(results.Any(static r => r.RuleName.Contains("AU-12")), Is.True); // Audit generation
        Assert.That(results.Any(static r => r.RuleName.Contains("AU-3")), Is.True); // Content of audit records
        Assert.That(results.Any(static r => r.RuleName.Contains("AU-8")), Is.True); // Time stamps
        Assert.That(results.Any(static r => r.RuleName.Contains("AU-9")), Is.True); // Protection
        Assert.That(results.Any(static r => r.RuleName.Contains("AU-11")), Is.True); // Retention
        Assert.That(results.Any(static r => r.RuleName.Contains("AU-2")), Is.True); // Auditable events
        Assert.That(results.Any(static r => r.RuleName.Contains("AC-2")), Is.True); // Account management
        Assert.That(results.Any(static r => r.RuleName.Contains("AC-7")), Is.True); // Failed logons
        Assert.That(results.Any(static r => r.RuleName.Contains("AC-8")), Is.True); // System use notification
        Assert.That(results.Any(static r => r.RuleName.Contains("AC-17")), Is.True); // Remote access
        Assert.That(results.Any(static r => r.RuleName.Contains("SI-4")), Is.True); // Security monitoring
        Assert.That(results.Any(static r => r.RuleName.Contains("IA-5")), Is.True); // Authenticator mgmt
    }

    /// <summary>
    /// ValidateAsync_AllResultsHaveComplianceStandard
    /// </summary>
    [Test]
    public async Task ValidateAsync_AllResultsHaveComplianceStandard()
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
        Assert.That(results.All(static r => r.ComplianceStandard == "DISA STIG"), Is.True);
    }

    /// <summary>
    /// ValidateAsync_WithFullyCompliantEvents_MaximizesPassRate
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithFullyCompliantEvents_MaximizesPassRate()
    {
        // Arrange - events that satisfy as many controls as possible
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin",
                IpAddress = "10.0.0.1",
                MachineName = "web-server-01",
                Action = "Added",
                CorrelationId = Guid.NewGuid().ToString(),
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            },
            new()
            {
                EventType = "User.Logout",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin",
                IpAddress = "10.0.0.1",
                MachineName = "web-server-01",
                Action = "Modified",
                CorrelationId = Guid.NewGuid().ToString(),
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            },
            new()
            {
                EventType = "Admin.RoleChange",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin",
                IpAddress = "10.0.0.1",
                MachineName = "web-server-01",
                Action = "Modified",
                CorrelationId = Guid.NewGuid().ToString(),
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            },
            new()
            {
                EventType = "System.PolicyChange",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin",
                IpAddress = "10.0.0.1",
                MachineName = "web-server-01",
                Action = "Modified",
                CorrelationId = Guid.NewGuid().ToString(),
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            },
            new()
            {
                EventType = "Account.Created",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin",
                IpAddress = "10.0.0.1",
                MachineName = "web-server-01",
                Action = "Added",
                CorrelationId = Guid.NewGuid().ToString(),
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            },
            new()
            {
                EventType = "User.ConsentGranted",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin",
                IpAddress = "10.0.0.1",
                MachineName = "web-server-01",
                Action = "Added",
                CorrelationId = Guid.NewGuid().ToString(),
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            },
            new()
            {
                EventType = "Audit.Archive",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "system",
                IpAddress = "10.0.0.1",
                MachineName = "web-server-01",
                Action = "Added",
                CorrelationId = Guid.NewGuid().ToString(),
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            },
            new()
            {
                EventType = "User.PasswordChanged",
                InsertedDate = DateTimeOffset.UtcNow,
                User = "admin",
                IpAddress = "10.0.0.1",
                MachineName = "web-server-01",
                Action = "Modified",
                CorrelationId = Guid.NewGuid().ToString(),
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            },
            new()
            {
                // Old event for retention
                EventType = "System.Startup",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-400),
                User = "system",
                IpAddress = "10.0.0.1",
                MachineName = "web-server-01",
                Action = "Added",
                CorrelationId = Guid.NewGuid().ToString(),
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            }
        };

        // Act
        var results = await _validator.ValidateAsync(events);

        // Assert - all controls should pass
        var failedResults = results.Where(static r => !r.Passed).ToList();
        Assert.That(failedResults, Is.Empty,
            $"Expected all controls to pass but these failed: {string.Join(", ", failedResults.Select(static r => r.RuleName))}");
    }
}
