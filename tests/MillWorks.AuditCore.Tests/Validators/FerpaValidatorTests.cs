using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Validators;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// Comprehensive FERPA Validator tests covering all 14 validation rules
/// (2 Configuration meta-rules + 12 Operational rules) and GenerateRecommendations.
/// </summary>
[TestFixture]
public class FerpaValidatorTests
{
    private FerpaValidator _validator = null!;
    private Mock<IComplianceAttributeScanner> _scannerMock = null!;

    [SetUp]
    public void Setup()
    {
        _scannerMock = new Mock<IComplianceAttributeScanner>();

        // Default: scanner finds entities and properties (configuration rules pass)
        _scannerMock.Setup(static s => s.GetFerpaEntities())
            .Returns(new List<Type> { typeof(AuditEventEntity) });
        _scannerMock.Setup(static s => s.GetSensitiveProperties(ComplianceStandard.FERPA))
            .Returns(new List<SensitivePropertyInfo>
            {
                new(typeof(AuditEventEntity),
                    typeof(AuditEventEntity).GetProperty(nameof(AuditEventEntity.User))!,
                    [ComplianceStandard.FERPA], AutoEncrypt: true, MaskInLogs: true)
            });

        _validator = new FerpaValidator(_scannerMock.Object);
    }

    // ── Standard Property ──

    [Test]
    public void Standard_ReturnsFerpa()
    {
        Assert.That(_validator.Standard, Is.EqualTo(ComplianceStandard.FERPA));
    }

    // ── Rule: FERPA Entity Configuration (§99.31) — Configuration ──

    [Test]
    public async Task EntityConfiguration_WithFerpaEntities_Passes()
    {
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(CreateEvents()));
        var rule = FindRule(results, "FERPA Entity Configuration");

