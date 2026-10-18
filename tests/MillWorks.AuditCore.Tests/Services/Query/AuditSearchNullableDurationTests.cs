using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Query;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services.Query;

[TestFixture]
public sealed class AuditSearchNullableDurationTests
{
    [TestCase("Duration", "null", null)]
    [TestCase("duration", "null", null)]
    [TestCase("Duration", "0", 0)]
    [TestCase("Duration", "42", 42)]
    [TestCase("Duration", "\"unknown\"", null)]
    [TestCase("Duration", "2147483648", null)]
    public async Task OptionalDurationPreservesStructuredEvent(string property, string jsonDuration, int? expected)
    {
        await using var context = new AuditDbContext(TestDbContextFactory.CreateInMemoryOptions());
        var eventId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var started = DateTimeOffset.Parse("2026-10-18T17:43:26.191218+00:00");
        var json = $$"""
            {"EventId":"{{eventId}}","EventType":"Compliance.BreachEvidence.Downloaded",
             "StartDate":"2026-10-18T17:43:26.191218+00:00","EndDate":null,
             "{{property}}":{{jsonDuration}},"Environment":{"user_name":null},"Target":null,
             "CustomFields":{"EntityId":"{{entityId}}","Description":"[REDACTED]"},
             "Success":null,"ErrorMessage":null}
            """;
        context.AuditEvents.Add(new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Compliance.BreachEvidence.Downloaded",
            JsonData = json,
            InsertedDate = started
        });
        await context.SaveChangesAsync();
        var service = new AuditSearchService(context, NullLogger<AuditSearchService>.Instance);

        var result = await service.SearchAuditEventsAsync(new AuditSearchRequest { Offset = 0, Limit = 10 });
        var detail = await new AuditService(Mock.Of<IAuditLogRepository>(), new AuditEventRepository(context),
            NullLogger<AuditService>.Instance).GetAuditEventById(eventId);

        Assert.That(detail, Is.Not.Null);
        foreach (var item in new[] { result.Items!.Single(), detail! })
        {
            Assert.That(item.Data, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(item.Data!.EventId, Is.EqualTo(eventId));
                Assert.That(item.Data.EventType, Is.EqualTo("Compliance.BreachEvidence.Downloaded"));
                Assert.That(item.Data.StartDate, Is.EqualTo(started));
                Assert.That(item.Data.Duration, Is.EqualTo(expected));
                Assert.That(item.Data.ErrorMessage, Is.Null);
                Assert.That(item.Data.CustomFields!.ContainsKey("ParseError"), Is.False);
                Assert.That(item.Data.CustomFields["Description"], Is.EqualTo("[REDACTED]"));
                Assert.That(item.User, Is.Null);
                Assert.That(item.Data.Environment!.UserName, Is.Null);
                Assert.That(item.JsonData, Is.EqualTo(json));
            });
        }
    }

    [Test]
    public async Task ExplicitEntityChangesRemainAvailableInSearchAndDetail()
    {
        await using var context = new AuditDbContext(TestDbContextFactory.CreateInMemoryOptions());
        var eventId = Guid.NewGuid();
        var json = """
            {"event_type":"Entity.Updated","duration":null,
             "entity_changes":{"entity_type":"Project","entity_id":"owned-project","action":"Updated",
              "changed_properties":["Name"],"old_values":{"Name":"[REDACTED]"},
              "new_values":{"Name":"[REDACTED]"}}}
            """;
        context.AuditEvents.Add(new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Entity.Updated",
            JsonData = json,
            InsertedDate = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        var result = await new AuditSearchService(context, NullLogger<AuditSearchService>.Instance)
            .SearchAuditEventsAsync(new AuditSearchRequest { Offset = 0, Limit = 10 });
        var detail = await new AuditService(Mock.Of<IAuditLogRepository>(), new AuditEventRepository(context),
            NullLogger<AuditService>.Instance).GetAuditEventById(eventId);

        Assert.That(detail, Is.Not.Null);
        foreach (var item in new[] { result.Items!.Single(), detail! })
        {
            var changes = item.Data?.EntityChanges;
            Assert.That(changes, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(changes!.EntityType, Is.EqualTo("Project"));
                Assert.That(changes.EntityId, Is.EqualTo("owned-project"));
                Assert.That(changes.Action, Is.EqualTo("Updated"));
                Assert.That(changes.ChangedProperties, Is.EqualTo(new[] { "Name" }));
                Assert.That(changes.OldValues!["Name"]!.ToString(), Is.EqualTo("[REDACTED]"));
                Assert.That(changes.NewValues!["Name"]!.ToString(), Is.EqualTo("[REDACTED]"));
                Assert.That(item.JsonData, Is.EqualTo(json));
            });
        }
    }

}
