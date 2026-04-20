using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Options;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Verifies that the compiled-model cache is keyed on schema so two
/// <see cref="AuditApplicationDbContext"/> instances with different schemas
/// do not share a cached model.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AuditModelCacheKeyFactoryTests
{
    [Test]
    public void Create_SameTypeSameSchemaSameDesignTime_ReturnsEqualKeys()
    {
        var factory = new AuditModelCacheKeyFactory();

        using var contextA = CreateContext("audit");
        using var contextB = CreateContext("audit");

        var keyA = factory.Create(contextA, designTime: false);
        var keyB = factory.Create(contextB, designTime: false);

        Assert.That(keyA, Is.EqualTo(keyB));
    }

    [Test]
    public void Create_DifferentSchemas_ReturnsDifferentKeys()
    {
        var factory = new AuditModelCacheKeyFactory();

        using var defaultContext = CreateContext("audit");
        using var customContext = CreateContext("audit_custom");

        var defaultKey = factory.Create(defaultContext, designTime: false);
        var customKey = factory.Create(customContext, designTime: false);

        Assert.That(defaultKey, Is.Not.EqualTo(customKey));
    }

    [Test]
    public void Create_DifferentDesignTimeFlag_ReturnsDifferentKeys()
    {
        var factory = new AuditModelCacheKeyFactory();

        using var context = CreateContext("audit");

        var runtimeKey = factory.Create(context, designTime: false);
        var designTimeKey = factory.Create(context, designTime: true);

        Assert.That(runtimeKey, Is.Not.EqualTo(designTimeKey));
    }

    private static AuditApplicationDbContext CreateContext(string schema)
    {
        var dbOptions = new DbContextOptionsBuilder<AuditApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var efOptions = Options.Create(new EntityFrameworkOptions { Schema = schema });

        return new AuditApplicationDbContext(dbOptions, encryptionService: null, efOptions: efOptions);
    }
}
