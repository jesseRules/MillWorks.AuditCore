using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Conversion;
using MillWorks.AuditCore.EntityFramework.Extensions;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Phase 4: Edge case tests for EncryptedValueConverter and attribute-based encryption handling.
/// Validates idempotency, backward compatibility, null handling, and multi-field independence.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase4")]
public sealed class EncryptedValueConverterEdgeCaseTests : IDisposable
{
    private SqliteConnection _connection;
    private TrackingEncryptionService _encryptionService;
    private DbContextOptions<EdgeCaseEncryptionDbContext> _dbOptions;
    private EdgeCaseEncryptionDbContext _dbContext;

    [SetUp]
    public void Setup()
    {
        _encryptionService = new TrackingEncryptionService();
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<EdgeCaseEncryptionDbContext>()
            .UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        _dbContext = new EdgeCaseEncryptionDbContext(_dbOptions, _encryptionService);
        _dbContext.Database.EnsureCreated();
    }

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

    // ── Idempotency: already-encrypted values are not double-encrypted ──

    [Test]
    public void Encrypt_AlreadyEncryptedValue_IsNotDoubleEncrypted()
    {
        var model = _dbContext.Model;
        var entityType = model.FindEntityType(typeof(MultiFieldEntity))!;
        var property = entityType.FindProperty("FieldA")!;
        var converter = property.GetValueConverter()!;

        // Simulate an already-encrypted value
        var result = converter.ConvertToProvider("ENC:already_encrypted");

        result.Should().Be("ENC:already_encrypted",
            "already-encrypted values should pass through unchanged");
    }

    // ── Backward compatibility: legacy plaintext passes through on read ──

    [Test]
    public void Decrypt_LegacyPlaintext_PassesThrough()
    {
        var model = _dbContext.Model;
        var entityType = model.FindEntityType(typeof(MultiFieldEntity))!;
        var property = entityType.FindProperty("FieldA")!;
        var converter = property.GetValueConverter()!;

        // Simulate reading legacy plaintext from database
        var result = converter.ConvertFromProvider("legacy_plaintext_value");

        result.Should().Be("legacy_plaintext_value",
            "plaintext legacy data should pass through without error");
    }

    // ── Null handling ──

    [Test]
    public void Encrypt_EmptyString_ReturnsEmpty()
    {
        var model = _dbContext.Model;
        var entityType = model.FindEntityType(typeof(MultiFieldEntity))!;
        var property = entityType.FindProperty("FieldA")!;
        var converter = property.GetValueConverter()!;

        var result = converter.ConvertToProvider("");
        result.Should().Be("");
    }

    [Test]
    public async Task NullableField_NullValue_SkipsEncryption()
    {
        var entity = new NullableFieldEntity { SecretField = null };
        _dbContext.NullableEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        _encryptionService.EncryptCalls.Should().BeEmpty();
    }

    [Test]
    public async Task NullableField_NonNullValue_IsEncrypted()
    {
        var entity = new NullableFieldEntity { SecretField = "sensitive" };
        _dbContext.NullableEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        _encryptionService.EncryptCalls.Should().Contain(("sensitive", "SecretField"));
    }

    // ── Multiple encrypted fields use independent derived keys ──

    [Test]
    public async Task MultipleEncryptedFields_UseIndependentKeys()
    {
        var entity = new MultiFieldEntity
        {
            FieldA = "value_a",
            FieldB = "value_b"
        };
        _dbContext.MultiFieldEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        _encryptionService.EncryptCalls.Should().Contain(("value_a", "FieldA"));
        _encryptionService.EncryptCalls.Should().Contain(("value_b", "FieldB"));
    }

    // ── Round-trip through save and reload ──

    [Test]
    public async Task SaveAndReload_DecryptsCorrectly()
    {
        var entity = new MultiFieldEntity
        {
            FieldA = "secret_a",
            FieldB = "secret_b"
        };
        _dbContext.MultiFieldEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        await using var readContext = new EdgeCaseEncryptionDbContext(_dbOptions, _encryptionService);
        var loaded = await readContext.MultiFieldEntities.FirstAsync(e => e.Id == entity.Id);

        loaded.FieldA.Should().Be("secret_a");
        loaded.FieldB.Should().Be("secret_b");
    }

    // ── Update preserves encryption ──

    [Test]
    public async Task UpdateField_ReEncryptsNewValue()
    {
        var entity = new MultiFieldEntity { FieldA = "original", FieldB = "unchanged" };
        _dbContext.MultiFieldEntities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _encryptionService.EncryptCalls.Clear();

        entity.FieldA = "updated";
        await _dbContext.SaveChangesAsync();

        _encryptionService.EncryptCalls.Should().Contain(("updated", "FieldA"));
    }

