# Audit disposition and recovery

**Status:** Design and implementation planning; implementation not started.
**Updated:** 2026-10-18.

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
Current H3a code rejects changed canonical content under a reused logical identity as a conflict.
`AuditEnvelopeIdentity` retains versioned fingerprints independently of payload cleanup;
`AuditIdentityLedger.StageAsync` and `VerifyDurableAsync` classify retained identity evidence with
a missing source as unverified. This is existing comparison behavior, not disposition support.
Lawful deletion and anonymization must define their interaction with that evidence before implementation.

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

## Code review additions — 2026-10-18

Read-only inspection against AuditCore HEAD `e45b3ac5578762f4ee750ad6ae8db2df2b7748b9`
identified four implementation gaps below. These are source findings, not passing test or deployment
evidence. The original 2026-10-16 inspection context above remains historical. All implementation
boxes remain unchecked. Paths below are relative to this repository unless prefixed `../MillWorks/`.

| Gap | Existing files/helpers to inspect and include in the implementation scope | Required result |
| --- | --- | --- |
| Retained comparison identities | `src/MillWorks.AuditCore.EntityFramework/Entities/AuditEnvelopeIdentity.cs`; `src/MillWorks.AuditCore.Services/Sinks/Identity/AuditIdentityLedger.cs` (`StageAsync`, `VerifyDurableAsync`); `src/MillWorks.AuditCore.Services/AuditLogger.Identity.cs`; `src/MillWorks.AuditCore.Services/Sinks/AuditOutboxWriter.IdentitySql.cs` | Define lawful suppression/supersession separately from duplicate, conflicting, and unverified input across event, entity, and outbox identity channels. |
| Already claimed payloads | `src/MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs` (`DrainBatchAsync`); `src/MillWorks.AuditCore.Services/Sinks/Recovery/OutboxHandoffDispatcher.cs` (`DispatchAsync`); `src/MillWorks.AuditCore.Services/Sinks/Recovery/OutboxStateStore.Resolutions.cs` (`ResolveAsync`, `CleanupAsync`); `src/MillWorks.AuditCore.Services/Models/DeadLetterQueueProcessor.cs` | Coordinate disposition with persistence and external delivery, including payloads already held in worker memory. |
| Archive identity round trip | `src/MillWorks.AuditCore.Abstractions/Dto/AuditEventDto.cs`; `src/MillWorks.AuditCore.Services/Mapping/AuditEventMappings.cs` (`ToDto`, `ToEntity`); `src/MillWorks.AuditCore.Services/AuditArchivalService.cs` (`ArchiveAuditEventsAsync`, `StreamRestoreAsync`) | Replace or extend the reviewed archive contract so restore preserves the identities and evidence required for disposition checks. |
| Independently running workers | Worker files listed in slice 4; `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs`; `src/MillWorks.AuditCore.AspNetCore/Extensions/ServiceCollectionExtensions.cs`; `../MillWorks/MillWorks.Api/Infrastructure/Startup/BackgroundJobModuleStartupHostedService.cs` | Inventory actual registrations and enforce an explicit recovery barrier at each affected worker, rather than relying on hosted-service registration order. |

Jesse owns approval of the unresolved choices added below. AuditCore implementation owns library
contracts, mutations, and focused tests; host implementation owns barrier wiring and startup tests.
Before editing a checkbox, narrow this inspection inventory to its exact changed files and helpers,
name any new contracts/migrations/test files, and record validation commands under the existing
implementation constraints. This table does not authorize changes to every listed file.

## Industry evidence and consumer contract — 2026-10-18

The following first-party documentation informs the product contract. It does not establish vendor
internal receipt protocols, worker barriers, or fingerprint designs, and it is not AuditCore test evidence.

