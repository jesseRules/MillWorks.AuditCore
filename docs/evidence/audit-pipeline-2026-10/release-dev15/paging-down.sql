BEGIN TRANSACTION;
DROP INDEX [IX_AuditEvents_Date_EventId] ON [audit].[AuditEvents];

DELETE FROM [audit].[__EFMigrationsHistory]
WHERE [MigrationId] = N'20261019182702_AddAuditEventPagingIndex';

COMMIT;
GO

