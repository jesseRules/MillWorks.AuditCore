BEGIN TRANSACTION;
CREATE INDEX [IX_AuditEvents_Date_EventId] ON [audit].[AuditEvents] ([InsertedDate] DESC, [EventId] DESC);

INSERT INTO [audit].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261019182702_AddAuditEventPagingIndex', N'10.0.13');

COMMIT;
GO

