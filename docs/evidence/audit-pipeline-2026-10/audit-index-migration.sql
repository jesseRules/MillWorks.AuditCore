BEGIN TRANSACTION;
CREATE INDEX [IX_AuditLogs_Envelope_Lookup] ON [audit].[AuditLogs] ([EnvelopeId]) INCLUDE ([PropertyName]) WHERE [EnvelopeId] IS NOT NULL;

INSERT INTO [audit].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261008124628_AddAuditLogEnvelopeLookupIndex', N'10.0.11');

COMMIT;
GO

