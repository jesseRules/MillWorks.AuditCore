BEGIN TRANSACTION;
DROP INDEX [IX_AuditEvents_User] ON [audit].[AuditEvents];

DELETE FROM [audit].[__EFMigrationsHistory]
WHERE [MigrationId] = N'20261020103954_AddAuditEventUserIndex';

COMMIT;
GO

