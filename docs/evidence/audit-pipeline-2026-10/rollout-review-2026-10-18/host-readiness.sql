SET NOCOUNT ON;
SET LOCK_TIMEOUT 2000;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
DECLARE @history nvarchar(max) = N'[]', @queue nvarchar(max) = N'[]', @handoffs nvarchar(max) = N'[]';
IF OBJECT_ID(N'audit.__EFMigrationsHistory', N'U') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @json=(SELECT MigrationId FROM audit.__EFMigrationsHistory ORDER BY MigrationId FOR JSON PATH)', N'@json nvarchar(max) OUTPUT', @json=@history OUTPUT;
IF OBJECT_ID(N'audit.AuditOutbox', N'U') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @json=(SELECT Status,EnvelopeVersion,COUNT_BIG(*) AS RowCount,MIN(CreatedAt) AS OldestCreatedAt,MAX(LeaseExpiresAt) AS LatestLeaseExpiry FROM audit.AuditOutbox GROUP BY Status,EnvelopeVersion FOR JSON PATH)', N'@json nvarchar(max) OUTPUT', @json=@queue OUTPUT;
IF OBJECT_ID(N'audit.AuditOutboxFailureHandoffs', N'U') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT @json=(SELECT Status,COUNT_BIG(*) AS RowCount FROM audit.AuditOutboxFailureHandoffs GROUP BY Status FOR JSON PATH)', N'@json nvarchar(max) OUTPUT', @json=@handoffs OUTPUT;
SELECT SYSUTCDATETIME() AS ObservedUtc, DB_NAME() AS DatabaseName,
    JSON_QUERY((SELECT s.name AS SchemaName,t.name AS TableName FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name='audit' ORDER BY t.name FOR JSON PATH)) AS AuditTables,
    JSON_QUERY((SELECT s.name AS SchemaName,t.name AS TableName FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.name='__EFMigrationsHistory' ORDER BY s.name FOR JSON PATH)) AS MigrationHistoryTables,
    JSON_QUERY(@history) AS AuditMigrations,
    JSON_QUERY(@queue) AS QueuedEnvelopeCounts,
    JSON_QUERY(@handoffs) AS FailureHandoffCounts
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
