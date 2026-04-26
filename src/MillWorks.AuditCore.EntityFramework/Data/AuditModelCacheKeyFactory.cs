using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MillWorks.AuditCore.EntityFramework.Data;

/// <summary>
/// Cache-key factory that includes the configured audit schema alongside the context type
/// and design-time flag. EF Core caches compiled models per key; without the schema in the
/// key, a process that constructs <see cref="AuditDbContext"/> instances with
/// different schemas could reuse a model compiled for the wrong schema.
/// </summary>
internal sealed class AuditModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
    {
        var schema = (context as AuditDbContext)?.Schema ?? "audit";
        return (context.GetType(), schema, designTime);
    }
}
