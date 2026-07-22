# Merkle Anchoring Pipeline

**Status:** Design (unblocks three deferred High-priority items)
**Origin:** Realizes the `NextStepsDocument.md` reference cited (but never written) by
`completed/TamperDetectionIntegrityGaps.md` (finding #3) and `deferred/TailTruncationDetection.md`.
**Priority:** High — it is the **blocking dependency** for `deferred/TailTruncationDetection.md`,
`deferred/GdprAnonymizationReChaining.md`, and (transitively) `deferred/RecursiveAnonymization.md`.
**Builds on:** the completed `BatchPublishingRedesign.md` batch substrate (Slices A–F: `AuditEnvelope.EnvelopeId`
stable identity, `IAuditBatchProcessor`, idempotent leased outbox) and the existing hash chain
(`AuditIntegrity`: `EventHash`/`PreviousHash`/`SequenceNumber`/`HmacSignature`).

> **This is a design, not a build ticket.** It exists so the three deferred items stop pointing at a phantom
> doc. It settles the tree structure, anchor format, proof semantics, and the anonymization interaction they
> each said they were blocked on — then phases the work. Nothing here is committed until scheduled.

---

## Why a Merkle layer (what the linear chain cannot do)

The hash chain is tamper-**evident** for insertion/deletion/modification *within* the sequence, but it has two
structural blind spots the deferred plans already identified:

1. **Tail truncation** (`TailTruncationDetection.md`). Deleting the last N rows leaves a contiguous, valid-looking
   chain `1..k`. `VerifyChainIntegrityAsync`/`VerifySequenceIntegrityAsync` pass. Nothing external pins "the head
   should be at least sequence M."
2. **No compact third-party proof.** Proving a specific record is present, or that the log has only grown
   (append-only) between two points, requires re-walking the chain — you cannot hand an auditor, a client, or a
   peer institution an O(log n) proof.

A Merkle tree over the existing leaf hashes closes both: an externally-anchored **Signed Tree Head (STH)** pins
the leaf count (truncation becomes inherently detectable), and **inclusion / consistency proofs** give compact,
privacy-preserving third-party verification. This is the Certificate-Transparency verifiable-log pattern.

**Additive, not a rewrite.** The chain + HMAC stay exactly as they are. The Merkle tree's *leaf* is the record's
existing `EventHash`; the tree is a second index over the same immutable rows. Both verifiers run.

## Standard to build to

**RFC 6962** (Certificate Transparency v1) and **RFC 9162** (CT v2). Non-negotiable specifics:

- **Domain-separated hashing** (second-preimage resistance): leaf hash = `SHA-256(0x00 ‖ leafData)`, interior node
  = `SHA-256(0x01 ‖ left ‖ right)`. A tree that hashes leaves and nodes the same way is forgeable — this is the
  most common Merkle mistake.
- **Merkle Tree Hash (MTH)**, **audit path (inclusion) proofs**, and **consistency proofs** exactly per RFC 6962 §2.1.
- **Signed Tree Head**: `{tree_size, timestamp, root_hash}` signed with a dedicated key.
- Reference implementation to study: Google **Trillian**; the CT ecosystem's monitor/auditor role model.

## Where it lives

- **The primitive → `MillWorks.Cryptography`.** A Merkle tree with RFC-6962 domain separation, MTH, and the two
  proof algorithms is exactly a shared, key-agnostic crypto primitive with **known-answer test vectors** — it
  belongs beside the AES-GCM frame, the hasher, and the JCS canonicalizer, not hand-rolled inside AuditCore.
  (Consistent with Cryptography's "one hasher, published KATs, zero third-party crypto deps" charter.)
- **The pipeline → `MillWorks.AuditCore`.** AuditCore consumes the primitive: it builds epochs from batched
  envelopes, seals STHs, persists proofs, publishes anchors, and extends verification. Signing/anchoring keys
  stay in AuditCore's existing key management (Key Vault / file-based, same as the integrity HMAC).

## Architecture

```
Batched envelopes (BatchPublishingRedesign: stable EnvelopeId, idempotent outbox)
        │  leaf = existing EventHash  (SHA-256(0x00 ‖ canonical(record)))
        ▼
Epoch accumulator ──seal──▶ Merkle tree (Cryptography primitive) ──▶ root_hash
        │                                                              │
        │                                                     Signed Tree Head {size, ts, root}
        ▼                                                              │
IAuditProofStore (inclusion path per leaf, persisted)        IAuditAnchorStore.PublishAnchorAsync
                                                                       │
                                                        WORM / independent-credential target
                                                        (S3 Object Lock, Azure Immutable Blob,
                                                         Federation Raft log — see below)
```

- **Epoch = a batch/window.** An epoch seals on a cadence (per-batch or per-minute — `TailTruncationDetection.md`'s
  frequency/detection-window table). Sealing computes the MTH, mints an STH, stores each leaf's inclusion path, and
  publishes the STH to the anchor store.
- **`IAuditAnchorStore`** (from `TailTruncationDetection.md`): `PublishAnchorAsync(STH)` / `GetLatestAnchorAsync()`.
  Implementations: `FileSystemAnchorStore` (test), `S3ObjectLockAnchorStore` / `AzureImmutableBlobAnchorStore`
  (prod), and `FederationAnchorStore` (below). Trust model is the plan's: **write-once, append-only, independent
  credentials** — if the DB credentials can also delete anchors, the protection is illusory.
- **`IAuditProofStore`**: persists inclusion paths so a proof can be served without recomputing the tree.

## What this unblocks (the three deferred items)

### Tail truncation (`TailTruncationDetection.md`, finding #3) — resolved
- **Interim (ship first, no tree):** persist `(headHash, maxSequence, timestamp)` to `IAuditAnchorStore` on a
  schedule; `VerifyChainIntegrityAsync` compares `MAX(SequenceNumber)` against the anchor and reports truncation if
  current < anchored. This is Phase 0 and pays off before any Merkle work.
- **Long-term:** the STH pins `tree_size`. Truncation ⇒ current leaf count < anchored `tree_size`, detected on the
  next verification; a **consistency proof** from the last anchored STH to the current tree fails if history was
  rewritten. Truncation detection becomes inherent, not a bolt-on.

### GDPR anonymization re-chaining (`GdprAnonymizationReChaining.md`, finding #5) — design settled
Anonymizing a record changes its leaf ⇒ changes the root ⇒ **stale published anchors**. This design chooses, from
that plan's three options:
- **Chain level:** Option 1 (do **not** physically re-link) + the `AuditIntegritySupersession` record — unchanged
  from the GDPR plan; the supersession proves the modification was lawful.
- **Tree level:** **supersession-at-anchor** + **exclusion/replacement proof**. The original leaf stays provable in
  its original anchored tree (its inclusion proof against the old STH is preserved); a supersession record binds
  `oldLeaf → newLeaf` under signature; the anonymized leaf enters a **forward epoch** and is provable in the new
  STH. Published anchors are never retroactively edited (that would defeat WORM) — supersession is *forward*. A
  "separate anonymization tree" is rejected: it fragments verification and complicates monitors.
- Requires the `AuditIntegritySupersession` entity + migration from that plan; `SupersessionService` signs
  `(oldLeaf, newLeaf, type, ts)` and both the chain verifier and a Merkle monitor treat a valid supersession as a
  lawful modification, not tamper.

### Recursive anonymization (`RecursiveAnonymization.md`) — coordinated
Unblocked once the supersession/forward-epoch model above is fixed; the recursive JSON anonymizer just changes how
the new leaf is computed. No Merkle-specific work beyond leaf recomputation.

## BackgroundJobs integration

This pipeline is distributed by nature (multiple API replicas, scheduled sealing, external publication, and — at
Tier 2 — cross-institution anchoring). Several `MillWorks.BackgroundJobs` primitives map directly onto its
*correctness* requirements, not just its convenience:

1. **Epoch sealing as a leader-fenced recurring job.** Root sealing **must be single-writer per epoch** — two
   nodes sealing the same window fork the tree. BackgroundJobs' **leader election** + **recurring-job scheduling**
   give exactly-one sealing per epoch, and its **generation-fenced heartbeats + split-brain detection / node
   fencing** stop a zombie leader from sealing a competing root. This *replaces* the `sp_getapplock`
   serialize-every-write bottleneck: leaves accrue in parallel, only the per-epoch seal is coordinated.
2. **Anchor publication as a durable, retried, dead-lettered job.** Publishing an STH to an external WORM target is
   fallible I/O. BackgroundJobs' **durable retry + dead-letter queue** ensure a failed publish is never silently
   lost — a missed anchor is a security hole (it widens the truncation-detection window), so it must surface, not
   vanish. Alert on DLQ.
3. **Capability-aware placement (Fleet).** Sealing needs the audit DB; publishing needs *network reach to the
   anchor target*. Fleet **connection capabilities** route the publish job only to a node that can reach the WORM
   store / anchor endpoint, with the first-class **"no machine can run this"** state so a missing anchor route is
   loud rather than silently stuck.
4. **Scheduled verification.** A recurring job runs consistency proofs (last anchored STH → current tree) and
   spot inclusion proofs, extending AuditCore's existing scheduled chain verification to the Merkle layer.
5. **Federation (Tier 2) — the strongest fit.** BackgroundJobs Layer 2 (DotNext **Raft**, "aggregated results
   only, no raw data" across institutions under HIPAA/FERPA) is the textbook transparency-log deployment:
   - **`FederationAnchorStore`:** publish each institution's STH into the **Raft-replicated FederationDbContext**.
     Consensus gives a cross-institution, agreed, append-only anchor no single institution can rewrite — a stronger
     anchor than any single-party WORM store.
   - **Privacy-preserving cross-institution proofs:** institution A proves to B (or the federation authority) that
     "these shared aggregates derive from an audited, unaltered log" via an **inclusion + consistency proof against
     the anchored STH** — revealing only sibling hashes, **never the raw leaves**. Merkle proofs are structurally
     privacy-preserving, which is exactly what a privacy-law federation needs.
   - **Gossip monitor (split-view / equivocation detection):** a recurring federation job fetches peers' STHs and
     checks consistency, catching an institution that presents different logs to different peers — the CT
     monitor/auditor role, mapped onto the federation mesh.

## Phasing

| Phase | Scope | Deps |
|-------|-------|------|
| 0 | **Interim WORM head anchor** — `IAuditAnchorStore` + `(headHash, maxSequence, ts)` + verification compare. Ships value before any tree. | `IAuditAnchorStore`, one WORM impl |
| 1 | **Merkle primitive in `MillWorks.Cryptography`** — RFC-6962 MTH, domain-separated hashing, inclusion + consistency proofs, **KAT vectors** (RFC 6962 test trees). | Cryptography |
| 2 | **Epoch sealing + STH + proof store** (single-node) over `IAuditBatchProcessor` batches. | Phase 1, BatchPublishing |
| 3 | **Verification extension** — inclusion/consistency proofs into `TamperDetectionService`; STH-pinned truncation detection. | Phase 2 |
| 4 | **BackgroundJobs distribution** — leader-fenced sealing, DLQ'd anchor publish, Fleet routing, scheduled verify. | Phase 3, BackgroundJobs |
| 5 | **Federation anchoring + cross-institution proofs + gossip monitor.** | Phase 4, BackgroundJobs.Federation |
| 6 | **GDPR supersession-at-anchor** — `AuditIntegritySupersession`, forward-epoch anonymization, monitor treats valid supersession as lawful. | Phase 2, GDPR plan |

## Non-goals

- **Security events are excluded** — `SecurityEventIntegrity.md` already (correctly) declares Merkle batching a
  non-goal there: `SecurityEvents` are low-volume, per-event chaining is acceptable. This pipeline is for the
  high-volume `AuditEvents`/`AuditIntegrity` chain only.
- **No retroactive anchor editing.** Anonymization is handled forward (supersession), never by rewriting a
  published STH — that would defeat the WORM guarantee.
- **Not a blockchain.** External anchoring can *use* a public chain in high-assurance deployments, but the default
  targets are WORM object storage and the Raft federation log; no consensus token, no ledger currency.

## Dependencies / open decisions

- Batch/epoch cadence (per-batch vs per-minute) and its detection-window trade-off (`TailTruncationDetection.md`).
- STH signing key: reuse the integrity HMAC key management or a dedicated Merkle-signing key (recommend dedicated —
  it is published to external parties, unlike the internal HMAC).
- Anchor failure policy: fail-closed vs degrade when the anchor store is unavailable (`TailTruncationDetection.md`
  failure modes) — recommend degrade-with-loud-alert for publish, fail-closed for verification against a *missing
  expected* anchor.
- External anchor target selection per deployment (WORM object store vs Federation Raft vs both).

## Related documents

- `deferred/TailTruncationDetection.md` — finding #3; this design is its long-term fix (and Phase 0 is its interim).
- `deferred/GdprAnonymizationReChaining.md` — finding #5; supersession-at-anchor is settled here (Phase 6).
- `deferred/RecursiveAnonymization.md` — coordinated via the GDPR item.
- `completed/TamperDetectionIntegrityGaps.md` — origin (the deferred #3 head-anchor gap).
- `BatchPublishingRedesign.md` — the batch substrate epochs are sealed from.
- `SecurityEventIntegrity.md` — records the (correct) Merkle non-goal for security events.
