using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Compliance;

namespace MillWorks.AuditCore.Tests.Services.Compliance;

/// <summary>
/// Phase 4: Edge case tests for ConsentVerificationService.
/// Validates constructor validation, scope isolation, consent overwrite, and concurrent access.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase4")]
public sealed class ConsentVerificationServiceEdgeCaseTests
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

    // ── Constructor validation ──

    [Test]
    public void Constructor_NullCache_ThrowsArgumentNullException()
    {
        var act = () => new ConsentVerificationService(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("cache");
    }

    // ── Consent overwrite ──

    [Test]
    public async Task RecordConsentAsync_Overwrite_UpdatesExpiry()
    {
        // Record with short TTL
        await _service.RecordConsentAsync("user1", "Student", null, DateTimeOffset.UtcNow.AddHours(1));
        _service.HasActiveConsent("user1", "Student").Should().Be(ConsentStatus.Granted);

        // Overwrite with never-expire
        await _service.RecordConsentAsync("user1", "Student", null, DateTimeOffset.MaxValue);
        _service.HasActiveConsent("user1", "Student").Should().Be(ConsentStatus.Granted);
    }

    // ── Scope isolation ──

    [Test]
    public async Task HasActiveConsent_NullScope_IsolatedFromNamedScope()
    {
        await _service.RecordConsentAsync("user1", "Student", null, DateTimeOffset.MaxValue);

        // Null scope and named scope should be independent
        _service.HasActiveConsent("user1", "Student").Should().Be(ConsentStatus.Granted);
        _service.HasActiveConsent("user1", "Student", "grades").Should().Be(ConsentStatus.NotFound);
    }

    [Test]
    public async Task HasActiveConsent_DifferentScopes_FullyIsolated()
    {
        await _service.RecordConsentAsync("user1", "Student", "grades", DateTimeOffset.MaxValue);
        await _service.RecordConsentAsync("user1", "Student", "transcripts", DateTimeOffset.MaxValue);

        _service.HasActiveConsent("user1", "Student", "grades").Should().Be(ConsentStatus.Granted);
        _service.HasActiveConsent("user1", "Student", "transcripts").Should().Be(ConsentStatus.Granted);
        _service.HasActiveConsent("user1", "Student", "attendance").Should().Be(ConsentStatus.NotFound);
    }

    // ── Revoke only affects exact match ──

    [Test]
    public async Task RevokeConsentAsync_OnlyRevokesScopedEntry()
    {
        await _service.RecordConsentAsync("user1", "Student", "grades", DateTimeOffset.MaxValue);
        await _service.RecordConsentAsync("user1", "Student", "transcripts", DateTimeOffset.MaxValue);

        await _service.RevokeConsentAsync("user1", "Student", "grades");

        _service.HasActiveConsent("user1", "Student", "grades").Should().Be(ConsentStatus.NotFound);
        _service.HasActiveConsent("user1", "Student", "transcripts").Should().Be(ConsentStatus.Granted);
    }

    [Test]
    public async Task RevokeConsentAsync_NonExistent_DoesNotThrow()
    {
        // Revoking consent that was never granted should be a no-op
        var act = () => _service.RevokeConsentAsync("nobody", "Nothing");
        await act.Should().NotThrowAsync();
    }

    // ── Concurrent access ──

    [Test]
    public async Task ConcurrentRecordAndCheck_NoRace()
    {
        var tasks = Enumerable.Range(0, 50).Select(async i =>
        {
            var userId = $"user{i}";
            await _service.RecordConsentAsync(userId, "Student", null, DateTimeOffset.MaxValue);
            var status = _service.HasActiveConsent(userId, "Student");
            return (userId, status);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r =>
            r.status.Should().Be(ConsentStatus.Granted));
    }

    [Test]
    public async Task ConcurrentRevokeAndCheck_NoRace()
    {
        // Pre-populate
        for (var i = 0; i < 20; i++)
            await _service.RecordConsentAsync($"user{i}", "Student", null, DateTimeOffset.MaxValue);

        // Revoke all concurrently
        var tasks = Enumerable.Range(0, 20)
            .Select(i => _service.RevokeConsentAsync($"user{i}", "Student"))
            .ToList();
        await Task.WhenAll(tasks);

        // All should be revoked
        for (var i = 0; i < 20; i++)
            _service.HasActiveConsent($"user{i}", "Student").Should().Be(ConsentStatus.NotFound);
    }

    // ── Async wrapper delegates correctly ──

    [Test]
    public async Task HasActiveConsentAsync_MatchesSyncResult()
    {
        await _service.RecordConsentAsync("user1", "Student", "grades", DateTimeOffset.MaxValue);

        var syncResult = _service.HasActiveConsent("user1", "Student", "grades");
        var asyncResult = await _service.HasActiveConsentAsync("user1", "Student", "grades");

        asyncResult.Should().Be(syncResult);
    }

    // ── Multiple entity types for same user ──

    [Test]
    public async Task RecordConsent_MultipleEntityTypes_Independent()
    {
        await _service.RecordConsentAsync("user1", "Student", null, DateTimeOffset.MaxValue);
        await _service.RecordConsentAsync("user1", "Teacher", null, DateTimeOffset.MaxValue);

        _service.HasActiveConsent("user1", "Student").Should().Be(ConsentStatus.Granted);
        _service.HasActiveConsent("user1", "Teacher").Should().Be(ConsentStatus.Granted);

        await _service.RevokeConsentAsync("user1", "Student");

        _service.HasActiveConsent("user1", "Student").Should().Be(ConsentStatus.NotFound);
        _service.HasActiveConsent("user1", "Teacher").Should().Be(ConsentStatus.Granted);
    }

    // ── Edge case: empty strings ──

    [Test]
    public async Task RecordConsent_EmptyUserId_StillWorks()
    {
        // The service doesn't validate input — verify it doesn't crash
        await _service.RecordConsentAsync("", "Student", null, DateTimeOffset.MaxValue);
        _service.HasActiveConsent("", "Student").Should().Be(ConsentStatus.Granted);
    }

    [Test]
    public async Task RecordConsent_EmptyEntityType_StillWorks()
    {
        await _service.RecordConsentAsync("user1", "", null, DateTimeOffset.MaxValue);
        _service.HasActiveConsent("user1", "").Should().Be(ConsentStatus.Granted);
    }
}
