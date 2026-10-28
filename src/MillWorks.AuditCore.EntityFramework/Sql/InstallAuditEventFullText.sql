-- Run in the intended database outside a user/implicit transaction, after full-text support exists.
-- Default audit schema only. Refuse mismatched objects; never drop/rebuild an existing index.
-- Creation starts asynchronous population; run VerifyAuditEventFullText.sql before acceptance.
-- Dynamic DDL defers full-text feature binding until after the capability/schema guards.
SET NOCOUNT ON;
IF @@TRANCOUNT <> 0 OR (@@OPTIONS & 2) = 2
    THROW 51000, 'Run audit full-text provisioning outside user and implicit transactions.', 1;
IF COALESCE(CONVERT(int, FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')), 0) <> 1
    THROW 51001, 'SQL Server Full-Text Search must be installed before provisioning audit word search.', 1;
DECLARE @tableId int = OBJECT_ID(N'[audit].[AuditEvents]', N'U');
IF @tableId IS NULL
    THROW 51002, 'Expected audit.AuditEvents in the selected database; apply the owning schema first.', 1;
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id = @tableId
    AND name IN (N'JsonData', N'User', N'UserFullName', N'EventType', N'EntityType', N'EntityId', N'AdditionalData')) <> 7
    THROW 51003, 'Audit full-text columns are missing; reconcile the owning schema before provisioning.', 1;
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes i
    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = @tableId AND i.name = N'PK_AuditEvents' AND i.is_unique = 1
      AND i.is_disabled = 0 AND i.has_filter = 0 AND c.name = N'EventId' AND c.is_nullable = 0
      AND 1 = (SELECT COUNT(*) FROM sys.index_columns k WHERE k.object_id = i.object_id AND k.index_id = i.index_id AND k.key_ordinal > 0))
    THROW 51004, 'Expected enabled unique single-column PK_AuditEvents on non-null EventId.', 1;
IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'AuditEventWords')
   AND COALESCE(CONVERT(int, FULLTEXTCATALOGPROPERTY(N'AuditEventWords', 'AccentSensitivity')), 0) <> 1
    THROW 51005, 'Existing AuditEventWords catalog has incompatible accent sensitivity; review explicitly.', 1;
IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = @tableId)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM sys.fulltext_indexes f
        JOIN sys.fulltext_catalogs cat ON cat.fulltext_catalog_id = f.fulltext_catalog_id
        JOIN sys.indexes i ON i.object_id = f.object_id AND i.index_id = f.unique_index_id
        WHERE f.object_id = @tableId AND f.is_enabled = 1 AND f.change_tracking_state = 'A'
          AND f.stoplist_id IS NULL AND cat.name = N'AuditEventWords' AND i.name = N'PK_AuditEvents'
          AND 7 = (SELECT COUNT(*) FROM sys.fulltext_index_columns fc WHERE fc.object_id = @tableId)
          AND 7 = (SELECT COUNT(*) FROM sys.fulltext_index_columns fc
              JOIN sys.columns c ON c.object_id = fc.object_id AND c.column_id = fc.column_id
              WHERE fc.object_id = @tableId AND fc.language_id = 0
                AND c.name IN (N'JsonData', N'User', N'UserFullName', N'EventType', N'EntityType', N'EntityId', N'AdditionalData')))
        THROW 51006, 'Existing audit full-text index differs from the required definition; no automatic replacement performed.', 1;
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'AuditEventWords')
        EXEC(N'CREATE FULLTEXT CATALOG [AuditEventWords] WITH ACCENT_SENSITIVITY = ON;');
    EXEC(N'CREATE FULLTEXT INDEX ON [audit].[AuditEvents]
    ([JsonData] LANGUAGE 0, [User] LANGUAGE 0, [UserFullName] LANGUAGE 0,
     [EventType] LANGUAGE 0, [EntityType] LANGUAGE 0, [EntityId] LANGUAGE 0, [AdditionalData] LANGUAGE 0)
    KEY INDEX [PK_AuditEvents] ON [AuditEventWords]
    WITH CHANGE_TRACKING AUTO, STOPLIST = OFF;');
END;
SELECT N'Definition accepted; population and live query acceptance remain separate.' AS provisioning_result;
