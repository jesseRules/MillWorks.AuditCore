# Security Event Integrity

**Status:** Proposed  
**Date:** 2026-06-07  
**Scope:** Tamper-evidence model for SecurityEvents table  
**Parent:** SecurityEventHardeningRoadmap.md (Workstream 1)

## Problem

`SecurityEvents` are durable and queryable but not tamper-evident. An attacker with database write access could modify, delete, or reorder security events without detection. For break-glass and other critical security events, this gap undermines forensic reliability.

The main audit-event hash chain provides tamper-evidence for `AuditEvents`, but `SecurityEvents` are intentionally separate. This plan establishes integrity guarantees for security events without coupling them to the higher-volume audit-event chain.

## Decision

Add a dedicated integrity chain for security events, modeled after the existing audit-event integrity system but independent.

Use a separate chain because:

- Security events are low-volume, high-criticality; audit events are high-volume.
- Verification cadence and alerting thresholds differ.
- Coupling would force security-event verification to wait on audit-event batching.
- Independent chains allow different retention and archival policies.

## Design

### SecurityEventIntegrity Table

```sql
CREATE TABLE SecurityEventIntegrity (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    SecurityEventId BIGINT NOT NULL,
    SequenceNumber BIGINT NOT NULL,
    EventHash VARBINARY(32) NOT NULL,      -- SHA-256
    PreviousHash VARBINARY(32) NOT NULL,   -- Chain link
    ChainId NVARCHAR(64) NOT NULL,         -- Namespace for multi-tenant chains
    ComputedAt DATETIMEOFFSET NOT NULL,
    CONSTRAINT FK_SecurityEventIntegrity_SecurityEvent 
        FOREIGN KEY (SecurityEventId) REFERENCES SecurityEvents(Id),
    CONSTRAINT UQ_SecurityEventIntegrity_Chain_Sequence 
        UNIQUE (ChainId, SequenceNumber)
);

CREATE INDEX IX_SecurityEventIntegrity_SecurityEventId 
    ON SecurityEventIntegrity(SecurityEventId);
CREATE INDEX IX_SecurityEventIntegrity_ChainId_ComputedAt 
    ON SecurityEventIntegrity(ChainId, ComputedAt);
```

### Entity

```csharp
public sealed class SecurityEventIntegrityEntity
{
    public long Id { get; set; }
    public long SecurityEventId { get; set; }
    public long SequenceNumber { get; set; }
    public byte[] EventHash { get; set; } = Array.Empty<byte>();
    public byte[] PreviousHash { get; set; } = Array.Empty<byte>();
    public string ChainId { get; set; } = string.Empty;
    public DateTimeOffset ComputedAt { get; set; }
    
    public AuditSecurityEventEntity? SecurityEvent { get; set; }
}
```

### Hash Computation

Compute `EventHash` over canonical security-event fields:

```csharp
var payload = new
{
    entity.Id,
    entity.EventType,
    entity.Severity,
    entity.Message,
    entity.DetectedAt,
    entity.DetectedBy,
    entity.TenantId,
    entity.ActorUserId,
    entity.SubjectUserId,
    entity.CorrelationId,
    entity.Operation,
    entity.SourceIpHash,
    entity.UserAgentHash,
    entity.DetailsJson,
    entity.RelatedAuditEventId
};
var json = JsonSerializer.Serialize(payload, StableJsonOptions);
return SHA256.HashData(Encoding.UTF8.GetBytes(json));
```

Use deterministic JSON serialization (sorted keys, no whitespace) for reproducible hashes.

### Chain Link

```csharp
ChainHash = SHA256.HashData(
    EventHash.Concat(PreviousHash).Concat(BitConverter.GetBytes(SequenceNumber)).ToArray()
);
```

Genesis record uses `PreviousHash = SHA256("genesis:{ChainId}")`.

### Recording Path

Integrity records are written in the same transaction as `RecordEventAsync`:

1. Insert `AuditSecurityEventEntity`
2. Fetch previous integrity record for chain (or genesis)
3. Compute hashes
4. Insert `SecurityEventIntegrityEntity`
5. Commit transaction

This preserves fail-closed semantics: if integrity write fails, the security event insert rolls back.

### Verification

Add `ISecurityEventIntegrityService`:

```csharp
Task<SecurityEventIntegrityResult> VerifyChainAsync(
    string chainId,
    DateTimeOffset? since = null,
    CancellationToken cancellationToken = default);

Task<SecurityEventIntegrityResult> VerifyEventAsync(
    long securityEventId,
    CancellationToken cancellationToken = default);
```

`SecurityEventIntegrityResult`:

```csharp
public sealed record SecurityEventIntegrityResult(
    bool IsValid,
    long EventsVerified,
    long? FirstInvalidSequence,
    string? FailureReason);
```

Verification detects:

- Hash mismatch (event content modified)
- Chain link mismatch (sequence tampered)
- Sequence gaps (events deleted)
- Missing integrity records

### ChainId Strategy

Default `ChainId` to `"default"` for single-tenant deployments. Multi-tenant deployments can use `"tenant:{TenantId}"` to isolate chains, enabling per-tenant verification and archival.

## Implementation Outline

1. Add `SecurityEventIntegrityEntity` and EF configuration.
2. Add migration for `SecurityEventIntegrity` table.
3. Add `ISecurityEventIntegrityRepository` with `GetLatestForChainAsync`, `AddAsync`.
4. Modify `AuditSecurityEventService.RecordEventAsync` to write integrity record in same transaction.
5. Add `ISecurityEventIntegrityService` with `VerifyChainAsync`, `VerifyEventAsync`.
6. Add structured logging for verification results.
7. Update `GetCriticalEventsAsync` to include integrity status if requested.

## Tests

1. `RecordEventAsync` creates integrity record with correct hash.
2. Chain links correctly to previous record.
3. Genesis record created for first event in chain.
4. Verification passes for unmodified chain.
5. Verification fails on content modification (hash mismatch).
6. Verification fails on deleted record (sequence gap).
7. Verification fails on reordered records (chain link mismatch).
8. Multi-tenant chains are isolated.
9. Partial verification (`since` parameter) works correctly.
10. Transaction rollback on integrity write failure also rolls back event.

## Non-Goals

- HMAC signatures with external key management (future enhancement).
- Real-time integrity alerts (use scheduled verification + existing alerting).
- Merkle tree batching (security events are low-volume; per-event chaining is acceptable).
- Cross-chain linking to audit-event integrity (keep independent).

## Open Questions

- Should verification run automatically on a schedule, or only on-demand?
- Should integrity failures trigger security events themselves (recursive concern)?
- Should `ChainId` be configurable per-event or derived from `TenantId`?
