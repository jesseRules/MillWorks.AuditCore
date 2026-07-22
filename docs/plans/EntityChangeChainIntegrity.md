# Entity-Change Trail: Chain It or Stop Calling It Tamper-Evident

**Status:** Proposed — decision required
**Date:** 2026-07-19
**Origin:** MillWorks consultant review 2026-07-19 — "The major guarantee mismatch"
**Priority:** P1 — accuracy of a security/compliance claim

## Problem

Two audit trails exist and only one is cryptographically protected:

- **Explicit events** (`IAuditLogger` → `AuditEventBatchWriter`) get
  `AuditIntegrity` rows: SHA-256 chain + HMAC over event hash, previous hash,
  sequence, and trusted timestamp (`TamperDetectionService.cs`), event and
  integrity record written in one transaction (`AuditLogger.cs`).
- **EF change capture** produces `AuditEnvelopeKind.EntityChange`, which
  `AuditEntityBatchWriter` writes **straight to `AuditLogEntity`**
  (`src/MillWorks.AuditCore.Services/Sinks/Writers/AuditEntityBatchWriter.cs:18`).
  It never calls the tamper-detection service. These rows are append-only *via the
  EF interceptor* but are **not** hash-chained.

The `audit.AuditLogs` trail — the one MillWorks surfaces in the frontend — is
therefore not tamper-evident, while README language and a MillWorks-side comment
imply the guarantee lives on the interceptor path. MillWorks'
`ComplianceAuditChainTests.cs:9-23` already documents the real behavior correctly
in its own XML doc; the marketing/README text is the outlier.

## The decision

Pick one and make code + docs agree:

### Option A — Document the boundary (cheapest, honest)
- Correct README and any inline comments to state plainly: `AuditLogs` =
  append-only change log; `AuditEvents` + `AuditIntegrity` = the tamper-evident
  chain, reached via `IAuditLogger` or `AuditDbContext`.
- Provide the documented bridge pattern: a consumer that needs chain-grade
  assurance for specific entity changes emits an explicit `IAuditLogger` event in
  addition to the interceptor row.
- **Cost:** docs + one worked example. **Assurance:** unchanged, but claims become
  true.

### Option B — Chain the entity-change envelopes
- Route `EntityChange` envelopes through the same integrity path so each gets an
  `AuditIntegrity` row.
- **Cost:** high. Interceptor currently favors throughput (batched writes,
  Immediate mode). Chaining requires strict per-envelope sequencing and the same
  `sp_getapplock` serialization the explicit path uses; this reshapes the batch
  writer's concurrency model and its throughput budget.
- **Assurance:** the visible trail becomes genuinely tamper-evident.

## Recommendation

**Option A now; Option B only if a compliance standard demands chain-grade
integrity for automatic entity change logs specifically.** Today's regulated
requirement (HIPAA retention, fail-closed on PHI writes) is met by the explicit
path plus append-only enforcement. Shipping honest docs closes the credibility gap
immediately; Option B is a scoped follow-up gated on a real requirement.

## Interaction with other plans

- Independent of `EntityChangeRedactionSeam.md` (P0) — redaction is orthogonal to
  chaining.
- Option B would compound the `sp_getapplock` contention already noted for the
  explicit path; sequence it after any batch-publishing redesign
  (`BatchPublishingRedesign.md`).
