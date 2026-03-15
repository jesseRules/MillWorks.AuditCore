using System.Reflection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.Services.Validators;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Tests.Validators;

/// <summary>
/// Unit tests for <see cref="ComplianceAttributeScanner"/>.
/// Uses test types decorated with [FERPA] and [SensitiveData] in a nested class
/// so the scanner can discover them from the test assembly.
/// </summary>
[TestFixture]
public class ComplianceAttributeScannerTests
{
    private ComplianceAttributeScanner _scanner = null!;

    [SetUp]
    public void Setup()
    {
        // Scan only the test assembly so results are deterministic
        _scanner = new ComplianceAttributeScanner(
            [typeof(ComplianceAttributeScannerTests).Assembly]);
    }

    // ── GetFerpaEntities ──

    [Test]
    public void GetFerpaEntities_FindsDecoratedTypes()
    {
        var ferpaEntities = _scanner.GetFerpaEntities();

        Assert.That(ferpaEntities, Is.Not.Empty);
        Assert.That(ferpaEntities, Has.Some.EqualTo(typeof(TestStudentEntity)));
    }

    [Test]
    public void GetFerpaEntities_DoesNotIncludeUnDecoratedTypes()
    {
        var ferpaEntities = _scanner.GetFerpaEntities();

        Assert.That(ferpaEntities, Has.None.EqualTo(typeof(TestNonFerpaEntity)));
    }

    [Test]
    public void GetFerpaEntities_ReturnsSameInstanceOnRepeatedCalls()
    {
        var first = _scanner.GetFerpaEntities();
        var second = _scanner.GetFerpaEntities();

        Assert.That(first, Is.SameAs(second));
    }

    // ── GetSensitiveProperties(ComplianceStandard) ──

    [Test]
    public void GetSensitiveProperties_ByStandard_FindsFerpaProperties()
    {
        var properties = _scanner.GetSensitiveProperties(ComplianceStandard.FERPA);

        Assert.That(properties, Is.Not.Empty);
        Assert.That(properties, Has.Some.Matches<SensitivePropertyInfo>(
            p => p.Property.Name == nameof(TestStudentEntity.StudentId)));
    }

    [Test]
    public void GetSensitiveProperties_ByStandard_RespectsApplicableStandards()
    {
        // GradeValue is only marked for FERPA, not HIPAA
        var hipaaProperties = _scanner.GetSensitiveProperties(ComplianceStandard.HIPAA);

        Assert.That(hipaaProperties, Has.None.Matches<SensitivePropertyInfo>(
            p => p.Property.Name == nameof(TestStudentEntity.GradeValue)));
    }

    [Test]
    public void GetSensitiveProperties_ByStandard_ReturnsEmptyForStandardWithNoProperties()
    {
        // STIG has no decorated properties in test types
        var stigProperties = _scanner.GetSensitiveProperties(ComplianceStandard.STIG);

        Assert.That(stigProperties, Is.Empty);
    }

    [Test]
    public void GetSensitiveProperties_ByStandard_IncludesAutoEncryptAndMaskInLogs()
    {
        var properties = _scanner.GetSensitiveProperties(ComplianceStandard.FERPA);

        var studentIdProp = properties.FirstOrDefault(
            static p => p.Property.Name == nameof(TestStudentEntity.StudentId));
        Assert.That(studentIdProp, Is.Not.Null);
        Assert.That(studentIdProp!.AutoEncrypt, Is.True);
        Assert.That(studentIdProp.MaskInLogs, Is.True);
    }

    // ── GetSensitiveProperties(Type) ──

    [Test]
    public void GetSensitiveProperties_ByType_ReturnsPropertiesForDecoratedType()
    {
        var properties = _scanner.GetSensitiveProperties(typeof(TestStudentEntity));

        Assert.That(properties, Is.Not.Empty);
        Assert.That(properties.Select(static p => p.Property.Name),
            Has.Member(nameof(TestStudentEntity.StudentId)));
        Assert.That(properties.Select(static p => p.Property.Name),
            Has.Member(nameof(TestStudentEntity.GradeValue)));
    }

    [Test]
    public void GetSensitiveProperties_ByType_ReturnsEmptyForTypeWithoutAttributes()
    {
        var properties = _scanner.GetSensitiveProperties(typeof(TestNonFerpaEntity));

        Assert.That(properties, Is.Empty);
    }

    // ── No assemblies provided (AppDomain fallback) ──

    [Test]
    public void Constructor_WithNoAssemblies_FallsBackToAppDomain()
    {
        var loggerMock = new Mock<ILogger<ComplianceAttributeScanner>>();
        var scanner = new ComplianceAttributeScanner(logger: loggerMock.Object);

        // Should not throw, and should find at least the test types loaded in AppDomain
        Assert.That(scanner.GetFerpaEntities(), Is.Not.Null);
    }

    [Test]
    public void Constructor_WithEmptyAssemblyList_FallsBackToAppDomain()
    {
        var loggerMock = new Mock<ILogger<ComplianceAttributeScanner>>();
        var scanner = new ComplianceAttributeScanner(
            Array.Empty<Assembly>(), loggerMock.Object);

        // Should log a warning
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    // ── Test fixture types ──

    [FERPA(RecordType = "StudentRecord", RequiresConsent = true)]
    public class TestStudentEntity
    {
        [SensitiveData(
            ApplicableStandards = [ComplianceStandard.FERPA],
            AutoEncrypt = true,
            MaskInLogs = true)]
        public string StudentId { get; set; } = string.Empty;

        [SensitiveData(
            ApplicableStandards = [ComplianceStandard.FERPA],
            AutoEncrypt = false,
            MaskInLogs = false)]
        public string GradeValue { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty; // Not sensitive
    }

    public class TestNonFerpaEntity
    {
        public string Id { get; set; } = string.Empty;
    }
}