        Assert.That(rule.Passed, Is.True);
        Assert.That(rule.Category, Is.EqualTo("Configuration"));
        Assert.That(rule.ComplianceStandard, Is.EqualTo("FERPA"));
    }

    [Test]
    public async Task EntityConfiguration_WithNoFerpaEntities_Fails()
    {
        _scannerMock.Setup(static s => s.GetFerpaEntities()).Returns(new List<Type>());

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(CreateEvents()));
        var rule = FindRule(results, "FERPA Entity Configuration");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.High));
        Assert.That(rule.Recommendations, Is.Not.Empty);
    }

    // ── Rule: FERPA Sensitive Data Protection (§99.31) — Configuration ──

    [Test]
    public async Task SensitiveDataProtection_WithFerpaProperties_Passes()
    {
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(CreateEvents()));
        var rule = FindRule(results, "FERPA Sensitive Data Protection");

        Assert.That(rule.Passed, Is.True);
        Assert.That(rule.Category, Is.EqualTo("Configuration"));
    }

    [Test]
    public async Task SensitiveDataProtection_WithNoFerpaProperties_Fails()
    {
        _scannerMock.Setup(static s => s.GetSensitiveProperties(ComplianceStandard.FERPA))
            .Returns(new List<SensitivePropertyInfo>());

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(CreateEvents()));
        var rule = FindRule(results, "FERPA Sensitive Data Protection");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ── Rule 1: Education Records Access Logging (§99.10) ──

    [Test]
    public async Task EducationRecordsAccess_WithStudentEvents_Passes()
    {
        var events = CreateEvents(("Student.DataAccess", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Education Records Access Logging");

        Assert.That(rule.Passed, Is.True);
        Assert.That(rule.RegulationReference, Does.Contain("§99.10"));
    }

    [Test]
    public async Task EducationRecordsAccess_WithEnrollmentEvents_Passes()
    {
        var events = CreateEvents(("Enrollment.Created", "registrar"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Education Records Access Logging");

        Assert.That(rule.Passed, Is.True);
    }

    [Test]
    public async Task EducationRecordsAccess_WithTranscriptEvents_Passes()
    {
        var events = CreateEvents(("Transcript.Requested", "student"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Education Records Access Logging");

        Assert.That(rule.Passed, Is.True);
    }

    [Test]
    public async Task EducationRecordsAccess_WithGradeEvents_Passes()
    {
        var events = CreateEvents(("Grade.Updated", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Education Records Access Logging").Passed, Is.True);
    }

    [Test]
    public async Task EducationRecordsAccess_WithNoRelevantEvents_Fails()
    {
        var events = CreateEvents(("User.Login", "user"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Education Records Access Logging");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ── Rule 2: Unique User Identification (§99.31(a)) ──

    [Test]
    public async Task UniqueUserIdentification_AllEventsHaveUser_Passes()
    {
        var events = CreateEvents(("Test", "user1"), ("Test", "user2"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Unique User Identification").Passed, Is.True);
    }

    [Test]
    public async Task UniqueUserIdentification_EventMissingUser_Fails()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", User = "user1", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "Test", User = null, InsertedDate = DateTimeOffset.UtcNow }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Unique User Identification");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Critical));
        Assert.That(rule.FailedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task UniqueUserIdentification_EmptyWhitespaceUser_Fails()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", User = "  ", InsertedDate = DateTimeOffset.UtcNow }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Unique User Identification").Passed, Is.False);
    }

    // ── Rule 3: Prior Written Consent Tracking (§99.30) ──

    [Test]
    public async Task ConsentTracking_WithConsentEvents_Passes()
    {
        var events = CreateEvents(("Consent.Granted", "admin"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Prior Written Consent Tracking").Passed, Is.True);
    }

    [Test]
    public async Task ConsentTracking_WithParentConsentEvents_Passes()
    {
        var events = CreateEvents(("ParentConsent.Recorded", "admin"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Prior Written Consent Tracking").Passed, Is.True);
    }

    [Test]
    public async Task ConsentTracking_WithAuthorizationEvents_Passes()
    {
        var events = CreateEvents(("Authorization.Updated", "admin"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Prior Written Consent Tracking").Passed, Is.True);
    }

    [Test]
    public async Task ConsentTracking_WithNoConsentEvents_Fails()
    {
        var events = CreateEvents(("Student.Read", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Prior Written Consent Tracking");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    // ── Rule 4: Directory Information Opt-Out (§99.37) ──

    [Test]
    public async Task DirectoryOptOut_WithOptOutEvents_Passes()
    {
        var events = CreateEvents(("DirectoryOptOut.Requested", "student"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Directory Information Opt-Out").Passed, Is.True);
    }

    [Test]
    public async Task DirectoryOptOut_WithNoOptOutEvents_Fails()
    {
        var events = CreateEvents(("Student.Read", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Directory Information Opt-Out");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    // ── Rule 5: Legitimate Educational Interest (§99.31(a)(1)) ──

    [Test]
    public async Task LegitimateInterest_WithJustificationEvents_Passes()
    {
        var events = CreateEvents(("LegitimateInterest.Documented", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Legitimate Educational Interest").Passed, Is.True);
    }

    [Test]
    public async Task LegitimateInterest_WithAccessJustification_Passes()
    {
        var events = CreateEvents(("AccessJustification.Recorded", "admin"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Legitimate Educational Interest").Passed, Is.True);
    }

    [Test]
    public async Task LegitimateInterest_WithNoJustificationEvents_Fails()
    {
        var events = CreateEvents(("Student.Read", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Legitimate Educational Interest");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    // ── Rule 6: Disclosure Logging (§99.32) ──

    [Test]
    public async Task DisclosureLogging_WithDisclosureEvents_Passes()
    {
        var events = CreateEvents(("Disclosure.ThirdParty", "registrar"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Disclosure Logging").Passed, Is.True);
    }

    [Test]
    public async Task DisclosureLogging_WithDataSharingEvents_Passes()
    {
        var events = CreateEvents(("DataSharing.External", "admin"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Disclosure Logging").Passed, Is.True);
    }

    [Test]
    public async Task DisclosureLogging_WithNoDisclosureEvents_Fails()
    {
        var events = CreateEvents(("Student.Read", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Disclosure Logging").Passed, Is.False);
    }

    // ── Rule 7: Annual Notification (§99.7) ──

    [Test]
    public async Task AnnualNotification_WithNotificationEvents_Passes()
    {
        var events = CreateEvents(("AnnualNotice.Sent", "system"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Annual Notification").Passed, Is.True);
    }

    [Test]
    public async Task AnnualNotification_WithFerpaNotificationEvents_Passes()
    {
        var events = CreateEvents(("FerpaNotification.Sent", "system"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Annual Notification").Passed, Is.True);
    }

    [Test]
    public async Task AnnualNotification_WithNoNotificationEvents_Fails()
    {
        var events = CreateEvents(("Student.Read", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Annual Notification");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Low));
    }

    // ── Rule 8: Retention Compliance (§99.32(a)(2)) ──

    [Test]
    public async Task Retention_WithFiveYearsOfData_Passes()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", User = "user", InsertedDate = DateTimeOffset.UtcNow.AddYears(-6) },
            new() { EventType = "Test", User = "user", InsertedDate = DateTimeOffset.UtcNow }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Retention Compliance");

        Assert.That(rule.Passed, Is.True);
        Assert.That(rule.Message, Does.Contain("meets 5-year retention"));
    }

    [Test]
    public async Task Retention_SystemYoungerThanFiveYears_PassesWithInfo()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Test", User = "user", InsertedDate = DateTimeOffset.UtcNow.AddDays(-100) },
            new() { EventType = "Test", User = "user", InsertedDate = DateTimeOffset.UtcNow }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Retention Compliance");

        Assert.That(rule.Passed, Is.True);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Info));
        Assert.That(rule.Message, Does.Contain("will be fully verifiable"));
    }

    [Test]
    public async Task Retention_EmptyEvents_Fails()
    {
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(new List<AuditEventEntity>()));
        var rule = FindRule(results, "Retention Compliance");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    // ── Rule 9: Integrity Controls ──

    [Test]
    public async Task Integrity_WithIntegrityRecords_Passes()
    {
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventType = "Test", User = "user", InsertedDate = DateTimeOffset.UtcNow,
                AuditIntegrity = new AuditIntegrityEntity { EventId = Guid.NewGuid() }
            }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Integrity Controls").Passed, Is.True);
    }

    [Test]
    public async Task Integrity_NoIntegrity_TamperDetectionEnabled_Critical()
    {
        var securityOptions = new SecurityOptions { EnableTamperDetection = true };
        var validator = new FerpaValidator(_scannerMock.Object, securityOptions);

        var events = CreateEvents(("Test", "user"));
        var results = await validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Integrity Controls");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Critical));
        Assert.That(rule.Message, Does.Contain("Tamper detection is enabled"));
    }

    [Test]
    public async Task Integrity_NoIntegrity_TamperDetectionDisabled_Info()
    {
        var securityOptions = new SecurityOptions { EnableTamperDetection = false };
        var validator = new FerpaValidator(_scannerMock.Object, securityOptions);

        var events = CreateEvents(("Test", "user"));
        var results = await validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Integrity Controls");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Info));
        Assert.That(rule.Message, Does.Contain("not enabled"));
    }

    [Test]
    public async Task Integrity_NoIntegrity_NoSecurityOptions_Info()
    {
        // Default constructor — no SecurityOptions injected
        var validator = new FerpaValidator(_scannerMock.Object);

        var events = CreateEvents(("Test", "user"));
        var results = await validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Integrity Controls");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Info));
    }

    // ── Rule 10: De-identification for Research (§99.31(b)) ──

    [Test]
    public async Task DeIdentification_NoResearchEvents_PassesNotApplicable()
    {
        var events = CreateEvents(("Student.Read", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "De-identification for Research");

        Assert.That(rule.Passed, Is.True);
        Assert.That(rule.Message, Does.Contain("not applicable"));
    }

    [Test]
    public async Task DeIdentification_ResearchWithDeIdentification_Passes()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Research.DataExport", User = "researcher", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "DeIdentified.DataSet", User = "system", InsertedDate = DateTimeOffset.UtcNow }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "De-identification for Research");

        Assert.That(rule.Passed, Is.True);
    }

    [Test]
    public async Task DeIdentification_ResearchWithoutDeIdentification_Fails()
    {
        var events = CreateEvents(("Research.DataExport", "researcher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "De-identification for Research");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.Medium));
    }

    // ── Rule 11: Security Incident Tracking ──

    [Test]
    public async Task SecurityIncident_WithIncidentEvents_PassesWithCount()
    {
        var events = CreateEvents(("Security.Breach", "admin"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Security Incident Tracking");

        Assert.That(rule.Passed, Is.True);
        Assert.That(rule.Message, Does.Contain("being tracked"));
    }

    [Test]
    public async Task SecurityIncident_WithNoIncidents_StillPasses()
    {
        var events = CreateEvents(("Student.Read", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Security Incident Tracking");

        // Security incident tracking always passes — having no incidents is acceptable
        Assert.That(rule.Passed, Is.True);
        Assert.That(rule.FailedCount, Is.EqualTo(0));
    }

    // ── Rule 12: Login/Authentication Monitoring ──

    [Test]
    public async Task LoginMonitoring_WithLoginEvents_Passes()
    {
        var events = CreateEvents(("User.Login", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Login/Authentication Monitoring").Passed, Is.True);
    }

    [Test]
    public async Task LoginMonitoring_WithAuthenticationEvents_Passes()
    {
        var events = CreateEvents(("Authentication.Success", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(FindRule(results, "Login/Authentication Monitoring").Passed, Is.True);
    }

    [Test]
    public async Task LoginMonitoring_WithFailedLogins_CountsFailures()
    {
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "User.Login", User = "user", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "User.LoginFailed", User = "hacker", InsertedDate = DateTimeOffset.UtcNow }
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Login/Authentication Monitoring");

        Assert.That(rule.Passed, Is.True);
        Assert.That(rule.Message, Does.Contain("1 failed"));
    }

    [Test]
    public async Task LoginMonitoring_WithNoLoginEvents_Fails()
    {
        var events = CreateEvents(("Student.Read", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));
        var rule = FindRule(results, "Login/Authentication Monitoring");

        Assert.That(rule.Passed, Is.False);
        Assert.That(rule.Severity, Is.EqualTo(ValidationSeverity.High));
    }

    // ── Empty Events Edge Case ──

    [Test]
    public async Task ValidateAsync_EmptyEventList_AllRulesReturnResults()
    {
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(new List<AuditEventEntity>()));

        // Should have all 14 rules (2 config + 12 operational)
        Assert.That(results.Count, Is.EqualTo(14));
        Assert.That(results.All(static r => r.ComplianceStandard == "FERPA"), Is.True);
    }

    // ── All Results Have Required Metadata ──

    [Test]
    public async Task ValidateAsync_AllResultsHaveComplianceStandard()
    {
        var events = CreateEvents(("Student.DataAccess", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(results.All(static r => r.ComplianceStandard == "FERPA"), Is.True);
    }

    [Test]
    public async Task ValidateAsync_AllResultsHaveRegulationReference()
    {
        var events = CreateEvents(("Student.DataAccess", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(results.All(static r => !string.IsNullOrEmpty(r.RegulationReference)), Is.True);
    }

    [Test]
    public async Task ValidateAsync_AllResultsHaveCategory()
    {
        var events = CreateEvents(("Student.DataAccess", "teacher"));
        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(results.All(static r =>
            r.Category == "Configuration" || r.Category == "Operational"), Is.True);
    }

    [Test]
    public async Task ValidateAsync_ReturnsAllExpectedRules()
    {
        // Provide a comprehensive set of events that triggers all rules
        var events = new List<AuditEventEntity>
        {
            new() { EventType = "Student.DataAccess", User = "teacher", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "Consent.Granted", User = "admin", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "DirectoryOptOut.Requested", User = "student", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "LegitimateInterest.Documented", User = "teacher", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "Disclosure.External", User = "registrar", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "AnnualNotice.Sent", User = "system", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "User.Login", User = "teacher", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventType = "Security.Alert", User = "system", InsertedDate = DateTimeOffset.UtcNow },
        };

        var results = await _validator.ValidateAsync(ComplianceValidationContext.FromEvents(events));

        Assert.That(results.Count, Is.EqualTo(14));
        Assert.That(results.Count(static r => r.Category == "Configuration"), Is.EqualTo(2));
        Assert.That(results.Count(static r => r.Category == "Operational"), Is.EqualTo(12));
    }

    // ── GenerateRecommendations Tests ──

    [Test]
    public void GenerateRecommendations_AllPassing_ReturnsComplianceMessage()
    {
        var results = new List<AuditValidationResult>
        {
            new() { RuleName = "Test", Passed = true, Severity = ValidationSeverity.Info }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations, Is.Not.Empty);
        Assert.That(recommendations[0], Does.Contain("FERPA COMPLIANCE"));
        Assert.That(recommendations.Any(static r =>
            r.Contains("Ongoing", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void GenerateRecommendations_CriticalFailures_IncludesCriticalSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Unique User Identification (§99.31(a))",
                Passed = false,
                Severity = ValidationSeverity.Critical,
                Category = "Operational",
                RegulationReference = "34 CFR §99.31(a)",
                Message = "Events lack user identification",
                FailedCount = 5,
                TotalCount = 10,
                Recommendations = ["Fix immediately"]
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations.Any(static r => r.Contains("CRITICAL")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("IMMEDIATE")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("federal funding")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_HighFailures_IncludesHighSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Education Records Access Logging (§99.10)",
                Passed = false,
                Severity = ValidationSeverity.High,
                Category = "Operational",
                RegulationReference = "34 CFR §99.10",
                Message = "No access events",
                Recommendations = ["Instrument access paths"]
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations.Any(static r => r.Contains("HIGH PRIORITY")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_MediumFailures_IncludesMediumSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Consent Tracking (§99.30)",
                Passed = false,
                Severity = ValidationSeverity.Medium,
                Category = "Operational",
                RegulationReference = "34 CFR §99.30",
                Message = "No consent events",
                Recommendations = ["Implement consent tracking"]
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations.Any(static r => r.Contains("MEDIUM PRIORITY")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_LowFailures_IncludesLowSection()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Annual Notification (§99.7)",
                Passed = false,
                Severity = ValidationSeverity.Low,
                Category = "Operational",
                RegulationReference = "34 CFR §99.7",
                Message = "No notification events"
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations.Any(static r => r.Contains("LOW PRIORITY")), Is.True);
    }

    [Test]
    public void GenerateRecommendations_WithFailures_IncludesFerpaResources()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Test",
                Passed = false,
                Severity = ValidationSeverity.Medium,
                Category = "Operational",
                RegulationReference = "§99.10",
                Message = "Failed"
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        Assert.That(recommendations.Any(static r =>
            r.Contains("studentprivacy.ed.gov")), Is.True);
        Assert.That(recommendations.Any(static r =>
            r.Contains("funding", StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public void GenerateRecommendations_MixedSeverities_GroupsCorrectly()
    {
        var results = new List<AuditValidationResult>
        {
            new()
            {
                RuleName = "Critical Rule", Passed = false, Severity = ValidationSeverity.Critical,
                Category = "Operational", RegulationReference = "§99.31",
                Message = "Critical issue", Recommendations = ["Fix now"]
            },
            new()
            {
                RuleName = "Medium Rule", Passed = false, Severity = ValidationSeverity.Medium,
                Category = "Operational", RegulationReference = "§99.30",
                Message = "Medium issue", Recommendations = ["Fix soon"]
            },
            new()
            {
                RuleName = "Passing Rule", Passed = true, Severity = ValidationSeverity.Info,
                Message = "All good"
            }
        };

        var recommendations = _validator.GenerateRecommendations(results);

        // Should have both critical and medium sections, but not "all passed"
        Assert.That(recommendations.Any(static r => r.Contains("CRITICAL")), Is.True);
        Assert.That(recommendations.Any(static r => r.Contains("MEDIUM")), Is.True);
        Assert.That(recommendations[0], Does.Not.Contain("All validation checks passed"));
        Assert.That(recommendations.Any(static r => r.Contains("2 compliance issue")), Is.True);
    }

    // ── Helpers ──

    private static List<AuditEventEntity> CreateEvents(params (string eventType, string user)[] events)
    {
        return events.Select(static e => new AuditEventEntity
        {
            EventType = e.eventType,
            User = e.user,
            InsertedDate = DateTimeOffset.UtcNow
        }).ToList();
    }

    private static AuditValidationResult FindRule(List<AuditValidationResult> results, string ruleNameContains)
    {
        var rule = results.FirstOrDefault(r => r.RuleName.Contains(ruleNameContains));
        Assert.That(rule, Is.Not.Null, $"Expected to find rule containing '{ruleNameContains}'");
        return rule!;
    }
}
