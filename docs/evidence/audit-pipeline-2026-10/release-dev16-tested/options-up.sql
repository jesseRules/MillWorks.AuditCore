BEGIN TRANSACTION;
CREATE INDEX [IX_AuditEvents_User] ON [audit].[AuditEvents] ([User]);

INSERT INTO [audit].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261020103954_AddAuditEventUserIndex', N'10.0.13');

COMMIT;
GO

