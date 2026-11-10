-- Read-only check in the intended database, using a principal with catalog metadata visibility.
-- A completed crawl is not proof of zero indexing lag for later writes; verify a known event too.
SET NOCOUNT ON;
DECLARE @tableId int = OBJECT_ID(N'[audit].[AuditEvents]', N'U');
SELECT COALESCE(CONVERT(int, FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')), 0) AS fulltext_installed,
    CASE WHEN @tableId IS NULL THEN 0 ELSE 1 END AS audit_table_present;
SELECT cat.name AS catalog_name, i.name AS key_index, f.is_enabled,
    f.change_tracking_state_desc, f.stoplist_id, f.has_crawl_completed,
    f.crawl_type_desc, f.crawl_start_date, f.crawl_end_date,
    FULLTEXTCATALOGPROPERTY(cat.name, 'AccentSensitivity') AS accent_sensitive
FROM sys.fulltext_indexes f
JOIN sys.fulltext_catalogs cat ON cat.fulltext_catalog_id = f.fulltext_catalog_id
JOIN sys.indexes i ON i.object_id = f.object_id AND i.index_id = f.unique_index_id
WHERE f.object_id = @tableId;
SELECT c.name AS column_name, fc.language_id
FROM sys.fulltext_index_columns fc
JOIN sys.columns c ON c.object_id = fc.object_id AND c.column_id = fc.column_id
WHERE fc.object_id = @tableId ORDER BY c.name;
IF COALESCE(CONVERT(int, FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')), 0) <> 1
    THROW 51010, 'Audit word search unavailable: Full-Text Search is not installed.', 1;
IF NOT EXISTS (
    SELECT 1 FROM sys.fulltext_indexes f
    JOIN sys.fulltext_catalogs cat ON cat.fulltext_catalog_id = f.fulltext_catalog_id
    JOIN sys.indexes i ON i.object_id = f.object_id AND i.index_id = f.unique_index_id
    WHERE f.object_id = @tableId AND f.is_enabled = 1 AND f.has_crawl_completed = 1
      AND f.change_tracking_state = 'A' AND f.stoplist_id IS NULL
      AND cat.name = N'AuditEventWords' AND i.name = N'PK_AuditEvents'
      AND CONVERT(int, FULLTEXTCATALOGPROPERTY(cat.name, 'AccentSensitivity')) = 1
      AND 7 = (SELECT COUNT(*) FROM sys.fulltext_index_columns fc WHERE fc.object_id = @tableId)
      AND 7 = (SELECT COUNT(*) FROM sys.fulltext_index_columns fc
          JOIN sys.columns c ON c.object_id = fc.object_id AND c.column_id = fc.column_id
          WHERE fc.object_id = @tableId AND fc.language_id = 0
            AND c.name IN (N'JsonData', N'User', N'UserFullName', N'EventType', N'EntityType', N'EntityId', N'AdditionalData')))
    THROW 51011, 'Audit word search unavailable: index definition or population is not ready.', 1;
SELECT N'Ready for bounded live word-query verification.' AS verification_result;
