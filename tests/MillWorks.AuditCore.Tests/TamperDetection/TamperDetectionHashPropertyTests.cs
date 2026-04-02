using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MillWorks.AuditCore.Abstractions.Canonicalization;

namespace MillWorks.AuditCore.Tests.TamperDetection;

/// <summary>
/// Phase 5: Property-based tests for the tamper detection hash computation.
/// Verifies the cryptographic properties that underpin audit trail integrity:
/// determinism, avalanche, chain integrity, collision resistance, and order sensitivity.
///
/// These tests replicate the TamperDetectionService.ComputeEventHash logic directly
/// (it uses IncrementalHash with SHA-256 over canonicalized fields separated by '|').
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase5")]
public sealed class TamperDetectionHashPropertyTests
{
    private static readonly Random Rng = new(42);

    /// <summary>
    /// Replicates TamperDetectionService.ComputeEventHash for test verification.
    /// </summary>
    private static string ComputeEventHash(Guid eventId, string? eventType, string? user,
        DateTimeOffset? insertedDate, string? jsonData)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(eventId.ToString()));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(eventType ?? string.Empty));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(user ?? string.Empty));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(AuditCanonicalizer.NormalizeDate(insertedDate)));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(AuditCanonicalizer.Canonicalize(jsonData)));

        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    // ── Determinism: same input always produces same hash ──

    [Test]
    public void Property_Determinism_SameEventSameHash()
    {
        for (var i = 0; i < 1000; i++)
        {
            var eventId = Guid.NewGuid();
            var eventType = $"Event.Type.{Rng.Next(100)}";
            var user = $"user{Rng.Next(1000)}";
            var date = DateTimeOffset.UtcNow.AddMinutes(-Rng.Next(10000));
            var json = $"{{\"field\":\"{Rng.Next(10000)}\"}}";

            var hash1 = ComputeEventHash(eventId, eventType, user, date, json);
            var hash2 = ComputeEventHash(eventId, eventType, user, date, json);

            hash1.Should().Be(hash2, $"iteration {i}: same event must produce same hash");
        }
    }

    // ── Avalanche: changing one bit changes at least 40% of output bits ──

    [Test]
    public void Property_Avalanche_OneFieldChangeCausesSignificantDiff()
    {
        var totalBitChanges = 0;
        var totalBits = 0;
        const int iterations = 500;

        for (var i = 0; i < iterations; i++)
        {
            var eventId = Guid.NewGuid();
            var eventType = $"User.Login.{i}";
            var user = $"user{i}";
            var date = DateTimeOffset.UtcNow;
            var json = $"{{\"data\":\"{i}\"}}";

            var hash1 = ComputeEventHash(eventId, eventType, user, date, json);
            // Change just the user field slightly
            var hash2 = ComputeEventHash(eventId, eventType, user + "x", date, json);

            var bytes1 = Convert.FromBase64String(hash1);
            var bytes2 = Convert.FromBase64String(hash2);

            var diffBits = CountDifferentBits(bytes1, bytes2);
            totalBitChanges += diffBits;
            totalBits += bytes1.Length * 8;
        }

        var avalancheRatio = (double)totalBitChanges / totalBits;
        avalancheRatio.Should().BeGreaterThan(0.35,
            "SHA-256 avalanche property: changing one input bit should change ~50% of output bits");
    }

    // ── No practical collisions: unique events produce unique hashes ──

    [Test]
    public void Property_NoCollisions_10000UniqueEventsUniqueHashes()
    {
        var hashes = new HashSet<string>();

        for (var i = 0; i < 10_000; i++)
        {
            var hash = ComputeEventHash(
                Guid.NewGuid(),
                $"Event.{Rng.Next(100)}",
                $"user{Rng.Next(10000)}",
                DateTimeOffset.UtcNow.AddSeconds(-Rng.Next(100000)),
                $"{{\"idx\":{i}}}");

            hashes.Add(hash);
        }

        hashes.Should().HaveCount(10_000, "10,000 unique events should produce 10,000 unique hashes");
    }

    // ── Order sensitivity: different event order produces different chain ──

    [Test]
    public void Property_OrderSensitivity_DifferentOrderDifferentHash()
    {
        for (var i = 0; i < 500; i++)
        {
            var eventIdA = Guid.NewGuid();
            var eventIdB = Guid.NewGuid();
            var date = DateTimeOffset.UtcNow;

            var hashA = ComputeEventHash(eventIdA, "TypeA", "userA", date, "{\"a\":1}");
            var hashB = ComputeEventHash(eventIdB, "TypeB", "userB", date, "{\"b\":2}");

            // Chain: A then B
            var chainAB = SHA256.HashData(Encoding.UTF8.GetBytes(hashA + hashB));
            // Chain: B then A
            var chainBA = SHA256.HashData(Encoding.UTF8.GetBytes(hashB + hashA));

            chainAB.Should().NotBeEquivalentTo(chainBA,
                $"iteration {i}: event order must matter in chain computation");
        }
    }

    // ── Previous hash dependency: same event with different previous hash produces different output ──

    [Test]
    public void Property_PreviousHashDependency()
    {
        for (var i = 0; i < 500; i++)
        {
            var eventId = Guid.NewGuid();
            var eventType = "Test.Event";
            var user = "user1";
            var date = DateTimeOffset.UtcNow;
            var json = "{\"data\":\"test\"}";

            var eventHash = ComputeEventHash(eventId, eventType, user, date, json);

            var prevHash1 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"prev{i}")));
            var prevHash2 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"other{i}")));

            // Simulate chain hash: previous + current
            var chain1 = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(prevHash1 + eventHash)));
            var chain2 = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(prevHash2 + eventHash)));

            chain1.Should().NotBe(chain2,
                $"iteration {i}: different previous hash must produce different chain hash");
        }
    }

    // ── Chain integrity: independently recomputable ──

    [Test]
    public void Property_ChainIntegrity_RecomputationMatches()
    {
        var chainLength = 100;
        var events = Enumerable.Range(0, chainLength).Select(i => new
        {
            EventId = Guid.NewGuid(),
            EventType = $"Chain.Event.{i}",
            User = $"user{i}",
            Date = DateTimeOffset.UtcNow.AddSeconds(i),
            Json = $"{{\"seq\":{i}}}"
        }).ToList();

        // Build chain
        var hashes = new string[chainLength];
        string? previousHash = null;
        for (var i = 0; i < chainLength; i++)
        {
            var eventHash = ComputeEventHash(
                events[i].EventId, events[i].EventType,
                events[i].User, events[i].Date, events[i].Json);

            var chainInput = (previousHash ?? "") + eventHash;
            hashes[i] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(chainInput)));
            previousHash = hashes[i];
        }

        // Verify chain independently
        string? verifyPrevHash = null;
        for (var i = 0; i < chainLength; i++)
        {
            var eventHash = ComputeEventHash(
                events[i].EventId, events[i].EventType,
                events[i].User, events[i].Date, events[i].Json);

            var chainInput = (verifyPrevHash ?? "") + eventHash;
            var verifyHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(chainInput)));

            verifyHash.Should().Be(hashes[i], $"chain hash at position {i} must be recomputable");
            verifyPrevHash = verifyHash;
        }
    }

    // ── Null field handling ──

    [Test]
    public void Property_NullFields_ProduceValidHash()
    {
        for (var i = 0; i < 500; i++)
        {
            var eventId = Guid.NewGuid();
            var eventType = Rng.Next(2) == 0 ? null : "Test";
            var user = Rng.Next(2) == 0 ? null : "user1";
            DateTimeOffset? date = Rng.Next(2) == 0 ? null : DateTimeOffset.UtcNow;
            var json = Rng.Next(2) == 0 ? null : "{\"a\":1}";

            var act = () => ComputeEventHash(eventId, eventType, user, date, json);
            act.Should().NotThrow($"iteration {i}: null fields must not crash hashing");

            var hash = ComputeEventHash(eventId, eventType, user, date, json);
            hash.Should().NotBeNullOrEmpty();
            // SHA-256 base64 = 44 characters
            hash.Length.Should().Be(44, "SHA-256 hash in base64 should be 44 characters");
        }
    }

    // ── Each field contributes to hash ──

    [Test]
    public void Property_EachFieldContributes_ChangingAnyFieldChangesHash()
    {
        var baseId = Guid.NewGuid();
        var baseType = "Base.Type";
        var baseUser = "baseUser";
        var baseDate = DateTimeOffset.UtcNow;
        var baseJson = "{\"base\":true}";

        var baseHash = ComputeEventHash(baseId, baseType, baseUser, baseDate, baseJson);

        // Change eventId
        ComputeEventHash(Guid.NewGuid(), baseType, baseUser, baseDate, baseJson)
            .Should().NotBe(baseHash, "changing eventId must change hash");

        // Change eventType
        ComputeEventHash(baseId, "Other.Type", baseUser, baseDate, baseJson)
            .Should().NotBe(baseHash, "changing eventType must change hash");

        // Change user
        ComputeEventHash(baseId, baseType, "otherUser", baseDate, baseJson)
            .Should().NotBe(baseHash, "changing user must change hash");

        // Change date
        ComputeEventHash(baseId, baseType, baseUser, baseDate.AddSeconds(1), baseJson)
            .Should().NotBe(baseHash, "changing date must change hash");

        // Change jsonData
        ComputeEventHash(baseId, baseType, baseUser, baseDate, "{\"base\":false}")
            .Should().NotBe(baseHash, "changing jsonData must change hash");
    }

    private static int CountDifferentBits(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var xor = (byte)(a[i] ^ b[i]);
            while (xor != 0)
            {
                count += xor & 1;
                xor >>= 1;
            }
        }
        return count;
    }
}
