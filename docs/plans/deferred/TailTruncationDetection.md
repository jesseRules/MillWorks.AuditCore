# Tail Truncation Detection

**Status:** Deferred
**Origin:** TamperDetectionIntegrityGaps.md finding #3
**Blocked By:** Merkle pipeline design
**Priority:** High

## Problem

Deleting the last N integrity rows plus their audit events leaves a contiguous sequence 1..k and an intact chain. Both `VerifyChainIntegrityAsync` and `VerifySequenceIntegrityAsync` pass — they only detect gaps and linkage breaks, not truncation from the tail. This is the canonical "cover your tracks" attack on an audit log: delete the evidence of your intrusion, and the remaining chain verifies clean.

### Current Behavior

```
Before attack:  [1] → [2] → [3] → [4] → [5]  (chain intact)
After attack:   [1] → [2] → [3]              (chain still intact, events 4-5 gone)
Verification:   ✅ PASS — no gaps, all links valid
```

The chain has no "head anchor" — nothing external records what the highest sequence number should be.

## Solution Approach

### Interim Fix: External Head Anchor

Periodically persist `(headHash, maxSequenceNumber, timestamp)` to an external or WORM location:

1. **External storage options:**
   - Separate database/schema with different credentials
   - Cloud object storage with immutability policy (S3 Object Lock, Azure Immutable Blob)
   - Hardware security module (HSM) counter
   - Blockchain anchor (for high-assurance environments)

2. **Verification change:**
   - `VerifyChainIntegrityAsync` fetches the latest anchor
   - Compares `MAX(SequenceNumber)` against anchored value
   - Reports truncation if current max < anchored max

### Long-Term Fix: Merkle Pipeline

The Merkle batching pipeline (see `NextStepsDocument.md`) provides a more robust solution:

1. **Merkle tree roots** are published to external anchors at regular intervals
2. **Inclusion proofs** allow verification that specific events existed at anchor time
3. **Truncation detection** is inherent — if the tree doesn't contain the expected leaf count, it's detectable

## Design Considerations

### Anchor Frequency vs. Detection Window

| Frequency | Detection Window | Storage Cost | Verification Cost |
|-----------|------------------|--------------|-------------------|
| Per-event | Immediate | High | Low |
| Per-minute | ≤1 minute | Medium | Low |
| Per-batch (Merkle) | ≤batch interval | Low | Medium (proof verification) |

### Anchor Trust Model

The anchor must be:
- **Write-once**: Attacker cannot modify historical anchors
- **Append-only**: Attacker cannot delete anchors
- **Independent**: Compromising the audit DB doesn't compromise the anchor

If the same credentials that access `AuditEvents` can also delete anchors, the protection is illusory.

### Failure Modes

1. **Anchor storage unavailable**: Should verification fail closed or degrade?
2. **Clock skew**: Anchors with future timestamps
3. **Anchor corruption**: Invalid format or signature

## Implementation Outline

1. Define `IAuditAnchorStore` interface:
   ```csharp
   public interface IAuditAnchorStore
   {
       Task<AuditAnchor> GetLatestAnchorAsync(CancellationToken ct);
       Task PublishAnchorAsync(AuditAnchor anchor, CancellationToken ct);
   }
   ```

2. Add anchor publication to `IntegrityWriteBatcher` or a dedicated background service

3. Extend `TamperDetectionService.VerifyChainIntegrityAsync` to compare against anchor

4. Implement at least one `IAuditAnchorStore`:
   - `FileSystemAnchorStore` (for testing/simple deployments)
   - `S3ObjectLockAnchorStore` or `AzureImmutableBlobAnchorStore` (production)

5. Add configuration for anchor frequency and failure behavior

## Dependencies

- Merkle pipeline design decisions (batch size, tree structure)
- External storage selection and authentication model
- Failure-mode policy decisions

## Related Documents

- `NextStepsDocument.md` — Merkle pipeline overview
- `TamperDetectionIntegrityGaps.md` — Origin finding
- `GdprAnonymizationReChaining.md` — Also deferred to Merkle pipeline
