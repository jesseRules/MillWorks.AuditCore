using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Query;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services.Query;

[TestFixture]
public sealed class AuditWordSearchTests
{
    [TestCase(null, null)]
    [TestCase("  ", null)]
    [TestCase("login", "\"login\"")]
    [TestCase("to", "\"to\"")]
    [TestCase("login alice", "\"login\" AND \"alice\"")]
    [TestCase("\"download completed\" alice", "\"download completed\" AND \"alice\"")]
    [TestCase("OR", "\"OR\"")]
    [TestCase("O'Brien", "\"O'Brien\"")]
    public void WordsAndPhrasesAreLiteralConditions(string? text, string? expected) =>
        Assert.That(AuditWordSearch.BuildCondition(text), Is.EqualTo(expected));

    [TestCase("\"unfinished")]
    [TestCase("\"\"")]
    [TestCase("abc*")]
    [TestCase("*")]
    [TestCase("%")]
    public void InvalidInputDoesNotWidenToAnUnfilteredRead(string text) =>
        Assert.Throws<ArgumentException>(() => AuditWordSearch.BuildCondition(text));

    [Test]
    public void OverlongQueryIsRejected() =>
        Assert.Throws<ArgumentException>(() => AuditWordSearch.BuildCondition(new string('a', 513)));

    [Test]
    public void WordPredicateTranslatesToIndexedFullTextRatherThanPayloadLike()
    {
        using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer("Server=localhost;Database=NotOpened;Integrated Security=true;TrustServerCertificate=true").Options);
        var request = new AuditSearchRequest { SearchTerm = "\"download completed\" alice", EventId = Guid.NewGuid(), EntityId = "subject-7" };
        var sql = context.AuditEvents.Where(AuditEventSearchFilter.Build(request)).ToQueryString();
        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("CONTAINS("));
            Assert.That(sql, Does.Not.Contain(" LIKE "));
            Assert.That(sql, Does.Contain("[EventId] ="));
            Assert.That(sql, Does.Contain("[EntityId] ="));
        });
    }

    [Test]
    public async Task ExactIdentifiersDoNotDependOnJsonAndRespectOtherFilters()
    {
        await using var context = new AuditDbContext(TestDbContextFactory.CreateInMemoryOptions());
        var id = Guid.NewGuid();
        context.AuditEvents.AddRange(new AuditEventEntity { EventId = id, EntityId = "subject-7", EntityType = "Incident", JsonData = null },
            new AuditEventEntity { EventId = Guid.NewGuid(), EntityId = "subject-70", EntityType = "Incident" });
        await context.SaveChangesAsync();
        var service = new AuditSearchService(context, NullLogger<AuditSearchService>.Instance);
        var exact = await service.SearchAuditEventsAsync(new AuditSearchRequest { EventId = id, EntityId = "subject-7" });
        var excluded = await service.SearchAuditEventsAsync(new AuditSearchRequest { EventId = id, EntityType = "Other" });
        Assert.Multiple(() =>
        {
            Assert.That(exact.Items!.Single().EventId, Is.EqualTo(id));
            Assert.That(exact.TotalItems, Is.EqualTo(1));
            Assert.That(excluded.TotalItems, Is.Zero);
        });
    }

    [Test]
    public async Task UnsupportedWordSearchIsUnavailableWhileExactFiltersRemainUsable()
    {
        await using var context = new AuditDbContext(TestDbContextFactory.CreateInMemoryOptions());
        var service = new AuditSearchService(context, NullLogger<AuditSearchService>.Instance);
        await Assert.ThrowsAsync<AuditSearchUnavailableException>(() => service.SearchAuditEventsAsync(new AuditSearchRequest { SearchTerm = "login" }));
        var page = await service.SearchAuditEventsAsync(new AuditSearchRequest { EventId = Guid.NewGuid() });
        Assert.That(page.TotalItems, Is.Zero);
    }
}
