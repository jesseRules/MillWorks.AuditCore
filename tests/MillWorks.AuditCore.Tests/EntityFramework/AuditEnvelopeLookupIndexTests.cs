using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Tests.EntityFramework;

[TestFixture]
[Category("Unit")]
public sealed class AuditEnvelopeLookupIndexTests
{
    [Test]
    public void SqlServerReplayLookup_CoversRowsWithAndWithoutPropertyChanges()
    {
        using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer("Server=localhost;Database=ModelOnly;Integrated Security=true;TrustServerCertificate=true")
            .Options);
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AuditLogEntity))!;
        var lookup = entity.GetIndexes().Single(index => index.GetDatabaseName() == "IX_AuditLogs_Envelope_Lookup");
        Assert.Multiple(() =>
        {
            Assert.That(lookup.Properties.Select(property => property.Name), Is.EqualTo(new[] { "EnvelopeId" }));
            Assert.That(lookup.GetFilter(), Is.EqualTo("[EnvelopeId] IS NOT NULL"));
            Assert.That(lookup.GetIncludeProperties(), Is.EqualTo(new[] { "PropertyName" }));
            Assert.That(lookup.IsUnique, Is.False);
            Assert.That(entity.GetIndexes().Single(index => index.GetDatabaseName() == "IX_AuditLogs_Envelope_Property").IsUnique, Is.True);
        });
    }
}