| Reference | Documented behavior | Application to this plan |
| --- | --- | --- |
| [WorkOS Audit Logs](https://workos.com/docs/audit-logs/events) | Organization-scoped retention, permanent expiry deletion, and export/streaming to other stores. | Define tenant retention scope and distinguish managed records from exported copies. |
| [Twilio Segment deletion and suppression](https://www.twilio.com/docs/segment/privacy/user-deletion-and-suppression) | Asynchronous deletion and suppression are distinct operations; deleted data cannot be replayed; downstream coverage depends on destination support. | Expose operation status, prevent historical resurrection, and disclose destination coverage. |
| [Pangea Secure Audit Log settings](https://pangea.cloud/docs/audit/getting-started/settings) | Retention tiers and ingestion redaction; some redaction uses reversible encryption. | Minimize sensitive payloads before storage and distinguish reversible masking from erasure. |
| [Datadog archive rehydration](https://docs.datadoghq.com/logs/log_configuration/rehydrating/) | Archived logs can be searched or rehydrated from customer-owned storage. | Treat archives as usable payload copies requiring disposition-aware access and restore. |
| [ICO erasure guidance for backups](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/individual-rights/individual-rights/right-to-erasure/) | Backup handling depends on circumstances; retained backup data must be beyond use, with clear explanation to individuals and an established replacement schedule. | Host policy must state backup handling and expiry. Reapply dispositions before restored data becomes accessible; this recovery design is an engineering inference from the guidance. |
| [Azure immutable storage](https://learn.microsoft.com/en-us/azure/storage/blobs/immutable-storage-overview) | Retention locks and legal holds prevent modification/deletion while allowing reads. | Immutability alone does not authorize disclosure; retain independent archive read gates. |

Jesse's direction is to incorporate these recommendations into the plan. The following consumer
requirements and architectural direction are adopted for planning; concrete contracts, protocols,
file scopes, and deployment qualification still require the decisions and proof below. These are
product requirements, not findings from consumer research or a selection of legal retention periods.

| Consumer expectation | Required contract |
| --- | --- |
| Request acceptance is distinguishable from completion. | Return an operation identity and pending status after durable acceptance. Define pending, blocked, failed, and completed outcomes and an authorized status-query boundary. Never report completion solely because a job was enqueued. |
| Authorized historical data stays unavailable after completion. | Enforce the declared scope across persistence, retries, queues, archive access, and recovery. Report physical deletion separately from retained backup copies that remain inaccessible pending scheduled expiry. |
| Retrying a request is safe. | An identical operation is idempotent; conflicting reuse of its identity, scope, action, or recovery point is rejected. |
| Remaining copies are accounted for. | Report covered stores/destinations, outstanding work, held records, backup expiry conditions, and externally controlled exports. Do not imply universal deletion when only AuditCore-managed stores are covered. |
| Evidence exists without preserving the erased payload. | Retain minimal authorization, actor, time, tenant/scope, action, and outcome evidence under an explicit retention policy. Protect identifiers and fingerprints; absence of payload does not make evidence non-sensitive. |
| Tenant boundaries remain intact. | Scope mutation, status access, replay, and evidence lookup to the authorized tenant; test denial and isolation. |

Historical disposition suppresses the disposed records and their copies, not all future activity by the
subject. Subject-wide ongoing suppression requires separate host authorization, scope, and lifecycle.
Lifting that suppression must not reactivate disposed history. Prove that a new, authorized event can be
accepted after historical deletion while an old queued copy remains suppressed; event time alone cannot
establish whether input is new. The host owns the policy for future collection and lawful retention/holds.

The architectural direction is to preserve immutable comparison evidence with separate disposition
authority; use new linked identities for anonymization successors without weakening integrity checks;
coordinate active publication durably across instances; use a private, versioned archive contract; and
require an explicit recovery barrier at affected workers. The exact supersession proof, coordination
protocol, archive schema, and barrier interfaces remain implementation design work. External delivery
cannot be undone by SQL rollback: unresolved delivery or copy handling keeps the affected operation
pending/blocked and the applicable replay/read boundary closed.

## Ordered implementation slices

Design dependencies cross slice boundaries: approve slice 3's in-flight coordination protocol before
implementing slice 2's disposition API, and approve slice 4's barrier contract before adding startup or
worker gates. If archival is enabled, approve its identity schema before implementing archive disposition
or restore. Within those dependencies, implement one approved checkbox at a time.

### 1. Authorized disposition boundary

- [ ] Specify the consumer operation/status contract and copy-coverage manifest before API implementation.
  Name request/result/status types, files, helpers, and concrete host callers. Define durable acceptance,
  outcome transitions, retry behavior, tenant-authorized status access, per-store progress, hold/denial
  reporting, backup expiry conditions, and external-copy exclusions. Separate historical disposition from
  ongoing subject suppression and its removal. Name tests proving new authorized events remain possible,
  old copies stay suppressed after suppression is lifted, cross-tenant status access is denied, and partial
  or unreachable destinations cannot produce a misleading completed result. Include ingestion minimization
  and redaction boundaries in the inventory; reversible masking must not count as completed erasure.
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

- [ ] Define disposition-aware identity outcomes before implementing deletion or anonymization.
  Specify the authority binding disposition receipts to tenant, record family, logical identity/channel,
  and content version, including identities restored from older SQL. Decide how lawful deletion,
  anonymization successors, exact old-payload replay, changed-content identity reuse, and missing-source
  comparison evidence are classified. Preserve rejection of unauthorized changes; do not delete or
  rewrite fingerprints merely to make replay pass. Specify evidence retention and recovery behavior.
  Extend `tests/MillWorks.AuditCore.Tests/Identity/EnvelopeIdentitySqlServerTests.cs` and its
  `EnvelopeIdentitySqlServerTests.Mutations.cs` partial, plus the applicable
  `tests/MillWorks.AuditCore.Tests/Integration/EnvelopeIdentitySqliteTests.cs` cases. Prove deletion and
  anonymization followed by replay in each identity channel, cross-tenant refusal, unchanged duplicate
  behavior for retained records, and rejection of unauthorized content reuse.
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
- [ ] Approve the concurrency protocol for disposition versus already claimed/in-flight copies.
  A check before claiming or processing is insufficient: `DrainBatchAsync` holds envelopes before writing,
  and `OutboxHandoffDispatcher.DispatchAsync` checks ownership before an external DLQ write. Define the
  ordering guarantee through SQL persistence and external delivery, its coordination scope across host
  instances, and the handling of copies delivered before disposition completes. Decide whether completion
  waits for in-flight work or records unresolved work that keeps affected replay/read boundaries closed;
  do not acknowledge completed suppression while a worker can recreate or disclose the old payload.
  Cover immediate/in-process publication, DLQ retry, and fallback paths found by the caller inventory.
  Extend `tests/MillWorks.AuditCore.Tests/Sinks/OutboxOwnershipConcurrencyTests.cs`,
  `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/OutboxDrainerIntegrationTests.cs`, and
  `tests/MillWorks.AuditCore.Tests/DeadLetterQueue/DeadLetterQueueProcessorTests.cs` as applicable.
  Use deterministic pause points after authorization/ownership checks and before persistence/delivery:
  race disposition against the paused worker, resume it, and prove the approved ordering, restart safety,
  cross-tenant isolation, and preservation of unresolved recovery evidence.
- [ ] For a deployment that enables archival, require its approved purpose and retention policy, exact artifact
  identity, subject indexing, and hold/expiry rules. Gate archive reads/downloads as well as restore; a valid checksum
  cannot authorize disclosure of a superseded payload. Define filtered restore or successor archives where immutable
  blobs cannot be changed. Prove archive creation, checksum verification, metadata recovery, and disposition-aware
  read/restore behavior before enabling it. Archival remains optional for deployments that do not require it.
- [ ] Approve and implement an archive schema that preserves disposition identity on round trip.
  The reviewed `AuditEventDto`/`AuditEventMappings` path omitted identity fields. Subsequent working-tree
  changes inspected during this plan update introduce the private
  `src/MillWorks.AuditCore.Services/Mapping/AuditArchivedEvent.cs` transport and use it in
  `AuditArchivalService`, preserving tenant/user fields. Reconcile that work before editing; do not duplicate
  the contract or treat its presence as disposition qualification. Review the private contract against the
  full subject/resource/evidence inventory and prove its round trip. Do not infer identity from free-text
  payloads. Specify schema/version validation, required comparison and
  integrity evidence, and refusal/quarantine for missing or malformed identity. Extend
  `tests/MillWorks.AuditCore.Tests/Integration/ArchiveRecordIntegrationTests.cs` or name a dedicated round-trip
  test file before implementation. Prove exact tenant/subject/resource identity preservation, lookup after
  restore, disposition filtering/supersession, valid integrity, and rejection without partial committed restore
  when required identity/evidence is absent. Do not introduce compatibility machinery without concrete callers.

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
- [ ] Inventory worker registrations and approve an explicit recovery-completion barrier contract before
  startup implementation. Inspect `src/MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`,
  `src/MillWorks.AuditCore.Services/Models/DeadLetterQueueProcessor.cs`,
  `src/MillWorks.AuditCore.Services/Core/InProcessRequestAuditDispatcher.cs`,
  `src/MillWorks.AuditCore.Services/AuditMaintenanceBackgroundService.cs`,
  `src/MillWorks.AuditCore.Services/Core/ArchiveCreationBackgroundService.cs`,
  `src/MillWorks.AuditCore.Services/ArchiveVerificationBackgroundService.cs`,
  `src/MillWorks.AuditCore.Services/TamperDetection/IntegrityWriteBatcher.cs`, and
  `src/MillWorks.AuditCore.Services/TamperDetection/IntegrityReconciliationService.cs`.
  Record which enabled workers wait, which operations are needed by recovery itself, and why any worker
  can safely run before completion. Avoid a deadlock where replay waits for a worker blocked by replay.
  Keep the host dependency behind a library contract; name its file, registration, and affected test files
  before editing. Extend `../MillWorks/MillWorks.Api.Tests/Infrastructure/Startup/BackgroundJobModuleStartupHostedServiceTests.cs`
  and focused worker tests to prove that blocked/failed replay permits no affected writes or deliveries,
  cancellation releases no gate, successful replay opens workers only after the full sequence, and readiness
  bypass options cannot bypass recovery. Registration order alone is not a barrier.

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

The 2026-10-18 review additionally requires Jesse's decisions on disposition-aware comparison identity
outcome encoding and evidence retention, the concrete in-flight publication/delivery protocol, archive
schema/version enforcement, and the worker barrier contract including recovery-internal operations.
The consumer requirements and architectural direction above are adopted; these remaining implementation
choices must be resolved in their owning slices before dependent code changes. Adding requirements to
this plan does not mark implementation or deployment qualification complete.

Direct-subject discovery already exists through `IAuditSubjectLookupService`; it returns record locations only.
The reviewed Access disclosure projection and transitive domain resolution remain host/domain work under the
DSAR plan. Neither is implicitly implemented by this recovery plan.

## Completion criteria

Implementation requires the authorized AuditCore persistence boundary, migration of concrete destructive callers,
separate protected receipts, safe replay and queue/archive gates, and focused provider, crash-matrix, integrity,
key-canary, and host-ordering tests. Tests must prove both lawful modifications and rejection of unauthorized ones.
Completion also requires disposition-aware identity-channel outcomes, deterministic races with already claimed
payloads, explicit worker barriers, and archive identity round-trip proof when archival is enabled.
The consumer contract must also prove truthful asynchronous outcomes and copy coverage, tenant-authorized
status access, and separation of historical disposition from future collection/suppression policy.
Deployment qualification additionally requires the named topology, effective storage controls, approved recovery
objectives, and an isolated drill with retained evidence. No boxes are complete based on historical test counts.
