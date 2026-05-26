using MillWorks.AuditCore.EntityFramework.Options;

namespace MillWorks.AuditCore.Tests.Services;

[TestFixture]
[Category("Unit")]
public sealed class EntityFrameworkOptionsTests
{
    private const string ValidConnectionString = "Server=test;Database=test;";

    [Test]
    public void Defaults_AreCorrect()
    {
        var options = new EntityFrameworkOptions();

        Assert.That(options.ConnectionString, Is.EqualTo(string.Empty));
        Assert.That(options.MigrateOnStartup, Is.False);
        Assert.That(options.EnsureDatabaseCreated, Is.False);
        Assert.That(options.SeedInitialData, Is.False);
        Assert.That(options.FailOnMigrationError, Is.False);
        Assert.That(options.Schema, Is.EqualTo("audit"));
        Assert.That(options.MigrationTimeoutSeconds, Is.EqualTo(300));
    }

    [Test]
    public void Validator_Accepts_DefaultSchema()
    {
        var options = new EntityFrameworkOptions
        {
            ConnectionString = ValidConnectionString,
            MigrationTimeoutSeconds = 300
            // Schema defaults to "audit"
        };

        var result = new EntityFrameworkOptionsValidator().Validate(null, options);

        Assert.That(result.Succeeded, Is.True, string.Join("; ", result.Failures ?? []));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t")]
    public void Validator_Rejects_NullOrWhitespaceSchema(string? schema)
    {
        var options = new EntityFrameworkOptions
        {
            ConnectionString = ValidConnectionString,
            MigrationTimeoutSeconds = 300,
            Schema = schema!
        };

        var result = new EntityFrameworkOptionsValidator().Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(
            string.Join("\n", result.Failures ?? []),
            Does.Contain("null or whitespace"));
    }

    [TestCase("1bad")]                                                // starts with digit
    [TestCase("audit schema")]                                        // space
    [TestCase("audit-prod")]                                          // hyphen
    [TestCase("audit;--")]                                            // SQL injection attempt
    [TestCase("audit.prod")]                                          // dot
    [TestCase("audit'")]                                              // quote
    public void Validator_Rejects_InvalidIdentifierSchema(string schema)
    {
        var options = new EntityFrameworkOptions
        {
            ConnectionString = ValidConnectionString,
            MigrationTimeoutSeconds = 300,
            Schema = schema
        };

        var result = new EntityFrameworkOptionsValidator().Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(
            string.Join("\n", result.Failures ?? []),
            Does.Contain("identifier pattern"));
    }

    [Test]
    public void Validator_Rejects_IdentifierExceeding128Chars()
    {
        var schema = new string('a', 129);

        var options = new EntityFrameworkOptions
        {
            ConnectionString = ValidConnectionString,
            MigrationTimeoutSeconds = 300,
            Schema = schema
        };

        var result = new EntityFrameworkOptionsValidator().Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(
            string.Join("\n", result.Failures ?? []),
            Does.Contain("identifier pattern"));
    }

    [TestCase("dbo")]
    [TestCase("DBO")]
    [TestCase("sys")]
    [TestCase("SYS")]
    [TestCase("guest")]
    [TestCase("Guest")]
    [TestCase("INFORMATION_SCHEMA")]
    [TestCase("information_schema")]
    public void Validator_Rejects_ReservedSchema(string schema)
    {
        var options = new EntityFrameworkOptions
        {
            ConnectionString = ValidConnectionString,
            MigrationTimeoutSeconds = 300,
            Schema = schema
        };

        var result = new EntityFrameworkOptionsValidator().Validate(null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(
            string.Join("\n", result.Failures ?? []),
            Does.Contain("reserved SQL Server schema"));
    }
}
