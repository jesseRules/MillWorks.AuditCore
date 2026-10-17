using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Sinks.Identity;

namespace MillWorks.AuditCore.Services.Sinks;

internal sealed partial class AuditOutboxWriter
{
    private async Task<int> WriteViaRawSqlAsync(DbContext db,
        IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
            throw new AuditOutboxAtomicityException("The unmapped outbox path requires SQL Server; map the outbox and identity entities for other providers.");
        var transaction = db.Database.CurrentTransaction!;
        if (!transaction.SupportsSavepoints)
            throw new AuditOutboxAtomicityException("The unmapped outbox identity protocol requires transaction savepoints; disable MARS or map the outbox and identity entities.");
        // SQL Server limits transaction/savepoint identifiers to 32 characters.
        var savepoint = "AI" + Guid.NewGuid().ToString("N")[..30];
        await transaction.CreateSavepointAsync(savepoint, ct);
        try
        {
            var count = 0;
            // Preserve the first original envelope for each logical identity.
            var unique = rows.DistinctBy(r => r.idempotencyKey).ToArray();
            foreach (var chunk in unique.Chunk(128))
                count += await WriteIdentityChunkAsync(db, chunk, ct);
            return count;
        }
        catch (Exception original)
        {
            try { await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None); }
            catch (Exception cleanup) { original.Data["AuditIdentityRollbackFailure"] = cleanup; }
            throw;
        }
    }

    private async Task<int> WriteIdentityChunkAsync(DbContext db,
        IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = db.Database.GetCommandTimeout() ?? 30;
        var values = new List<string>();
        foreach (var (json, version, id) in rows.OrderBy(r => r.idempotencyKey))
        {
            var envelope = Recovery.QueuedEnvelopeValidator.Read(json, version);
            object?[] fields = [Guid.NewGuid(), json, version, id, (int)envelope.Kind,
                envelope.GovernanceIdentity?.TenantId, AuditEnvelopeContent.Version, AuditEnvelopeContent.Fingerprint(envelope)];
            var names = new List<string>();
            foreach (var value in fields)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@p" + command.Parameters.Count;
                parameter.Value = value ?? DBNull.Value;
                if (value is null) parameter.DbType = System.Data.DbType.Guid;
                command.Parameters.Add(parameter);
                names.Add(parameter.ParameterName);
            }
            values.Add("(" + string.Join(",", names) + ")");
        }
        command.CommandText = $"""
            SET NOCOUNT ON;
            DECLARE @input TABLE (RowId uniqueidentifier, Json nvarchar(max), Version int,
                LogicalId uniqueidentifier PRIMARY KEY, Kind int, TenantId uniqueidentifier NULL,
                ContentVersion int, Fingerprint nvarchar(64));
            INSERT INTO @input VALUES {string.Join(",", values)};
            DECLARE @bad TABLE (Status int, LogicalId uniqueidentifier);
            INSERT INTO @bad
            SELECT CASE WHEN r.ContentVersion <> i.ContentVersion OR o.Id IS NULL THEN 2 ELSE 1 END, i.LogicalId
            FROM @input i
            JOIN [{_schema}].[AuditEnvelopeIdentities] r WITH (UPDLOCK,HOLDLOCK)
                ON r.Channel = N'outbox' AND r.LogicalId = i.LogicalId
            LEFT JOIN [{_schema}].[AuditOutbox] o WITH (UPDLOCK,HOLDLOCK) ON o.IdempotencyKey = i.LogicalId
            WHERE r.ContentVersion <> i.ContentVersion OR o.Id IS NULL OR r.Kind <> i.Kind
                OR (r.TenantId <> i.TenantId OR (r.TenantId IS NULL AND i.TenantId IS NOT NULL)
                    OR (r.TenantId IS NOT NULL AND i.TenantId IS NULL))
                OR r.Fingerprint COLLATE Latin1_General_100_BIN2 <> i.Fingerprint COLLATE Latin1_General_100_BIN2;
            INSERT INTO @bad
            SELECT 2, i.LogicalId FROM @input i
            JOIN [{_schema}].[AuditOutbox] o WITH (UPDLOCK,HOLDLOCK) ON o.IdempotencyKey = i.LogicalId
            WHERE NOT EXISTS (SELECT 1 FROM [{_schema}].[AuditEnvelopeIdentities] r WITH (UPDLOCK,HOLDLOCK)
                WHERE r.Channel = N'outbox' AND r.LogicalId = i.LogicalId);
            IF EXISTS (SELECT 1 FROM @bad)
                SELECT Status, LogicalId, 0 AS Inserted FROM @bad;
            ELSE
            BEGIN
                INSERT INTO [{_schema}].[AuditEnvelopeIdentities]
                    (Channel, LogicalId, Kind, TenantId, ContentVersion, Fingerprint, AcceptedAt)
                SELECT N'outbox', i.LogicalId, i.Kind, i.TenantId, i.ContentVersion, i.Fingerprint, SYSDATETIMEOFFSET()
                FROM @input i WHERE NOT EXISTS (
                    SELECT 1 FROM [{_schema}].[AuditEnvelopeIdentities] r WITH (UPDLOCK,HOLDLOCK)
                    WHERE r.Channel = N'outbox' AND r.LogicalId = i.LogicalId);
                INSERT INTO [{_schema}].[AuditOutbox]
                    (Id, EnvelopeJson, EnvelopeVersion, Status, CreatedAt, AttemptCount, IdempotencyKey)
                SELECT RowId, Json, Version, 0, SYSDATETIMEOFFSET(), 0, LogicalId FROM @input i
                WHERE NOT EXISTS (SELECT 1 FROM [{_schema}].[AuditOutbox] o WITH (UPDLOCK,HOLDLOCK)
                    WHERE o.IdempotencyKey = i.LogicalId);
                SELECT 0 AS Status, CAST(NULL AS uniqueidentifier) AS LogicalId, @@ROWCOUNT AS Inserted;
            END
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var conflicts = new List<Guid>();
        var unverified = new List<Guid>();
        var inserted = 0;
        while (await reader.ReadAsync(ct))
        {
            var status = reader.GetInt32(0);
            if (status == 0) inserted = reader.GetInt32(2);
            else if (status == 1) conflicts.Add(reader.GetGuid(1));
            else unverified.Add(reader.GetGuid(1));
        }
        if (conflicts.Count > 0) throw new AuditIdentityConflictException(conflicts);
        if (unverified.Count > 0) throw new AuditIdentityConflictException(unverified, unverified: true);
        return inserted;
    }
}