    // ── Change tracker not dirtied after save ──

    [Test]
    public async Task SaveChanges_DoesNotDirtyChangeTracker()
    {
        var entity = new MultiFieldEntity { FieldA = "test", FieldB = "test2" };
        _dbContext.MultiFieldEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Modified)
            .Should().BeEmpty("converter should not dirty the change tracker");
    }

    // ── Encryption failure prevents save ──

    [Test]
    public void EncryptionFailure_PreventsSave()
    {
        _encryptionService.ThrowOnEncrypt = true;
        var entity = new MultiFieldEntity { FieldA = "data", FieldB = "data2" };
        _dbContext.MultiFieldEntities.Add(entity);

        var act = () => _dbContext.SaveChangesAsync();
        act.Should().ThrowAsync<DbUpdateException>();
    }

    // ── SensitiveData attribute with AutoEncrypt=true ──

    [Test]
    public async Task SensitiveDataAttribute_AutoEncryptTrue_Encrypts()
    {
        var entity = new SensitiveAnnotatedEntity { SsnField = "123-45-6789" };
        _dbContext.SensitiveAnnotatedEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        _encryptionService.EncryptCalls.Should().Contain(("123-45-6789", "SsnField"));
    }

    [Test]
    public async Task SensitiveDataAttribute_AutoEncryptFalse_SkipsEncryption()
    {
        var entity = new SensitiveAnnotatedEntity
        {
            SsnField = "123-45-6789",
            NonAutoField = "should_not_encrypt"
        };
        _dbContext.SensitiveAnnotatedEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        _encryptionService.EncryptCalls.Should().Contain(("123-45-6789", "SsnField"));
        _encryptionService.EncryptCalls.Should().NotContain(c => c.FieldName == "NonAutoField");
    }

    #region Test Infrastructure

    private sealed class TrackingEncryptionService : IFieldEncryptionService
    {
        public List<(string Value, string FieldName)> EncryptCalls { get; } = [];
        public List<(string Value, string FieldName)> DecryptCalls { get; } = [];
        public bool ThrowOnEncrypt { get; set; }

        public Task<string> EncryptFieldAsync(string plainText, string fieldName,
            CancellationToken ct = default) => Task.FromResult(EncryptField(plainText, fieldName));

        public Task<string> DecryptFieldAsync(string encryptedValue, string fieldName,
            CancellationToken ct = default) => Task.FromResult(DecryptField(encryptedValue, fieldName));

        public string EncryptField(string plainText, string fieldName)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;
            if (ThrowOnEncrypt) throw new InvalidOperationException("Encryption failed");
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
            string keyVersion, CancellationToken ct = default) =>
            Task.FromResult(EncryptField(plainText, fieldName));

        public bool IsEncrypted(string? value) => value != null && value.StartsWith("ENC:");

        public Task<string> ReEncryptFieldAsync(string encryptedValue, string fieldName,
            string newKeyVersion, CancellationToken ct = default) =>
            Task.FromResult(EncryptField(DecryptField(encryptedValue, fieldName), fieldName));
    }

    private sealed class EdgeCaseEncryptionDbContext : DbContext
    {
        private readonly IFieldEncryptionService? _encryptionService;

        public EdgeCaseEncryptionDbContext(
            DbContextOptions<EdgeCaseEncryptionDbContext> options,
            IFieldEncryptionService? encryptionService = null) : base(options)
        {
            _encryptionService = encryptionService;
        }

        public DbSet<MultiFieldEntity> MultiFieldEntities { get; set; }
        public DbSet<NullableFieldEntity> NullableEntities { get; set; }
        public DbSet<SensitiveAnnotatedEntity> SensitiveAnnotatedEntities { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MultiFieldEntity>().HasKey(e => e.Id);
            modelBuilder.Entity<NullableFieldEntity>().HasKey(e => e.Id);
            modelBuilder.Entity<SensitiveAnnotatedEntity>().HasKey(e => e.Id);

            if (_encryptionService is not null)
                modelBuilder.UseFieldEncryption(_encryptionService);

            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class MultiFieldEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [EncryptedField]
        public string FieldA { get; set; } = string.Empty;

        [EncryptedField]
        public string FieldB { get; set; } = string.Empty;
    }

    private sealed class NullableFieldEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [EncryptedField]
        public string? SecretField { get; set; }
    }

    private sealed class SensitiveAnnotatedEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [SensitiveData(AutoEncrypt = true)]
        public string SsnField { get; set; } = string.Empty;

        [SensitiveData(AutoEncrypt = false)]
        public string NonAutoField { get; set; } = string.Empty;
    }

    #endregion
}
