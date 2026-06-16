# GDPR Anonymization Re-Chaining

**Status:** Deferred
**Origin:** TamperDetectionIntegrityGaps.md finding #5
**Blocked By:** Merkle pipeline design, schema addition required
**Priority:** High

## Problem

`AuditComplianceService.AnonymizeUserDataAsync` overwrites `User` and `JsonData` fields to comply with GDPR erasure requests. Both fields are inputs to `ComputeEventHash`. After anonymization:

1. The stored `EventHash` no longer matches the computed hash
2. `VerifyChainIntegrityAsync` fires a Critical `AuditTamperAlert`
3. The alert is indistinguishable from actual tampering

This creates a forced choice:
- **Run verification**: False positives on every anonymized event, alert fatigue
- **Skip verification**: Actual tampering goes undetected
- **Don't anonymize**: GDPR non-compliance

## Current Behavior

```
Original event:
  User: "john.doe@example.com"
  JsonData: {"email": "john.doe@example.com", "action": "login"}
  EventHash: SHA256(canonical) = abc123...

After anonymization:
  User: "[ANONYMIZED]"
  JsonData: {"email": "[REDACTED]", "action": "login"}
  EventHash: abc123... (unchanged — now stale)

Verification:
  Computed hash: def456...
  Stored hash: abc123...
  Result: ❌ TAMPER DETECTED (false positive)
```

## Solution Approach

### Supersession Record Design

Record a signed proof that the hash change was authorized:

```csharp
public class AuditIntegritySupersession
{
    public Guid Id { get; set; }
    public Guid AuditEventId { get; set; }
    
    public string OriginalEventHash { get; set; }
    public string NewEventHash { get; set; }
    
    public string SupersessionType { get; set; }  // "GdprErasure", "Correction", etc.
    public DateTimeOffset SupersededAt { get; set; }
    public string SupersededBy { get; set; }      // User/system that authorized
    
    public string Signature { get; set; }         // Signs (original, new, type, timestamp)
    public int SignatureVersion { get; set; }
}
```

### Verification Change

1. When `EventHash` doesn't match computed hash, check for supersession record
2. If supersession exists and signature is valid:
   - Verify `OriginalEventHash` matches the stored (now-stale) hash
   - Verify `NewEventHash` matches the computed hash
   - Record as "Lawful modification" rather than tamper
3. If no valid supersession: treat as tamper (existing behavior)

### Atomicity Requirement

Anonymization and supersession must be atomic:

```csharp
await using var transaction = await context.Database.BeginTransactionAsync();

// 1. Capture original hash
var originalHash = auditEvent.EventHash;

// 2. Modify the event
auditEvent.User = "[ANONYMIZED]";
auditEvent.JsonData = AnonymizeJsonData(auditEvent.JsonData);

// 3. Compute new hash
var newHash = ComputeEventHash(auditEvent);
auditEvent.EventHash = newHash;

// 4. Create signed supersession record
var supersession = new AuditIntegritySupersession
{
    OriginalEventHash = originalHash,
    NewEventHash = newHash,
    SupersessionType = "GdprErasure",
    // ...sign...
};
context.AuditIntegritySupersessions.Add(supersession);

// 5. Update integrity record if exists
var integrity = await context.AuditIntegrityRecords
    .FirstOrDefaultAsync(i => i.AuditEventId == auditEvent.Id);
if (integrity != null)
{
    integrity.EventHash = newHash;
    integrity.Hmac = ComputeHmac(auditEvent);  // Re-sign with v3 algorithm
}

await context.SaveChangesAsync();
await transaction.CommitAsync();
```

## Design Considerations

### Chain Continuity

The `PreviousEventHash` chain links events by their hash. Options:

1. **Don't re-link**: Supersession records explain the gap
2. **Re-link successor**: Update event k+1's `PreviousEventHash` to the new hash
   - Requires another supersession record for event k+1
   - Cascades through the chain — expensive

Recommendation: Option 1. The supersession record proves the modification was lawful; the chain doesn't need to be physically re-linked.

### Merkle Tree Impact

When the Merkle pipeline is implemented:
- Anonymized events change their leaf hash
- The Merkle root changes
- Published anchors become stale

Options:
1. **Supersession at anchor level**: Record that anchor X was superseded by anchor Y due to anonymization
2. **Exclusion proofs**: Prove the event was in the original tree, prove the anonymized event is in the new tree
3. **Separate anonymization tree**: Anonymized events move to a parallel structure

This is why the fix is deferred — the right design depends on Merkle pipeline decisions.

### Audit of the Audit

Who anonymized what, when, and why is itself audit-worthy:
- The supersession record serves as this audit trail
- Consider a separate `GdprErasureRequest` entity linking to multiple supersessions

## Schema Addition

```sql
CREATE TABLE [audit].[AuditIntegritySupersession] (
    Id uniqueidentifier PRIMARY KEY,
    AuditEventId uniqueidentifier NOT NULL,
    OriginalEventHash nvarchar(128) NOT NULL,
    NewEventHash nvarchar(128) NOT NULL,
    SupersessionType nvarchar(50) NOT NULL,
    SupersededAt datetimeoffset NOT NULL,
    SupersededBy nvarchar(256) NOT NULL,
    Signature nvarchar(1024) NOT NULL,
    SignatureVersion int NOT NULL,
    
    CONSTRAINT FK_Supersession_AuditEvent 
        FOREIGN KEY (AuditEventId) REFERENCES [audit].[AuditEvent](Id)
);

CREATE INDEX IX_Supersession_AuditEventId 
    ON [audit].[AuditIntegritySupersession](AuditEventId);
```

## Implementation Outline

1. Finalize Merkle pipeline design (blocking)
2. Add `AuditIntegritySupersession` entity and migration
3. Implement `SupersessionService` with signing logic
4. Modify `AuditComplianceService.AnonymizeUserDataAsync` to create supersession atomically
5. Modify `TamperDetectionService` verification to check supersession records
6. Add tests: anonymize → verify → expect clean (not tamper alert)

## Dependencies

- Merkle pipeline design (tree structure, anchor format)
- `RecursiveAnonymization.md` — JSON anonymization must be recursive
- Signature key management (same as integrity signatures, or separate?)

## Related Documents

- `TamperDetectionIntegrityGaps.md` — Origin finding
- `TailTruncationDetection.md` — Also deferred to Merkle pipeline
- `RecursiveAnonymization.md` — Coordinate implementation
