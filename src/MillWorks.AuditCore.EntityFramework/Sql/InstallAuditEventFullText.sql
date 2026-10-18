-- Run outside a user transaction, after full-text support is available.
-- Idempotent install for the default audit schema; no retained records are rewritten.
SET NOCOUNT ON;
IF FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') <> 1
    THROW 51000, 'SQL Server Full-Text Search must be installed before provisioning audit word search.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'AuditEventWords')
    CREATE FULLTEXT CATALOG [AuditEventWords] WITH ACCENT_SENSITIVITY = ON;
IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[audit].[AuditEvents]'))
    CREATE FULLTEXT INDEX ON [audit].[AuditEvents]
    ([JsonData] LANGUAGE 0, [User] LANGUAGE 0, [UserFullName] LANGUAGE 0,
     [EventType] LANGUAGE 0, [EntityType] LANGUAGE 0, [EntityId] LANGUAGE 0, [AdditionalData] LANGUAGE 0)
    KEY INDEX [PK_AuditEvents] ON [AuditEventWords]
    WITH CHANGE_TRACKING AUTO, STOPLIST = OFF;
