using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Integration;

[TestFixture]
public sealed class AuditEventPagingTests : SqliteIntegrationFixture
{
    [Test]
    public async Task FilteredPagesSortKeysBeforePayloadHydrationAndPreserveTieOrder()
    {
        var commands = new Commands();
        await using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>(Options)
            .AddInterceptors(commands).Options);
        var date = DateTimeOffset.Parse("2026-10-18T10:00:00Z");
        context.AuditEvents.AddRange(Enumerable.Range(0, 7).Select(i => new AuditEventEntity
        {
            EventId = Guid.NewGuid(), InsertedDate = date,
            EntityId = i < 5 ? "match" : "other", JsonData = "{\"value\":\"" + new string('a', 4096) + "\"}"
        }));
        await context.SaveChangesAsync();
        var expected = await context.AuditEvents.Where(e => e.EntityId == "match")
            .OrderByDescending(e => e.InsertedDate).ThenByDescending(e => e.EventId)
            .Select(e => e.EventId).ToListAsync();
        commands.Text.Clear();
        var repository = new AuditEventRepository(context);
        var page = await repository.GetByOffsetAsync(1, 2, e => e.EntityId == "match",
            q => q.OrderByDescending(e => e.InsertedDate).ThenByDescending(e => e.EventId));
        Assert.Multiple(() =>
        {
            Assert.That(page.TotalCount, Is.EqualTo(5));
            Assert.That(page.Items.Select(e => e.EventId), Is.EqualTo(expected.Skip(1).Take(2)));
            Assert.That(page.Items.All(e => e.JsonData!.Length > 4096), Is.True);
            Assert.That(commands.Text, Has.Count.EqualTo(3));
            Assert.That(commands.Text[1], Does.Not.Contain("JsonData"));
            Assert.That(commands.Text[1], Does.Contain("ORDER BY"));
            Assert.That(commands.Text[2], Does.Contain("JsonData"));
        });
    }

    [Test]
    public async Task ExhaustedPageDoesNotFetchAnyPayload()
    {
        var commands = new Commands();
        await using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>(Options)
            .AddInterceptors(commands).Options);
        var page = await new AuditEventRepository(context).GetByOffsetAsync(25, 25);
        Assert.Multiple(() =>
        {
            Assert.That(page.TotalCount, Is.Zero);
            Assert.That(page.Items, Is.Empty);
            Assert.That(commands.Text, Has.Count.EqualTo(2));
            Assert.That(commands.Text.Any(sql => sql.Contains("JsonData")), Is.False);
        });
    }

    [Test]
    public void SqlServerPageSelectionContainsOnlyKeys()
    {
        using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer("Server=localhost;Database=NotOpened;Integrated Security=true;TrustServerCertificate=true").Options);
        var sql = AuditEventPaging.SelectKeys(context.AuditEvents.OrderByDescending(e => e.InsertedDate)
            .ThenByDescending(e => e.EventId), 25, 25).ToQueryString();
        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("[EventId]"));
            Assert.That(sql, Does.Contain("OFFSET"));
            Assert.That(sql, Does.Not.Contain("JsonData"));
            Assert.That(sql, Does.Not.Contain("AdditionalData"));
        });
    }

    private sealed class Commands : DbCommandInterceptor
    {
        internal List<string> Text { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Text.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
