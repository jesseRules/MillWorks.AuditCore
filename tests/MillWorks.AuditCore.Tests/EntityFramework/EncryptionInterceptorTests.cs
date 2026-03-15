using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Conversion;
using MillWorks.AuditCore.EntityFramework.Extensions;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Tests for field-level encryption via EF Core value converters.
/// Uses SQLite in-memory — InMemory provider skips ValueConverter when TModel == TProvider.
/// </summary>
[TestFixture]
public class EncryptionValueConverterTests : IDisposable
{
    private SqliteConnection _connection;
    private FakeFieldEncryptionService _encryptionService;
    private DbContextOptions<EncryptionTestDbContext> _dbOptions;
    private EncryptionTestDbContext _dbContext;

    /// <summary>
    /// Setup test fixture
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _encryptionService = new FakeFieldEncryptionService();

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<EncryptionTestDbContext>()
            .UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .ConfigureWarnings(static w =>
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        _dbContext = new EncryptionTestDbContext(_dbOptions, _encryptionService);
        _dbContext.Database.EnsureCreated();
    }

    /// <summary>
    /// Tear down test fixture
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Test]
    public void ConverterIsRegisteredOnModel()
    {
        var model = _dbContext.Model;
        var entityType = model.FindEntityType(typeof(EncryptedTestEntity));
        var property = entityType!.FindProperty("SecretField")!;
        var converter = property.GetValueConverter();

        Assert.That(converter, Is.Not.Null, "Value converter should be registered on SecretField");
        Assert.That(converter, Is.InstanceOf<EncryptedValueConverter>());
    }

    [Test]
    public void ConverterExpression_DirectlyInvoked_CallsEncrypt()
    {
        var model = _dbContext.Model;
        var entityType = model.FindEntityType(typeof(EncryptedTestEntity));
        var property = entityType!.FindProperty("SecretField")!;
        var converter = property.GetValueConverter()!;

        // Invoke the compiled ConvertToProvider delegate directly
        var result = converter.ConvertToProvider("test_value");

        Assert.That(result, Is.EqualTo("ENC:test_value"));
        Assert.That(_encryptionService.EncryptCalls, Does.Contain(("test_value", "SecretField")));
    }

    [Test]
    public async Task SaveChangesAsync_EncryptsFieldBeforeStorage()
    {
        // Arrange
        var entity = new EncryptedTestEntity { SecretField = "plaintext" };
        _dbContext.EncryptedEntities.Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — encrypt was called during save
        Assert.That(_encryptionService.EncryptCalls, Does.Contain(("plaintext", "SecretField")));
    }

    [Test]
    public async Task Query_DecryptsFieldAfterLoad()
    {
        // Arrange — save entity
        var entity = new EncryptedTestEntity { SecretField = "plaintext" };
        _dbContext.EncryptedEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        // Act — load from fresh context to force re-read through converter
        await using var readContext = new EncryptionTestDbContext(_dbOptions, _encryptionService);
        var loaded = await readContext.EncryptedEntities.FirstAsync(e => e.Id == entity.Id);

        // Assert — round-trip preserves plaintext
        Assert.That(loaded.SecretField, Is.EqualTo("plaintext"));
    }

    [Test]
    public async Task InMemoryEntity_RemainsCleartext_AfterSave()
    {
        // Arrange
        var entity = new EncryptedTestEntity { SecretField = "plaintext" };
        _dbContext.EncryptedEntities.Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — value converter never mutates the entity object
        Assert.That(entity.SecretField, Is.EqualTo("plaintext"));
    }

    [Test]
    public async Task EncryptInDatabaseFalse_SkipsEncryption()
    {
        // Arrange
        var entity = new EncryptedTestEntity
        {
            SecretField = "plaintext",
            NonDbEncryptedField = "should_not_encrypt"
        };
        _dbContext.EncryptedEntities.Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — only SecretField is encrypted, not NonDbEncryptedField
        Assert.That(_encryptionService.EncryptCalls, Does.Contain(("plaintext", "SecretField")));
        Assert.That(_encryptionService.EncryptCalls,
            Does.Not.Contain(("should_not_encrypt", "NonDbEncryptedField")));
    }

    [Test]
    public async Task SensitiveData_AutoEncryptTrue_Encrypts()
    {
        // Arrange
        var entity = new SensitiveTestEntity { SensitiveField = "sensitive_plaintext" };
        _dbContext.SensitiveEntities.Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        Assert.That(_encryptionService.EncryptCalls,
            Does.Contain(("sensitive_plaintext", "SensitiveField")));
    }

    [Test]
    public async Task SensitiveData_AutoEncryptFalse_SkipsEncryption()
    {
        // Arrange
        var entity = new SensitiveTestEntity
        {
            SensitiveField = "sensitive_plaintext",
            NonAutoEncryptField = "should_not_encrypt"
        };
        _dbContext.SensitiveEntities.Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        Assert.That(_encryptionService.EncryptCalls,
            Does.Contain(("sensitive_plaintext", "SensitiveField")));
        Assert.That(_encryptionService.EncryptCalls,
            Does.Not.Contain(("should_not_encrypt", "NonAutoEncryptField")));
    }

    [Test]
    public async Task CustomKeyName_UsesCustomKey()
    {
        // Arrange
        var entity = new CustomKeyEncryptedTestEntity { ApiKey = "my-api-key" };
        _dbContext.CustomKeyEntities.Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — should use "MyCustomKeyName" not "ApiKey"
        Assert.That(_encryptionService.EncryptCalls,
            Does.Contain(("my-api-key", "MyCustomKeyName")));
    }

    [Test]
    public async Task NullValue_SkipsEncryption()
    {
        // Arrange
        var entity = new NullableEncryptedTestEntity { SecretField = null };
        _dbContext.NullableEncryptedEntities.Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        Assert.That(_encryptionService.EncryptCalls, Is.Empty);
    }

    [Test]
    public async Task MultipleEntities_AllEncrypted()
    {
        // Arrange
        var entity1 = new EncryptedTestEntity { SecretField = "value1" };
        var entity2 = new EncryptedTestEntity { SecretField = "value2" };
        _dbContext.EncryptedEntities.AddRange(entity1, entity2);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        Assert.That(_encryptionService.EncryptCalls, Does.Contain(("value1", "SecretField")));
        Assert.That(_encryptionService.EncryptCalls, Does.Contain(("value2", "SecretField")));
    }

    [Test]
    public void EncryptionFailure_PreventsSave()
    {
        // Arrange
        _encryptionService.ThrowOnEncrypt = true;
        var entity = new EncryptedTestEntity { SecretField = "plaintext" };
        _dbContext.EncryptedEntities.Add(entity);

        // Act & Assert — encryption failure must propagate
        Assert.ThrowsAsync<DbUpdateException>(async () =>
            await _dbContext.SaveChangesAsync());
    }

    [Test]
    public void SyncSave_EncryptsFields()
    {
        // Arrange
        var entity = new EncryptedTestEntity { SecretField = "sync_plaintext" };
        _dbContext.EncryptedEntities.Add(entity);

        // Act
        _dbContext.SaveChanges();

        // Assert
        Assert.That(_encryptionService.EncryptCalls, Does.Contain(("sync_plaintext", "SecretField")));
    }

    [Test]
    public void SyncSave_EntityRemainsCleartext()
    {
        // Arrange
        var entity = new EncryptedTestEntity { SecretField = "sync_plaintext" };
        _dbContext.EncryptedEntities.Add(entity);

        // Act
        _dbContext.SaveChanges();

        // Assert
        Assert.That(entity.SecretField, Is.EqualTo("sync_plaintext"));
    }

    [Test]
    public async Task ModifiedEntity_ReEncryptsNewValue()
    {
        // Arrange
        var entity = new EncryptedTestEntity { SecretField = "original" };
        _dbContext.EncryptedEntities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _encryptionService.EncryptCalls.Clear();

        // Modify
        entity.SecretField = "updated";

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        Assert.That(_encryptionService.EncryptCalls, Does.Contain(("updated", "SecretField")));
    }

    [Test]
    public async Task ChangeTracker_NotDirtied_AfterSave()
    {
        // Arrange
        var entity = new EncryptedTestEntity { SecretField = "plaintext" };
        _dbContext.EncryptedEntities.Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — no entities should be marked Modified after save
        var modifiedEntries = _dbContext.ChangeTracker.Entries()
            .Where(static e => e.State == EntityState.Modified)
            .ToList();

        Assert.That(modifiedEntries, Is.Empty,
            "Value converter approach should not dirty the change tracker after save");
    }

    [Test]
    public async Task SubsequentSave_DoesNotReEncrypt()
    {
        // Arrange
        var entity = new EncryptedTestEntity { SecretField = "plaintext" };
        _dbContext.EncryptedEntities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _encryptionService.EncryptCalls.Clear();

        // Act — save again without modifying anything
        await _dbContext.SaveChangesAsync();

        // Assert — no additional encryptions
        Assert.That(_encryptionService.EncryptCalls, Is.Empty);
    }

    [Test]
    public void NonStringEncryptedField_ThrowsOnModelCreation()
    {
        // Arrange & Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
        {
            var conn = new SqliteConnection("DataSource=:memory:");
            conn.Open();
            var options = new DbContextOptionsBuilder<BadEncryptionTestDbContext>()
                .UseSqlite(conn)
                .ConfigureWarnings(static w =>
                    w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                .Options;

            using var ctx = new BadEncryptionTestDbContext(options, _encryptionService);
            _ = ctx.Model;
            conn.Close();
        });
    }

    #region Test Infrastructure

    /// <summary>
    /// Concrete encryption service for testing — avoids Moq DIM interception issues
    /// </summary>
    private sealed class FakeFieldEncryptionService : IFieldEncryptionService
    {
        public List<(string Value, string FieldName)> EncryptCalls { get; } = [];
        public List<(string Value, string FieldName)> DecryptCalls { get; } = [];
        public bool ThrowOnEncrypt { get; set; }

        public Task<string> EncryptFieldAsync(string plainText, string fieldName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EncryptField(plainText, fieldName));

        public Task<string> DecryptFieldAsync(string encryptedValue, string fieldName,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DecryptField(encryptedValue, fieldName));

        public string EncryptField(string plainText, string fieldName)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            if (ThrowOnEncrypt) throw new InvalidOperationException("Key not found");
            EncryptCalls.Add((plainText, fieldName));
            return $"ENC:{plainText}";
        }

        public string DecryptField(string encryptedValue, string fieldName)
        {
            if (string.IsNullOrEmpty(encryptedValue)) return encryptedValue;
            DecryptCalls.Add((encryptedValue, fieldName));
            return encryptedValue.StartsWith("ENC:") ? encryptedValue["ENC:".Length..] : encryptedValue;
        }

        public Task<string> EncryptFieldWithVersionAsync(string plainText, string fieldName,
            string keyVersion, CancellationToken cancellationToken = default)
            => Task.FromResult(EncryptField(plainText, fieldName));

        public bool IsEncrypted(string? value)
            => value != null && value.StartsWith("ENC:");

        public Task<string> ReEncryptFieldAsync(string encryptedValue, string fieldName,
            string newKeyVersion, CancellationToken cancellationToken = default)
            => Task.FromResult(EncryptField(
                DecryptField(encryptedValue, fieldName), fieldName));
    }

    private class EncryptionTestDbContext : DbContext
    {
        private readonly IFieldEncryptionService? _encryptionService;

        public EncryptionTestDbContext(
            DbContextOptions<EncryptionTestDbContext> options,
            IFieldEncryptionService? encryptionService = null) : base(options)
        {
            _encryptionService = encryptionService;
        }

        public DbSet<EncryptedTestEntity> EncryptedEntities { get; set; }
        public DbSet<SensitiveTestEntity> SensitiveEntities { get; set; }
        public DbSet<NullableEncryptedTestEntity> NullableEncryptedEntities { get; set; }
        public DbSet<CustomKeyEncryptedTestEntity> CustomKeyEntities { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EncryptedTestEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<SensitiveTestEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<NullableEncryptedTestEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<CustomKeyEncryptedTestEntity>().HasKey(static e => e.Id);

            if (_encryptionService is not null)
                modelBuilder.UseFieldEncryption(_encryptionService);

            base.OnModelCreating(modelBuilder);
        }
    }

    private class BadEncryptionTestDbContext : DbContext
    {
        private readonly IFieldEncryptionService? _encryptionService;

        public BadEncryptionTestDbContext(
            DbContextOptions<BadEncryptionTestDbContext> options,
            IFieldEncryptionService? encryptionService = null) : base(options)
        {
            _encryptionService = encryptionService;
        }

        public DbSet<BadEncryptedEntity> BadEntities { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BadEncryptedEntity>().HasKey(static e => e.Id);

            if (_encryptionService is not null)
                modelBuilder.UseFieldEncryption(_encryptionService);

            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class EncryptedTestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [EncryptedField]
        public string SecretField { get; set; } = string.Empty;

        [EncryptedField(EncryptInDatabase = false)]
        public string NonDbEncryptedField { get; set; } = string.Empty;
    }

    private sealed class NullableEncryptedTestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [EncryptedField]
        public string? SecretField { get; set; }
    }

    private sealed class SensitiveTestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [SensitiveData(AutoEncrypt = true)]
        public string SensitiveField { get; set; } = string.Empty;

        [SensitiveData(AutoEncrypt = false)]
        public string NonAutoEncryptField { get; set; } = string.Empty;
    }

    private sealed class CustomKeyEncryptedTestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [EncryptedField(KeyName = "MyCustomKeyName")]
        public string ApiKey { get; set; } = string.Empty;
    }

    private sealed class BadEncryptedEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [EncryptedField]
        public int NotAString { get; set; }
    }

    #endregion
}
