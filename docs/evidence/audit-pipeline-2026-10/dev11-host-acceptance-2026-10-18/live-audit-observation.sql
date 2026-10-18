SET NOCOUNT ON;
SET LOCK_TIMEOUT 3000;
DECLARE @since datetimeoffset = '2026-10-18T21:33:21+00:00';
SELECT SYSUTCDATETIME() AS ObservedUtc, @since AS HostStartedUtc,
 (SELECT COUNT_BIG(*) FROM audit.AuditEnvelopeIdentities i WHERE AcceptedAt>=@since AND Channel='event' AND NOT EXISTS(SELECT 1 FROM audit.AuditEvents e WHERE e.EventId=i.LogicalId)) AS EventReceiptsMissingSource,
 (SELECT COUNT_BIG(*) FROM audit.AuditEnvelopeIdentities i WHERE AcceptedAt>=@since AND Channel='entity' AND NOT EXISTS(SELECT 1 FROM audit.AuditLogs l WHERE l.EnvelopeId=i.LogicalId)) AS EntityReceiptsMissingSource,
 JSON_QUERY((SELECT COUNT_BIG(*) AS SourceRows,COUNT(DISTINCT e.EventId) AS DistinctEvents,
  COUNT(DISTINCT i.LogicalId) AS DistinctReceipts,COUNT(DISTINCT i.TenantId) AS DistinctTenants,
  SUM(CASE WHEN e.TenantId=i.TenantId THEN 1 ELSE 0 END) AS MatchingTenantRows
  FROM audit.AuditEvents e JOIN audit.AuditEnvelopeIdentities i ON i.Channel='event' AND i.LogicalId=e.EventId
  WHERE e.EventType='HubAccess' AND e.InsertedDate >= '2026-10-18T21:52:32+00:00'
   AND e.EntityId='20000001-0000-0000-0000-000000000001' FOR JSON PATH,WITHOUT_ARRAY_WRAPPER)) AS LiveCrmPublicationProof,
 JSON_QUERY((SELECT Channel, Kind, COUNT_BIG(*) AS ReceiptCount,
  SUM(CONVERT(bigint,CASE WHEN TenantId IS NOT NULL THEN 1 ELSE 0 END)) AS TenantBoundReceipts
  FROM audit.AuditEnvelopeIdentities i WHERE AcceptedAt >= @since GROUP BY Channel,Kind FOR JSON PATH)) AS Receipts,
 JSON_QUERY((SELECT TOP (100) e.EventType,COUNT_BIG(*) AS ReceiptCount,MIN(i.AcceptedAt) AS FirstAcceptedAt,MAX(i.AcceptedAt) AS LastAcceptedAt
  FROM audit.AuditEnvelopeIdentities i JOIN audit.AuditEvents e ON i.Channel='event' AND e.EventId=i.LogicalId
  WHERE i.AcceptedAt>=@since GROUP BY e.EventType ORDER BY e.EventType FOR JSON PATH)) AS AcceptedEventTypes,
 JSON_QUERY((SELECT TOP (100) l.EntityType,COUNT_BIG(*) AS PropertyRows,COUNT(DISTINCT l.EnvelopeId) AS LogicalEnvelopes
  FROM audit.AuditLogs l JOIN audit.AuditEnvelopeIdentities i ON i.Channel='entity' AND i.LogicalId=l.EnvelopeId
  WHERE i.AcceptedAt>=@since GROUP BY l.EntityType ORDER BY l.EntityType FOR JSON PATH)) AS AcceptedEntityTypes
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER;
