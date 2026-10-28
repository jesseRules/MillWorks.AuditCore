using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.EntityFramework.Repositories;

/// <summary>Reads low-cardinality filter values with bounded index seeks instead of scanning every event.</summary>
public static class AuditEventDistinctValues
{
    private const int ProbeLimit = 128;

    /// <summary>Distinct nonempty display users, in database order.</summary>
    public static Task<List<string>> UsersAsync(AuditDbContext context, int? limit = null,
        CancellationToken cancellationToken = default) => ReadAsync(context, true, true, limit, cancellationToken);

    /// <summary>Distinct event types; repository callers retain empty values, search callers exclude them.</summary>
    public static Task<List<string>> EventTypesAsync(AuditDbContext context, bool excludeEmpty = false,
        int? limit = null, CancellationToken cancellationToken = default) =>
        ReadAsync(context, false, excludeEmpty, limit, cancellationToken);

    private static Task<List<string>> ReadAsync(AuditDbContext context, bool users, bool excludeEmpty,
        int? limit, CancellationToken cancellationToken)
    {
        if (limit is <= 0) return Task.FromResult(new List<string>());
        var propertyName = users ? nameof(AuditEventEntity.User) : nameof(AuditEventEntity.EventType);
        var entity = context.Model.FindEntityType(typeof(AuditEventEntity))!;
        var property = entity.FindProperty(propertyName)!;
        // Preserve model filters/conversions and provider semantics rather than bypassing them with raw SQL.
        if (!context.Database.IsSqlServer() || entity.BaseType is not null || entity.GetDerivedTypes().Any() ||
            entity.GetDeclaredQueryFilters().Any() ||
            property.GetValueConverter() is not null ||
            property.GetMaxLength() != 256)
        {
            IQueryable<string> values = users
                ? context.AuditEvents.Where(e => e.User != null && e.User != "").Select(e => e.User!)
                : context.AuditEvents.Where(e => e.EventType != null).Select(e => e.EventType!);
            if (excludeEmpty) values = values.Where(value => value != "");
            var ordered = values.Distinct().OrderBy(value => value);
            return (limit.HasValue ? ordered.Take(limit.Value) : ordered).ToListAsync(cancellationToken);
        }
        var table = entity.GetTableName()!;
        var schema = entity.GetSchema();
        var column = property.GetColumnName(StoreObjectIdentifier.Table(table, schema))!;
        static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
        var qualifiedTable = schema is null ? Quote(table) : Quote(schema) + "." + Quote(table);
        var key = Quote(column);
        var index = users ? "IX_AuditEvents_User" : "IX_AuditEvents_EventType";
        var predicate = key + " IS NOT NULL" + (excludeEmpty ? " AND " + key + " <> N''" : "");
        // Identifiers come only from the EF model; caller limits remain SQL parameters. Every loop is bounded.
        // Missing/not-yet-applied indexes and high-cardinality inputs use the original DISTINCT query.
        var sql = $"""
            DECLARE @values TABLE ([Value] nvarchar(256) NOT NULL);
            DECLARE @value nvarchar(256), @steps int = 0;
            IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(@table) AND name=@index
                       AND is_disabled=0 AND is_hypothetical=0)
               AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(@table) AND name=@column
                   AND collation_name <> CONVERT(sysname,DATABASEPROPERTYEX(DB_NAME(), 'Collation')))
            BEGIN
                SELECT @value=MIN({key}) FROM {qualifiedTable} WHERE {predicate};
                WHILE @value IS NOT NULL AND @steps < @probe AND @steps < @limit
                BEGIN
                    INSERT @values VALUES(@value);
                    SET @steps += 1;
                    SELECT @value=MIN({key}) FROM {qualifiedTable}
                    WHERE {predicate} AND {key}>@value;
                END;
                IF @value IS NULL OR @steps >= @limit
                BEGIN
                    SELECT [Value] FROM @values ORDER BY [Value];
                    RETURN;
                END;
            END;
            SELECT DISTINCT TOP(@limit) {key} AS [Value] FROM {qualifiedTable}
            WHERE {predicate} ORDER BY {key};
            """;
        return context.Database.SqlQueryRaw<string>(sql,
            new SqlParameter("@table", qualifiedTable), new SqlParameter("@index", index),
            new SqlParameter("@column", column),
            new SqlParameter("@probe", ProbeLimit), new SqlParameter("@limit", limit ?? int.MaxValue))
            .ToListAsync(cancellationToken);
    }
}
