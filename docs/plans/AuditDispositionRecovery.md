# Audit disposition and recovery

**Status:** Design and implementation planning; implementation not started.
**Updated:** 2026-10-16.

AuditCore must execute authorized deletion and anonymization consistently and prevent SQL recovery, archive
access, or queued-event replay from resurrecting superseded personal data. MillWorks owns policy and legal-hold
decisions; AuditCore owns persistence and integrity semantics for its records. Moving the host's direct table
mutations behind an AuditCore disposition API is a prerequisite to that ownership split.

This plan supplies the implementation detail for
[recovery work package 8](../../../MillWorks/Plan/DisasterRecoveryAndRestoreQualification.md#8-recovery-disposition-implementation-and-qualification)
and the [DSAR copied-data boundary](../../../MillWorks/MillWorks.ClientPortal/Plans/20-GDPR-DSAR-Export.md#auditcore-retention-implementation-evidence-2026-09-24).
It does not activate retention, select retention periods, or qualify a deployment. MillWorks has no production
environment. Archive support exists in AuditCore, but the reference host does not currently call `UseArchival`.

## Implementation constraints

Apply the five hard constraints from the repository's existing plans when implementing a slice:

1. Name the exact files and helpers before implementation; do not create unnamed files or helpers.
2. Change only the named scope; do not perform adjacent cleanup.
3. List unresolved choices before editing code and obtain Jesse's decisions rather than silently selecting them.
4. Implement one checkbox at a time and build/test after each file change; leave incomplete boxes unchecked.
5. Treat ambiguity as a reason to clarify the specification before implementation.

This is a planning document, not authorization to implement unresolved designs. Before starting a checkbox,
record its exact file list, current satisfied/missing/blocked requirements, decisions, and validation commands.
Greenfield cleanup does not require compatibility shims for hypothetical consumers. Inventory concrete callers,
then migrate them together. Applied or committed EF migrations remain immutable; schema changes require new
generated migrations. Do not build `MillWorks.Api` or projects that build it transitively without explicit
permission naming the API, as required by [MillWorks AGENTS.md](../../../MillWorks/AGENTS.md).

## Current disposition paths

Source inspection: 2026-10-16. Repository HEADs were AuditCore `1cc9ce2c407b4f58c95ba8d25e67379ca14c9765`
and MillWorks `3794b03e1da4bb905fa5cfcb774dfb75361803f4`; both working trees may contain later uncommitted
changes. These identifiers describe inspection context, not a tested artifact or qualification result.

| Path | Current behavior and issue | Required disposition decision |
| --- | --- | --- |
| MillWorks `AuditRetentionSweepJob` | Owns policy/hold orchestration but directly updates `SecurityEvents.RelatedAuditEventId` and deletes `IntegrityWorkItems`, `AuditIntegrity`, `AuditEvents`, and other audit families through EF. Set-based operations bypass the change-tracker append-only guard. | Retain host authorization/orchestration; move sanctioned persistence execution into the defined AuditCore API, preserving permit lifetime and transaction semantics. |
| `AuditMaintenanceService.CleanupOldAuditEventsAsync` | Deletes events by age without tenant, legal-hold, or coordinated integrity handling. | Remove the independent destructive path or route all concrete callers through authorized disposition; do not merely add receipts to it. |
| `AuditComplianceService.ApplyRetentionPolicyAsync` | Direct event deletion does not coordinate integrity rows; archive-before-delete invokes the separate archival path. | Decide whether policy selection remains host-owned, then remove or reroute overlapping mutation paths. |
| `AuditArchivalService.ArchiveAuditEventsAsync` | Uploads an archive, then deletes events and integrity rows through repositories. | Separate archive creation from authorized disposition; define hold, integrity-work, security-pointer, receipt, and failure handling before enabling destructive archival. |
| `AuditComplianceService.AnonymizeUserDataAsync` | Updates hashed `User` and `JsonData` fields through repositories without the planned supersession mechanism. | Resolve the existing hash/append-only mutation problem through the approved integrity design before exposing replayable anonymization. |

Related sources: [maintenance](../../src/MillWorks.AuditCore.Services/AuditMaintenanceService.cs),
[compliance](../../src/MillWorks.AuditCore.Services/AuditComplianceService.cs),
[archival](../../src/MillWorks.AuditCore.Services/AuditArchivalService.cs), and
[host sweep](../../../MillWorks/MillWorks.Api/Infrastructure/Audit/AuditRetentionSweepJob.cs).
Receipts do not make an otherwise unauthorized deletion valid. Resolve all five paths before claiming unified
disposition coverage; validate other destructive callers found during the inventory as part of the same scope.

## Ownership and recovery assets

AuditCore owns record-family mutations, related-row handling, integrity preservation, and idempotent reapplication.
The host supplies current policy/hold authority, scheduling, protected storage, and startup coordination. AuditCore
must not depend on Identity, AI, or Api. Security owns operational security-event disposition decisions.

Inventory SQL events, change logs and subject links, security events, integrity rows/work, transactional outbox
envelopes, file/Redis/in-memory DLQs and fallback copies, archive blobs, and archive metadata. State whether each
copy is durable, backed up, reconstructible, or intentionally lost, and record its tenant/subject/resource identity,
hold authority, expiry, and recovery behavior. Distinguish unpublished envelopes from already persisted records.

Keep these protected recovery assets and contracts separate:

- Identity: `BlobIdentityErasureRecoveryLedger` and `IdentityRestorationReplayCoordinator`.
- AI ResponseAnalysis: `BlobResponseAnalysisErasureRecoveryLedger` and
  `ResponseAnalysisRestorationReplayCoordinator`.
- AuditCore: disposition receipt contract and storage implementation still to be designed.
- AuditCore archives: payload copies, not receipt ledgers or SQL backups.

`BackgroundJobModuleStartupHostedService` currently invokes Identity replay followed by ResponseAnalysis replay.
The target sequence preserves both, then runs AuditCore replay and any archive gate before readiness and side
effects open. Prove this ordering with host startup tests; a failed or unreachable required ledger blocks readiness.

## Publishing and recovery contracts reviewed 2026-10-16

[Batch Publishing Hardening](BatchPublishingHardening.md) H1 owns SQL claim/outcome commit
verification, transactional failure handoff, and retained failed-row authority. Its handoff
is an external DLQ delivery projection; projection retry must not replay the audit operation.
Authorized source replay/skip requires actor/time/reason evidence and disposition checks.
H3a rejects changed canonical content under a reused logical identity as a conflict; exact
scope and comparison evidence must be specified before implementation.

SQL transition markers prove commits within the current SQL history. They do not replace
this plan's independently protected disposition receipts, which must survive restoring older
SQL. Conversely, a disposition receipt does not prove an outbox worker owned its claim.
Keep these responsibilities and their tests separate.

Coordinate [terminal-outbox retention](../../../MillWorks/Plan/DataLifecycle/03-Audit-Retention-And-Volume.md#terminal-outbox-recovery-coordination-reviewed-2026-10-16)
with H1's retained failures, pending handoffs, operator evidence, and commit-verification
windows. Age or external DLQ delivery alone is insufficient cleanup authority. A reviewed
payload disposition must suppress affected queued and external copies before replay/read
can resurrect them; preserving recovery evidence does not authorize indefinite payload retention.

The supporting [Attendance contract](../../../MillWorks/Plan/AttendanceAndVisitorManagement/Artifacts/AC-08-Outbox-Dispatch-And-Recovery.md),
[Factory commit-fault evidence](../../../MillWorks/Plan/CodeReviewTightening/FactoryPublication-RecoveryEvidence.md#added-sql-fault-injection-coverage),
and [Messaging recipes](../../../MillWorks/MillWorks.Messaging/README.md) supply reusable
patterns, not AuditCore implementation evidence or a shared domain engine. Test both sides
of SQL commit with AuditCore-owned operation evidence and independently test the cross-store
SQL/disposition-ledger failure matrix.

## Ordered implementation slices

### 1. Authorized disposition boundary

- [ ] Inventory callers and decide which independent mutation paths survive. Record the exact replacement API,
  files, and caller migration before implementation. Specify the host sweep refactor to delegate sanctioned
  persistence through that API; preserve explicit tenant/resource identity, current authorization, and the permit through
  transaction completion. Preserve bounded batches, terminal-only work cleanup, oldest-prefix deletion,
  transactional security-event unlinking, missing/malformed-identity refusal, and denied-permit boundaries.
- [ ] Specify integrity semantics for lawful dispositions. Model the moved starting point after
  oldest-prefix deletion, including an entirely removed chain and later appends, and distinguish it from
  unauthorized prefix/interior/tail removal. Reconcile with
  [anonymization supersession](deferred/GdprAnonymizationReChaining.md) and
  [Merkle anchoring](MerkleAnchoringPipeline.md); do not rewrite published anchors or bless arbitrary hash changes.

### 2. Receipt and cross-store protocol

- [ ] Define minimal receipts containing stable IDs or a bounded range, action, authorization/policy revision,
  timestamps, recovery watermark, and operation identity. Exclude deleted payloads and reusable credentials.
  Define idempotent behavior for each record family and reject reuse of an operation ID for another recovery point.
- [ ] Record and approve the commit protocol and complete failure matrix before API implementation. Cover intent
  stored but SQL failed; SQL committed but completion storage failed; crashes between each step; partial batches;
  retries; changed hold/policy; absent identity; unreachable ledger; and reviewed replay-bound overflow. Ambiguous
  outcomes block readiness or reconcile against durable authority; they cannot silently complete or skip.
- [ ] Implement the approved disposition API and consolidate concrete callers, including the host sweep. Preserve
  authorization, transaction/permit lifetime, related-row cleanup, and integrity semantics defined in slice 1; use
  the approved receipt protocol rather than adding a second independent mutation path.
- [ ] Implement protected receipt storage and completion markers outside the SQL recovery boundary. Keep domain
  namespaces separate; use host-supplied or lower-level shared storage mechanics without cross-domain dependency.
  Set effective create/read/delete permissions, overwrite protection, monitoring, and retention extending beyond
  the oldest restorable SQL point and ordinary request idempotency windows. Conditional `IfNoneMatch` writes alone
  do not establish storage immutability. Prove receipts survive SQL rollback.

### 3. Queued copies and archive access

- [ ] Gate transactional outbox, DLQ and fallback replay against applicable dispositions before publication can
  reintroduce a removed subject's data. Classify ambiguous items as quarantined; prove permitted replay, suppression,
  cross-tenant isolation, retries, and restart behavior. Include H1 failure/handoff and operator-resolution state
  in that inventory; prove cleanup racing replay cannot remove unresolved recovery authority and that a payload
  disposition suppresses pending projections. Do not infer identity from arbitrary payload text.
- [ ] For a deployment that enables archival, require its approved purpose and retention policy, exact artifact
  identity, subject indexing, and hold/expiry rules. Gate archive reads/downloads as well as restore; a valid checksum
  cannot authorize disclosure of a superseded payload. Define filtered restore or successor archives where immutable
  blobs cannot be changed. Prove archive creation, checksum verification, metadata recovery, and disposition-aware
  read/restore behavior before enabling it. Archival remains optional for deployments that do not require it.

### 4. Keys and host startup

- [ ] Treat AuditCore integrity keys as a separate consistency boundary from Identity Data Protection. Inventory
  `ISigningKeyProvider` material needed by stored `HmacKeyId` and optional `DigitalSignatureKeyId` values. Rotation
  is compatible with recovery when historical verification material remains resolvable. Verify a pre-recovery
  integrity canary with matching keys; missing/incompatible historical keys fail qualification and readiness rather
  than being replaced and accepted. Include independent positive/negative Data Protection tests in the host drill.
- [ ] Extend host startup to preserve Identity → ResponseAnalysis → AuditCore replay → archive gate ordering after
  SQL and matching keys are available and before readiness, dispatchers, or side effects. Supply recovery point and
  operation identity through reviewed deployment configuration outside restored SQL. Verify each failure boundary
  and that no domain's completion marker substitutes for another's.

### 5. Isolated recovery proof

- [ ] Using synthetic data in at least two tenants, capture a SQL recovery point, apply Identity and ResponseAnalysis
  erasures plus authorized AuditCore dispositions, then restore into an isolated target with outbound effects disabled.
  Exercise the normal startup path and verify exact-tenant reapplication, retained integrity, moved prefix boundaries,
  independent completion markers, and outbox/DLQ suppression. Include archive reads/restores if archival is enabled.
- [ ] Restart with the same operation identity to prove idempotency, then change its recovery point to prove rejection.
  Exercise missing/unreadable ledgers, each replay failure, ambiguous commits, bound overflow, mismatched keys, and
  unauthorized data/hash changes. No ambiguous outcome may open readiness. Do not use or restart user-managed
  services or overwrite a live target.
- [ ] Retain recovery point, exact application/package/artifact identities, counts/checksums, storage-policy IDs,
  operation ID, outcomes, operator and reviewer without secrets or user payloads. Record unfilled second-operator
  qualification explicitly; do not invent an independent reviewer or mark self-review as independent execution.

## Decisions before code changes

The API/file inventory, fate of each existing disposition entry point, receipt schema/storage adapter, cross-store
commit ordering, prefix-boundary proof, anonymization supersession, changed-hold replay behavior, replay bounds,
archive read boundary, and deployment qualification version policy remain unresolved. Name each decision owner
and obtain the required design decisions before its implementation checkbox. Pin exact versions and hashes for
engineering evidence; whether a named deployment requires a stable release is a separate explicit decision.

Direct-subject discovery already exists through `IAuditSubjectLookupService`; it returns record locations only.
The reviewed Access disclosure projection and transitive domain resolution remain host/domain work under the
DSAR plan. Neither is implicitly implemented by this recovery plan.

## Completion criteria

Implementation requires the authorized AuditCore persistence boundary, migration of concrete destructive callers,
separate protected receipts, safe replay and queue/archive gates, and focused provider, crash-matrix, integrity,
key-canary, and host-ordering tests. Tests must prove both lawful modifications and rejection of unauthorized ones.
Deployment qualification additionally requires the named topology, effective storage controls, approved recovery
objectives, and an isolated drill with retained evidence. No boxes are complete based on historical test counts.
