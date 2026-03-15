namespace MillWorks.AuditCore.Tests.Integration;

[TestFixture]
[Category("Integration")]
public class SqliteFixtureSmokeTests : SqliteIntegrationFixture
{
    [Test]
    public void CanConnect_ReturnsTrue()
    {
        using var context = CreateContext();
        Assert.That(context.Database.CanConnect(), Is.True);
    }
}
