using Microsoft.Extensions.Caching.Memory;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Compliance;

namespace MillWorks.AuditCore.Tests.Services.Compliance;

[TestFixture]
public class ConsentVerificationServiceTests
{
    private IMemoryCache _cache = null!;
    private ConsentVerificationService _service = null!;

    [SetUp]
    public void Setup()
    {
        _cache = new MemoryCache(new MemoryCacheOptions());
        _service = new ConsentVerificationService(_cache);
    }

    [TearDown]
    public void TearDown()
    {
        _cache.Dispose();
    }

    // ── HasActiveConsent (sync) ──

    [Test]
    public void HasActiveConsent_ReturnsGranted_WhenConsentIsCached()
    {
        // Arrange — seed consent into the cache
        _service.RecordConsentAsync("user1", "StudentEntity", null, DateTimeOffset.MaxValue).GetAwaiter().GetResult();

        // Act
        var result = _service.HasActiveConsent("user1", "StudentEntity");

        // Assert
        Assert.That(result, Is.EqualTo(ConsentStatus.Granted));
    }

    [Test]
    public void HasActiveConsent_ReturnsNotFound_WhenNoConsentExists()
    {
        // Act — no consent seeded
        var result = _service.HasActiveConsent("user1", "StudentEntity");

        // Assert
        Assert.That(result, Is.EqualTo(ConsentStatus.NotFound));
    }

    [Test]
    public void HasActiveConsent_ReturnsNotFound_AfterCacheTtlExpires()
    {
        // Arrange — consent with a TTL that's already expired
        var expiredTime = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        _service.RecordConsentAsync("user1", "StudentEntity", null, expiredTime).GetAwaiter().GetResult();

        // Act
        var result = _service.HasActiveConsent("user1", "StudentEntity");

        // Assert — cache entry should have been rejected/evicted
        Assert.That(result, Is.EqualTo(ConsentStatus.NotFound));
    }

    [Test]
    public void HasActiveConsent_ReturnsGranted_WithNonExpiredTtl()
    {
        // Arrange — consent that expires in the future
        var futureTime = DateTimeOffset.UtcNow.AddHours(1);
        _service.RecordConsentAsync("user1", "StudentEntity", null, futureTime).GetAwaiter().GetResult();

        // Act
        var result = _service.HasActiveConsent("user1", "StudentEntity");

        // Assert
        Assert.That(result, Is.EqualTo(ConsentStatus.Granted));
    }

    [Test]
    public void HasActiveConsent_ReturnsGranted_WithMaxValueExpiry_NeverExpires()
    {
        // Arrange — FERPA consent with no natural expiry
        _service.RecordConsentAsync("user1", "StudentEntity", null, DateTimeOffset.MaxValue).GetAwaiter().GetResult();

        // Act
        var result = _service.HasActiveConsent("user1", "StudentEntity");

        // Assert — MaxValue means the entry never expires
        Assert.That(result, Is.EqualTo(ConsentStatus.Granted));
    }

    [Test]
    public void HasActiveConsent_IsolatesByUserId()
    {
        // Arrange
        _service.RecordConsentAsync("user1", "StudentEntity", null, DateTimeOffset.MaxValue).GetAwaiter().GetResult();

        // Act — different user
        var result = _service.HasActiveConsent("user2", "StudentEntity");

        // Assert
        Assert.That(result, Is.EqualTo(ConsentStatus.NotFound));
    }

    [Test]
    public void HasActiveConsent_IsolatesByEntityType()
    {
        // Arrange
        _service.RecordConsentAsync("user1", "StudentEntity", null, DateTimeOffset.MaxValue).GetAwaiter().GetResult();

        // Act — different entity type
        var result = _service.HasActiveConsent("user1", "EnrollmentEntity");

        // Assert
        Assert.That(result, Is.EqualTo(ConsentStatus.NotFound));
    }

    [Test]
    public void HasActiveConsent_IsolatesByScope()
    {
        // Arrange
        _service.RecordConsentAsync("user1", "StudentEntity", "grades", DateTimeOffset.MaxValue).GetAwaiter().GetResult();

        // Act — different scope
        var result = _service.HasActiveConsent("user1", "StudentEntity", "transcripts");

        // Assert
        Assert.That(result, Is.EqualTo(ConsentStatus.NotFound));
    }

    // ── RecordConsentAsync + subsequent HasActiveConsent ──

    [Test]
    public async Task RecordConsentAsync_PopulatesCache_SubsequentCheckReturnsGranted()
    {
        // Act
        await _service.RecordConsentAsync("user1", "StudentEntity", "grades", DateTimeOffset.MaxValue);

        // Assert
        var result = _service.HasActiveConsent("user1", "StudentEntity", "grades");
        Assert.That(result, Is.EqualTo(ConsentStatus.Granted));
    }

    // ── RevokeConsentAsync ──

    [Test]
    public async Task RevokeConsentAsync_RemovesFromCache_SubsequentCheckReturnsNotFound()
    {
        // Arrange
        await _service.RecordConsentAsync("user1", "StudentEntity", null, DateTimeOffset.MaxValue);
        Assert.That(_service.HasActiveConsent("user1", "StudentEntity"), Is.EqualTo(ConsentStatus.Granted));

        // Act
        await _service.RevokeConsentAsync("user1", "StudentEntity");

        // Assert
        Assert.That(_service.HasActiveConsent("user1", "StudentEntity"), Is.EqualTo(ConsentStatus.NotFound));
    }

    [Test]
    public async Task RevokeConsentAsync_RemovesRegardlessOfTtl()
    {
        // Arrange — consent that wouldn't naturally expire
        await _service.RecordConsentAsync("user1", "StudentEntity", null, DateTimeOffset.MaxValue);

        // Act
        await _service.RevokeConsentAsync("user1", "StudentEntity");

        // Assert
        Assert.That(_service.HasActiveConsent("user1", "StudentEntity"), Is.EqualTo(ConsentStatus.NotFound));
    }

    [Test]
    public async Task RevokeConsentAsync_DoesNotAffectOtherUsers()
    {
        // Arrange
        await _service.RecordConsentAsync("user1", "StudentEntity", null, DateTimeOffset.MaxValue);
        await _service.RecordConsentAsync("user2", "StudentEntity", null, DateTimeOffset.MaxValue);

        // Act
        await _service.RevokeConsentAsync("user1", "StudentEntity");

        // Assert
        Assert.That(_service.HasActiveConsent("user1", "StudentEntity"), Is.EqualTo(ConsentStatus.NotFound));
        Assert.That(_service.HasActiveConsent("user2", "StudentEntity"), Is.EqualTo(ConsentStatus.Granted));
    }

    // ── HasActiveConsentAsync (async wrapper) ──

    [Test]
    public async Task HasActiveConsentAsync_DelegatesToSyncMethod()
    {
        // Arrange
        await _service.RecordConsentAsync("user1", "StudentEntity", null, DateTimeOffset.MaxValue);

        // Act
        var result = await _service.HasActiveConsentAsync("user1", "StudentEntity");

        // Assert — same result as sync
        Assert.That(result, Is.EqualTo(ConsentStatus.Granted));
    }

    [Test]
    public async Task HasActiveConsentAsync_ReturnsNotFound_WhenNoCacheEntry()
    {
        // Act
        var result = await _service.HasActiveConsentAsync("unknown", "UnknownEntity");

        // Assert
        Assert.That(result, Is.EqualTo(ConsentStatus.NotFound));
    }
}
