# Batch Publishing Hardening

**Status:** H1 delivered in dev.7; reviewed H2/H3a/H3 changes are verified and published to both local feeds as dev.8. Host rollout pending; H4/H5 remain planned; G deferred.
**Date:** 2026-10-16
**Origin:** Review of [Batch Publishing Redesign](BatchPublishingRedesign.md)
**Scope:** Outbox ownership enforcement, sink contract accuracy, per-envelope outcomes,
internal repository extraction, and qualification of remaining performance claims.

## Evidence and boundaries

Slices A–F delivered stable identity, writers, row outcomes, leases, and telemetry. They do
not establish safe state transitions after a lease changes owners. The review reported 164
focused tests: 71 repository/SQLite/processor/lease/immediate-sink tests and 93
service/writer/outbox/metrics tests. These runs did not exercise overlapping workers through
lease expiry, the live SQL Server sequence query, or sustained batch throughput. Original
commands/results for the 164 count have not been recovered; it is not independently
reproducible acceptance evidence. See the [test evidence register](../evidence/audit-pipeline-2026-10/README.md#test-reproducibility).

The sequence aggregate implementation and envelope lookup index are separate completed
code changes. Their outstanding SQL Server/runtime/release acceptance is owned by
[Audit Write Pipeline Durability](completed/AuditWritePipelineDurability.md#remaining-acceptance-and-release-work--reviewed-2026-10-16).
Do not duplicate that checklist, repeat a completed deployment, or mark acceptance complete
from unit-test results. Preserve all unrelated working-tree changes during implementation.

## MillWorks precedents and implementation gates

These references provide existing design and test patterns, not AuditCore implementation
acceptance. Relative links assume the sibling MillWorks checkout. Keep AuditCore's tables,
payloads, transactions, and recovery semantics owned by AuditCore. Shared packages provide
stateless recipes/conformance probes; do not introduce a dependency on the MillWorks API host
or a generic outbox engine.

| Question | MillWorks reference | Application to this plan |
|----------|---------------------|--------------------------|
| Claims, retries, and bounded recovery | [Messaging consolidation](../../../MillWorks/MessagingConsolidation-Orchestration.md) and [Messaging recipes](../../../MillWorks/MillWorks.Messaging/README.md#ef-claim-token-recipe) | Evaluate existing utilities and conformance probes before copying mechanics. AuditCore retains eligibility, ordering, and state ownership. |
| Durable terminal failure | [Attendance AC-08](../../../MillWorks/Plan/AttendanceAndVisitorManagement/Artifacts/AC-08-Outbox-Dispatch-And-Recovery.md) | Retain owner-controlled dead-letter state with supported recovery; H1's transactional handoff is a separately tracked external projection. |
| Lost commit acknowledgement | [Factory Publication recovery evidence](../../../MillWorks/Plan/CodeReviewTightening/FactoryPublication-RecoveryEvidence.md#added-sql-fault-injection-coverage) | Verify durable operation receipts before replay; test faults immediately before and after SQL commit. |
| Same ID, changed content | [Factory Publication constraints](../../../MillWorks/Plan/CodeReviewTightening/FactoryPublication.md#design-decisions-and-constraints) and [Video Upload contract](../../../MillWorks/Plan/VideoUploadPhaseU0Hardening.md#111-idempotency-contract) | Jesse confirmed conflict rejection on 2026-10-16: same scoped identity and canonical content is replay; changed content conflicts. Define AuditCore scope and comparison evidence before implementation. |
| Invalid queued contracts | [Attendance AC-08](../../../MillWorks/Plan/AttendanceAndVisitorManagement/Artifacts/AC-08-Outbox-Dispatch-And-Recovery.md#implemented-behavior) | Validate after materialization and before writers; unsupported contracts receive a guarded durable terminal disposition. |
| Consumer atomicity | [Compliance Item10](../../../MillWorks/MillWorks.Compliance/Plans/Item10-AuditTransactionalOutbox.md) | Preserve business-write/outbox atomicity and rollback coverage. Its proposed host configuration is not evidence of current runtime activation. |
| Retention and recovery | [Audit Retention and Volume](../../../MillWorks/Plan/DataLifecycle/03-Audit-Retention-And-Volume.md) and [Audit disposition/recovery](AuditDispositionRecovery.md) | Coordinate cleanup of terminal rows, handoffs, queued envelopes, receipts, and copied payloads with current holds and disposition/recovery obligations. |

### Decisions to settle before the affected implementation

- H1 decisions and schema are recorded in the [implementation contract](BatchPublishingH1Implementation.md).
  Operation receipts, uncertain-commit verification, handoff cycles, authorization and retention
  are implemented and locally tested; host rollout gates remain open.
- H3a: conflict rejection is confirmed by Jesse on 2026-10-16. Define versioned canonical
  fingerprint fields, storage, normalization, and redaction/enrichment timing; exclude
  EnvelopeId and transport retry metadata. Do not retain a first-committed-wins branch that
  reports changed content as a duplicate.
- H3a: Jesse confirmed global EventId uniqueness on 2026-10-16, preserving existing keys.
  Canonical comparison, authorization and schema are specified in the
  [implementation contract](BatchPublishingH3aImplementation.md). SQL verification is complete; host rollout remains open.
- H3: choose the internal per-input outcome shape and enforce its cardinality independently
  of deferred public G, as required below.

## Delivery order

| Slice | Priority | Dependency | Status | Deliverable |
|-------|----------|------------|--------|-------------|
| H1 | High | None | Implemented in AuditCore; rollout gates open | Guarded transitions, retained failure authority, verified commits, and retryable DLQ projection |
| H2 | Medium | None | Verified and published locally in dev.8; host rollout pending | Accurate immediate/outbox batch contract and failure examples |
| H3a | Medium | None | SQL verified and published locally in dev.8; host rollout pending | One logical explicit-event identity across both sink modes |
| H3 | Medium | H3a | Verified and published locally in dev.8; host rollout pending | Accurate duplicate/new-event outcomes and metrics |
| H4 | Refactor | H1 | Planned; independently deferrable | Internal outbox repository with ownership-aware operations |
| H5 | Qualification | H1–H3 (including H3a); sequence acceptance tracked separately | Planned | Reproducible throughput, latency, memory, and reconciliation evidence |
| G | Optional API | H1–H3 (including H3a) and demonstrated consumer need | Deferred | Public per-envelope result contract, if justified |

H2 can ship independently; H3a defines identity before H3 builds result correlation. H4 is not a prerequisite for fixing H1: implement the
smallest safe ownership change first, or extract only the persistence seam needed to test it.
Keep existing public `IAuditSink` method signatures for these fixes. Apply the greenfield rule:
update producers, fixtures, and contracts together rather than retaining legacy identity
fallbacks or adding compatibility shims solely for old behavior. Track completion evidence
per slice; this document does not authorize or record a deployment.

## H1 — Enforce claim ownership at the database boundary

### Implementation result — 2026-10-16

[Evidence](../evidence/audit-pipeline-2026-10/h1-ownership-2026-10-16/README.md): **299 passed,
zero failed, 19 expected provider-specific skips** across 318 discovered cases. Both SQL Server
RCSI modes and SQLite exercise production state operations. The
[implementation contract](BatchPublishingH1Implementation.md) records concrete schema,
transaction/receipt, authorization, external projection and retention decisions.
Two new migrations were generated and applied to disposable SQL databases; no application
migration, package release or host deployment occurred during that H1 implementation run.
Subsequently, [dev.7 was published to both local feeds](../evidence/audit-pipeline-2026-10/release-dev7/README.md)
after full Release verification (2,536 passed, zero failed); host deployment remains open.
The checklist below records local
library acceptance, not deployed-host acceptance. Coordinated host cleanup/authorization,
external copied-data handling and replacement of all old drainers remain rollout gates.

### Problem

`AuditOutboxDrainer` claims rows with `LeaseOwner` and `LeaseExpiresAt`, then attaches them
and later calls `SaveChangesAsync`. `AuditOutboxEntity` has no concurrency token, and outcome
updates do not require the original claim to remain current. Recovery also loads expired
rows and saves them without a conditional ownership predicate. A worker paused beyond the
lease/leader-lock TTL can resume after another worker reclaims the row and overwrite that
worker's completion, retry, failure, or lease fields. The distributed lock is explicitly
best effort and cannot supply the missing guarantee.

### Implementation

1. Identify each acquisition with a fresh claim token, including successive acquisitions by
   the same drainer. Prefer storing a bounded per-claim value in the existing `LeaseOwner`
   column if compatible with its 100-character limit and diagnostics. If a separate token
   column is necessary, add an incremental migration and document deployment ordering.
2. Return immutable claim metadata to processing. Claim queries must return only the rows
   acquired by that claim, not every InFlight row owned by the instance. Keep SQL Server's
   atomic claim and the portable path's conditional Pending-to-InFlight update. Do not attach
   claimed outbox entities: use untracked immutable claim records and a dedicated outbox-state
   context/scope separate from the audit-write context. Recovery must also remain untracked.
3. Complete/retry/fail with conditional database updates requiring row ID, InFlight status,
   and the original claim token only. Do not require an unexpired lease to finalize: the
   current token remains authoritative until conditional recovery/reclaim invalidates it.
   Completion racing recovery must have exactly one winner. A long-running batch whose token
   still matches may complete, avoiding endless replay with the current 60-second lease and
   no renewal. Expiry remains a recovery predicate, not a finalization predicate; finalization
   does not compare application-server clocks.
   Batch compatible outcomes by claim and state/value assignment, with bounded ID lists and
   provider parameter limits. Avoid a round trip per row in the normal path. Retry timestamps,
   attempt counts, or error details may require separate groups or a provider-specific bulk
   update. Compare affected counts with expected counts. If they differ, obtain exact applied
   and rejected IDs through transactional observation or provider output; a later unprotected
   read alone cannot prove which transitions this call applied. Treat zero affected rows as
   lost ownership only after reconciling an uncertain commit. Allocate a stable operation ID
   outside execution-strategy retries and persist an immutable receipt of exact applied row
   IDs/dispositions atomically with each update group. On lost commit acknowledgement, verify
   that receipt before retrying or reporting ownership loss. An unverifiable result remains
   unknown/reconcilable, not fabricated success/failure; replay cannot duplicate handoff intents.
4. Recover expired rows conditionally against current InFlight/expiry/claim state so a stale
   recovery snapshot cannot reset a renewed or reclaimed row. Preserve AttemptCount. Document
   one time authority per provider, capture one as-of value per pass, and use deterministic
   ordering and bounded recovery batches. Clock skew may cause premature recovery but cannot
   authorize stale-token writes; include this distinction in tests.
5. Use a transactional failure-handoff table in the audit/outbox database. In one database
   transaction, conditionally change the current InFlight row to Failed and insert one durable
   handoff keyed uniquely by `(OutboxRowId, FailureCycleId)`, containing the original envelope, reason, and stable
   handoff ID. Commit both or neither; a rejected original claim must insert nothing. The
   original drainer never calls an external DLQ directly. Add this incremental schema change
   to H1 and stop old drainers before rollout. Dispatch follows the state machine below. The
   failed outbox row remains authoritative for the audit failure and operator resolution; the
   handoff table is separately retryable external delivery state, not a replacement audit event.
   A handoff retry must not republish the original audit operation.
6. Report applied/lost-claim outcomes accurately; increment recovery/terminal metrics for
   actual transitions. Specifically move `AuditMetrics.DlqRouted.Add(1)` after a confirmed
   successful external DLQ write: no increment when no DLQ is registered, enqueue fails, or
   the dispatcher has already lost ownership before dispatch. A successful external write
   followed by a rejected acknowledgement still counts as a successful delivery attempt;
   it is not a confirmed unique handoff completion. Attempt/failure counters, if needed, must have distinct meanings. Lease renewal, if added, must also check the claim token and must
   not replace ownership predicates. Route cancellation through cancellation handling rather
   than converting shutdown into new retry/terminal work.

### Commit verification and shared mechanics

Generate a stable operation/transition identity outside the execution-strategy callback. Run
claim and transition transactions through the configured EF execution strategy; SQL READPAST
claims require explicitly READ COMMITTED isolation. Verify uncertain commit outcomes against
AuditCore-owned durable transition evidence before retrying or reporting failure. Define the
marker schema and exact applied/rejected-row result contract before implementation; a claim
token later cleared by completion is not sufficient evidence of an earlier transition.
Persist result evidence with the guarded mutation so verification can distinguish committed,
rolled-back, and still-ambiguous outcomes even after later state changes. A matching row ID or
a later unprotected existence query is insufficient. Keep ambiguous outcomes recoverable;
do not run terminal side effects on an assumed commit. These SQL transition markers are
separate from the out-of-band disposition receipts needed to survive SQL backup rollback in
[Audit disposition and recovery](AuditDispositionRecovery.md).

Review `ClaimTokenRecipe`, `ReadCommittedClaimTransaction`, `OutboxFailurePolicy`, and
`MillWorks.Messaging.Testing` before choosing a dependency or persistence implementation.
Reuse compatible shared mechanics where the package/dependency boundary supports them;
record any mismatch and equivalent conformance coverage rather than claiming adoption.
Retain AuditCore's domain-owned states and original-token finalization semantics. Bounded
selection, eligibility rechecks, token-only untracked readback, zero-row ownership loss,
retry budget/backoff, and cancellation behavior must remain explicit. Do not introduce a
shared processing engine or make the independently deferrable H4 extraction a prerequisite.

### Validate queued contracts before processing

After deserialization and before any writer runs, validate the supported envelope version,
kind, required serialized identity fields, and kind-specific invariants. JSON parsing alone
is insufficient: missing IDs must not acquire constructor-generated replacements, undefined
kinds must not be acknowledged, and explicit-event identity must obey H3a when that contract
lands. Validate batch inputs before staging too. Retain malformed, unsupported, and invalid
queued envelopes as guarded terminal failures with the original protected payload, safe
reason code, and recovery/handoff state; do not silently complete them or expose raw payloads
in logs or operator list results. Version changes require updating producers and fixtures.

### Durable handoff state machine

| Current state / event | Required transaction or action | Durable result / recovery |
|-----------------------|--------------------------------|---------------------------|
| Current audit claim exhausts retries or rejects an invalid envelope | Guarded InFlight → Failed plus unique handoff insertion in one transaction | Failed row and Pending handoff both persist; crash before commit preserves the claim/recovery path |
| Pending handoff becomes eligible | Dispatcher conditionally claims it with fresh token and expiry | Dispatching; retain payload, stable handoff ID, attempts, next retry, and error metadata |
| No DLQ configured | Leave Pending with bounded retry/backoff and operational error reporting | Payload remains durable and inspectable; no routing metric or Delivered state |
| Dispatcher owns claim and enqueue succeeds | Pass stable handoff ID as replay identity where the DLQ supports it; conditionally acknowledge using dispatcher token | Delivered only when acknowledgement commits; record completion time |
| Enqueue fails or shutdown interrupts delivery | Guarded retry update, or expired-claim recovery if the process dies | Pending again with bounded backoff; do not discard on a handoff retry limit |
| Crash after enqueue but before acknowledgement | Recover expired Dispatching claim and enqueue again | At-least-once delivery; duplicates are possible and retain the same stable handoff ID |
| Stale dispatcher acknowledges after reclaim | Reject token mismatch | New dispatcher's state remains unchanged; external effects already in flight cannot be undone |

Dispatcher expiry recovery and acknowledgement race with conditional predicates just like
original claims. Retain the failed source row until an authorized recovery/disposition resolves
it; successful external delivery does not resolve the source failure or transfer its authority.
Do not cascade-delete pending/dispatching handoffs or purge them by age alone. Delivered
projections follow an explicit processed-retention policy, coordinated with source resolution,
commit-marker verification windows, and approved copied-data dispositions. An authorized
disposition that removes payloads must also prevent queued copies from resurrecting them.

Provide authorized, tenant-scoped inspection, replay, and skip operations. Retain actor, time,
reason, action, and a resolution identity; enforce conditional transitions against concurrent
dispatch, cleanup, and operator actions. Audit-event replay uses a fresh attempt budget and
claim token while preserving the logical identity and rechecking current authorization and
dispositions; retrying a DLQ projection must not reset or replay the audit failure. Skip means
an explicit resolved failure, never a fabricated successful audit write. Define how source
replay/skip invalidates or suppresses a pending projection and how in-flight external delivery
is reconciled; do not promise to retract an external side effect. Specify a terminal-failure
cycle identity for a replayed source that fails again: the unique source-row handoff must not
mistake an earlier Delivered marker for delivery of the new failure. Decide whether to
conditionally advance that projection or revise its uniqueness, preserving prior resolution
and delivery evidence. Include this choice in the schema/transition design before coding.
No implicit deletion/reset is allowed. Protect handoff content using the same audit storage/redaction rules. Record migrations, dispatcher registration, retry settings, and
retention integration as H1 deliverables. `dlq_routed` counts confirmed successful external
writes, potentially including redelivery; separate handoff-completed metrics count successful
conditional acknowledgements. Neither counter promises exactly-once routing.

### Acceptance

- [x] SQL faults before and after commit distinguish rollback/retry from receipt-verified
      success for claims, outcome groups, recovery, and handoff acknowledgements; no double
      attempt increment or duplicate intent follows an ambiguous acknowledgement.
- [x] Recovery uses bounded batches and the documented time authority; cleanup preserves
      receipts still needed for operation verification.
- [x] Validate deserialized version, kind, identity, and required fields before writers run.
      Unsupported/malformed queued envelopes retain a guarded terminal failure/handoff;
      no guessed identity, false acknowledgement, or endless deserialization retry loop.

- [x] Deterministic two-context tests pause worker A, expire/recover/reclaim with B, then
      resume A; A cannot overwrite B's state or clear B's lease.
- [x] An expired but otherwise current InFlight claim can complete; recovery winning first
      rejects that completion. A batch taking longer than LeaseDuration can still finish.
- [x] The same-instance reclaim case rejects the old claim, proving tokens change per claim.
- [x] A second portable claim by the same instance does not return the first claim's rows.
- [x] Neither outbox-state nor audit-write change trackers hold `AuditOutboxEntity` after
      claim, recovery, or outcome application; writer SaveChanges cannot flush outbox state.
- [x] Grouped completion/retry/failure tests verify bounded normal-path command counts,
      partial ownership loss, and exact applied/rejected IDs rather than counts alone.
- [x] DLQ metric tests cover no registered DLQ, enqueue failure, success, and lost ownership;
      only confirmed successful routing increments `audit.outbox.dlq_routed`.
- [x] Recovery racing completion/reclaim cannot revert current state; affected-row counts
      and metrics match actual transitions.
- [x] Retry exhaustion, invalid-envelope failure, cancellation, and DLQ handoff failure
      retain a durable disposition without allowing stale workers to finalize newer claims.
- [x] Failure transition plus handoff insertion is atomic, unique by source row/failure cycle, and rejects
      stale claims. Barrier/fault-injection tests cover before commit, after commit/before
      enqueue, after enqueue/before acknowledgement, and acknowledgement versus recovery.
- [x] Missing DLQ and repeated delivery failure leave an inspectable/retryable durable payload;
      retention cannot delete it. Restart recovers dispatch claims without replaying audit writes.
- [x] Exercise the real SQL Server claim/apply/recovery path against a disposable database,
      plus the supported portable path. Tests must call production operations rather than
      reproduce their logic in test code. Use explicit barriers instead of timing sleeps.
- [x] SQL Server fault injection immediately before commit proves rollback/retry; a fault
      immediately after commit proves durable-marker verification without a second mutation.
      Cover claim, grouped outcomes, failure/handoff insertion, and handoff acknowledgement;
      include later state changes and retry of the same operation identity.
- [x] Unsupported version/kind, missing serialized identities, malformed JSON, and invalid
      kind-specific payloads become retained failures without invoking writers or false success.
- [x] Authorized replay/skip records actor/time/reason and rejects unauthorized/cross-tenant
      actions; race operator resolution with dispatch and cleanup. DLQ retry never republishes
      audit writes, and successful DLQ projection alone does not resolve the source failure.
      Terminal → authorized replay → terminal again preserves both failure cycles and cannot
      reuse an old Delivered marker as a new handoff acknowledgement.
- [x] Cleanup preserves unresolved failed rows, pending/dispatching projections, live claims,
      and markers required for uncertain-commit verification. Test authorized expiry/resolution
      and disposition-driven suppression of copies separately from age-only cleanup.
- [x] Replay after audit persistence but before completion remains idempotent. Repeated
      processing is allowed; duplicate durable audit records and stale state updates are not.

## H2 — Correct public batch acceptance and failure documentation

### Implementation result — 2026-10-16

[H2 evidence](../evidence/audit-pipeline-2026-10/h2-acceptance-2026-10-16/README.md):
**62 focused tests passed** and **2,550 full Release regression tests passed**, zero failures.
Both sinks reject null entries before writer calls. Public/implementation XML and README
now describe partial commits, returned failure IDs, interrupted calls, stable-identity retry,
and acceptance inside the consumer transaction. No public signature, transaction or schema
change; published locally in dev.8, with host rollout pending. Published dev.7 remains unchanged. The following records
H2's implemented requirements and local acceptance.

The former `IAuditSink.PublishBatchAsync` contract promised one transaction and no partial
success. XML and README now describe the independent immediate writers, returned
`AuditWriteException.FailedEnvelopeIds`, unchanged identities on retry, and the possibility
of committed work without complete feedback after a writer exception or cancellation.
Null batch entries now raise an argument exception before either writer or outbox staging.

SQLite regressions exercise entity replay/new batches, whole-attempt duplicate retries and
exhaustion without fresh-row commits. They force the actual explicit-event logger through
both duplicate catches into per-event fallback, then verify a later write failure or
cancellation leaves the earlier event committed. Mixed-kind tests verify both returned
failure directions and exception propagation after an earlier commit.

Outbox acceptance is tested before consumer SaveChanges and followed through save and
rollback: successful publication does not commit the consumer transaction or make the final
audit record queryable. No cross-writer transaction was introduced.

- [x] A mixed-kind regression test makes one writer succeed and the other fail, verifies the
      reported failed IDs, and confirms the successful subset is not described as rolled back.
- [x] Same-kind regressions cover entity replay/new outcomes and atomic failed attempts,
      plus explicit-event per-event fallback where an earlier write persists before failure.
- [x] Null at the start/middle/end throws before any persistence in either sink; valid inputs
      retain relative order within each writer and original ID correlation; empty batches remain supported.
- [x] Document writer exceptions/cancellation separately from returned failure outcomes;
      avoid promising complete per-envelope feedback on an interrupted call.
- [x] Public docs, sink docs, and plan examples agree about immediate and outbox acceptance.

## H3a — Unify explicit-event persistence identity

### Implementation discovery — 2026-10-16

[Source discovery and producer inventory](../evidence/audit-pipeline-2026-10/h3a-design-2026-10-16/README.md)
confirm globally unique current keys, key-only duplicate handling at multiple persistence
boundaries, and ten explicitly typed MillWorks API producers missing ExplicitEventId in the
bounded scan. Global scope is confirmed. The [implementation contract](BatchPublishingH3aImplementation.md)
records the source changes and host gates. [Retained verification](../evidence/audit-pipeline-2026-10/h3a-verification-2026-10-16/README.md)
records 178 identity/ownership passes and 114 final identity passes, zero failures. Library
verification is complete; host producer/data inventory, package release and activation remain open.


The outbox deduplicates explicit events by `ExplicitEventId`, but `AuditEventBatchWriter`
previously persisted `EventId = EnvelopeId`. The previous immediate path could store one logical explicit event twice when re-enveloped
with distinct EnvelopeIds. The implemented mapper now preserves the caller's required
ExplicitEventId; H3 will build exact outcome reporting on that identity.

For explicit events, require a non-null, non-empty `ExplicitEventId` and persist that value as
`AuditEvent.EventId`; use the same value as the outbox idempotency key. EnvelopeId remains the
per-envelope result correlation identity. Validate before writes, update every producer,
serialization fixture, and manual-envelope example, and remove the backwards-compatibility
fallback to EnvelopeId. Entity-change envelopes continue to use EnvelopeId for idempotency.
Do not silently rewrite existing persisted EventIds. Inventory local queued envelopes and
persisted records before rollout; handle incompatible data through a reviewed drain/migration disposition. No reset, deletion
or EventId rewrite is authorized by this work.

### Conflicting reuse of an explicit-event identity

**Decision confirmed by Jesse, 2026-10-16:** reject changed content under the same identity
as a conflict. This replaces the earlier first-committed-payload-wins duplicate policy.
Same scoped logical identity plus identical canonical content is replay; changed content is
not Duplicate and must not overwrite the accepted event. Apply the rule in immediate and
outbox modes, within a batch, across concurrent requests, and after drain. Return a stable,
payload-safe conflict classification; a corrected/different event needs a new logical ID.

The [implementation contract](BatchPublishingH3aImplementation.md) defines canonical comparison
version 1, immutable semantic fields, global scope, stored receipts and indefinite retention
until coordinated disposition exists. SQL races and entity repair/cleanup overlap are now verified with RCSI off/on. An
explicit event re-enveloped under a different EnvelopeId must still compare equal when its
logical content matches; transport/claim metadata is not logical event content. Include the
relevant tenant, actor, governance/subject metadata and event payload in the contract rather
than comparing arbitrary serialized JSON byte order. Apply current authorization before
returning replay/conflict details, and never expose another tenant's content.

Reconcile logical scope with the current globally unique EventId and outbox idempotency key;
do not silently narrow uniqueness to tenant/user because Video uses that shape. Decide any
schema and producer changes explicitly. Entity-change replay needs a separately specified
immutable envelope/property comparison that preserves repair of missing persisted fan-out
rows without accepting changed original content. Existing key-only deduplication does not
prove content equality. Inventory queued/persisted rows lacking comparison evidence and
handle local data explicitly rather than asserting that their payloads matched.

Validate all input identities before any persistence. Reject repeated EnvelopeIds within a
single batch as an argument error, before either writer runs or outbox rows are staged.
For one ExplicitEventId repeated under different EnvelopeIds, choose the first input as the
within-batch candidate; later inputs are duplicate references only when content matches and
that identity is confirmed durably present. Conflicting inputs receive the defined conflict
result and never become duplicate successes. If the candidate fails without any persisted
winner, none of those references can be reported as successful. Across concurrent submissions, the unique persisted
identity determines the winner. Preserve every EnvelopeId in outcome correlation.

- [x] One ExplicitEventId with different EnvelopeIds produces one durable event in immediate
      mode and one logical outbox entry in outbox mode; replay after drain remains idempotent.
- [x] Stored EventId equals the caller's ExplicitEventId; round-trip serialization preserves
      both IDs; new logical EventIds remain distinct even when event content matches.
- [x] Missing/empty ExplicitEventId is rejected before persistence in both modes; repository
      fixtures supply it. Entity identity and its comparison/repair contract are specified.
- [ ] Before host activation, update all host producers (including the ten known missing-ID
      initializers), inventory target-typed/external producers and queued/persisted data, wire
      trusted authorization and consumer receipt mapping, and apply owning migrations.
      Preserve retry identity/time; no reset or historical proof backfill is authorized.
- [x] Mixed/repeated logical IDs within one batch map deterministic per-input outcomes back
      to EnvelopeId without assuming one EventId maps to only one envelope. Duplicate
      EnvelopeIds are rejected before writes under the policy above, rather than causing a dictionary exception.

- [x] Identical canonical content under one scoped ExplicitEventId is replay; different
      content is a stable conflict in same-batch, persisted, queued, post-drain, and concurrent
      cases in both modes. Stored content remains unchanged; a failed/conflicting candidate
      cannot make later references false successes. Cover differently ordered JSON properties,
      distinct EnvelopeIds, changed tenant/actor/payload, and authorization before disclosure.
- [x] Identity scope, canonical comparison version/fields, stored evidence, schema, and
      cleanup window are specified and exercised under real uniqueness races. Entity-change
      partial replay repair remains supported without permitting conflicting original values.
- [x] Repeated EnvelopeIds are rejected before writes; distinct EnvelopeIds sharing a logical
      event receive correlated outcomes. Public examples require a new logical ID for new content.

## H3 — Preserve duplicate classification within mixed event batches

**Implementation:** [H3 contract](BatchPublishingH3Implementation.md). Exact per-event metadata,
validation, metrics and bounded contract-defect escalation are implemented and verified:
[2,442 broad and 155 focused passes](../evidence/audit-pipeline-2026-10/h3-outcomes-2026-10-16/README.md), zero failures. The library is now published locally in dev.8; host deployment remains pending.

The prior logger persisted mixed replay/new batches safely but returned aggregate success,
causing existing events to be counted as new. The new EventOutcomes metadata preserves the
observed persistence result through each recovery path and maps it back to EnvelopeId.

Add deterministic per-event outcome information on the internal logger/writer path, keyed by
persisted EventId (the ExplicitEventId established by H3a) and mapped back to each input
EnvelopeId. Choose result metadata or an internal persistence seam that exposes exact outcomes;
update in-repository logger implementations and tests together. Do not add an aggregate-only
compatibility branch that fabricates per-event precision. Do not infer duplicates with a post-write existence query, since all
successful writes exist by then. Preserve classifications through the concurrent-insert
fallback. Any logger used by this internal path must supply the required outcome contract;
record and update custom implementations affected by that contract.

- [x] New-only, duplicate-only, mixed replay/new, and concurrent-insert fallback tests verify
      persistence and exact internal per-envelope classifications.
- [x] Processor metrics distinguish newly published, content-verified duplicate, conflict,
      and failed envelopes in mixed batches; key existence alone is not verified replay.
- [x] Partial failures and replay after an interrupted fallback do not lose events or label
      an unpersisted new event as successful.
- [x] Callers, implementations, and fixtures use the chosen outcome contract; no legacy
      aggregate-only branch invents individual duplicate statuses.

### Internal result validation

Validate the entire result before applying transitions: exactly one recognized outcome per
input/claimed row, no unknown IDs, and no duplicate or contradictory outcomes. Malformed
results are a processor contract defect, not evidence that a particular audit write failed.
Apply no result-derived completion or terminal transitions from that malformed result; keep
claims recoverable, report the defect, and rely on idempotency if writes already committed.
Define bounded operational escalation for a persistently broken processor rather than silently
looping forever. Known invalid-envelope terminal outcomes remain a separate path.

- [x] Missing, duplicate, contradictory, and unknown outcomes cannot falsely complete/fail
      rows or increment attempts twice; committed audit writes survive recoverable replay.
- [x] These checks run on the internal processor/writer contracts even when G is deferred.

### H3a/H3 review corrections — 2026-10-16

Both code-review findings are resolved: concurrent commits between receipt/source reads no
longer become false unverified failures, and late fallback conflicts preserve earlier new
classifications. [Before/after proof and regression evidence](../evidence/audit-pipeline-2026-10/h3-review-fixes-2026-10-16/README.md)
records 10 failing new SQL cases before correction, all 14 passing afterward, 322 focused
passes and 2,443 broad passes, zero final failures. Genuine missing evidence remains rejected.
H2/H3a/H3 are now published locally as dev.8; host rollout and H4/H5 remain separate.

## H4 — Extract ownership-aware outbox persistence

Move claim, guarded complete/retry/fail, and recovery operations behind an internal repository
registered with the appropriate DbContext lifetime. Pass claim metadata to every mutation;
row-ID-only APIs from the original sketch are insufficient. Keep batch writing/outcome
classification in `IAuditBatchProcessor`, and scheduling, deserialization, and orchestration
in the drainer. Decide separately whether queue-depth sampling belongs in the repository.

- [ ] Preserve H1 provider tests and H2/H3 behavior across extraction.
- [ ] Drainer orchestration can be tested through the repository seam; repository tests use
      real database transitions and prove stale-claim rejection.
- [ ] No public API change; no line-count gate. The original ~100-line target is aspirational.

## H5 — Qualify performance claims

The original 10K envelopes/sec and P99 <100ms targets are unverified. A default 250ms polling
interval makes a blanket sub-100ms end-to-end P99 claim unsuitable without different settings.
Create a repeatable workload with recorded SQL Server version, hardware/resources, starting
row count, payload distribution, envelope mix, producers/drainers, indexes, poll interval,
lease/recovery durations, and tamper-detection configuration. Record configured LeaseDuration
against the slowest observed batch and its safety margin. Tune leases/renewal to reduce duplicate
work and keep recovery timely. Correctness must still hold when a batch outlasts its lease:
claim-token predicates and idempotent writes provide safety; lease duration alone does not.

- [ ] Define workload-specific throughput, latency, backlog, and memory budgets before
      declaring a pass; distinguish default settings from tuned settings.
- [ ] Record at least 30 minutes of offered and committed throughput, acceptance-to-audit
      persistence P50/P95/P99, queue depth, retries, duplicate counts, and memory trend.
- [ ] Include a batch exceeding LeaseDuration, with and without recovery winning the race;
      prove eventual progress and no endless replay.
- [ ] Include worker interruption and recovery; reconcile every offered identity to a durable
      audit record, durable pending/retry row, or durable failure disposition. Explain rejection
      and duplicate counts. Shutdown backlog is not committed throughput.
- [ ] Save commands, settings, results, and failures in a repository evidence file; update
      the original plan's success claims only to what those results support.

Sequence-query SQL Server correctness, running-API query capture, repeated scans, and release
provenance remain in the linked durability acceptance checklist, with their own closure gate.

## Optional G — Public results only when needed

Keep G deferred until a named consumer needs visibility that existing exceptions, internal
outcomes, and metrics cannot supply. Before implementation, record the use case and choose a
result-returning contract after inventorying in-repository `IAuditSink` implementations and
consumers. Apply the greenfield rule: update those consumers together; do not add a legacy
adapter/shim without a demonstrated requirement. Record any public signature change explicitly.

Required contract decisions and tests:

- [ ] Exactly one correlated outcome for each accepted input; define repeated EnvelopeIds,
      null entries, unsupported kinds, order, and empty batches.
- [ ] Duplicate is successful idempotent acceptance. `AllSucceeded` includes Succeeded and
      Duplicate; expose separate new-success, duplicate, and failed counts.
- [ ] Outbox outcomes describe acceptance within the consumer transaction, including the
      possibility of later rollback; they do not promise final persistence.
- [ ] Define expected failures represented as outcomes versus argument/infrastructure
      exceptions and cancellation. Do not report unattempted work as persisted.
- [ ] Outbox writer metadata distinguishes individual inserts from duplicates; its current
      aggregate insert count cannot identify which envelope was duplicate.
- [ ] Keep drainer row correlation internal (`BatchProcessingResult`/RowId); public sink
      results do not contain outbox row IDs or replace the processor contract.
- [ ] Consumer migration/adapter examples and contract tests cover immediate partial success,
      outbox rollback, duplicates, cancellation, and existing custom implementations.

## Planning decisions before implementation

The external records settle the direction of retained failure authority, separate retryable
DLQ delivery, content-conflict rejection, and durable verification of uncertain commits.
Before coding, record the exact files/migrations, SQL transition-marker/result schema,
canonical fingerprint and identity scope, replay/skip authorization contract, projection
suppression races, retry bounds/settings, shared-package adoption decision, and cleanup
windows. H1 choices are now implemented and recorded in its implementation contract/evidence. H3a/H3
canonical identity and outcome-contract choices remain design work for those slices.
Coordinate [H1 retention](../../../MillWorks/Plan/DataLifecycle/03-Audit-Retention-And-Volume.md#terminal-outbox-recovery-coordination-reviewed-2026-10-16)
and [Item10 consumer proof](../../../MillWorks/MillWorks.Compliance/Plans/Item10-AuditTransactionalOutbox.md#auditcore-hardening-dependencies-reviewed-2026-10-16)
before closing the relevant slices.

## Release and rollback

H3a's pre-rollout queued/persisted-data inventory is a release gate. Record the exact affected
identities and a reviewed reconciliation procedure before switching stored EventId semantics.
Any destructive greenfield reset requires explicit authorization; planning does not authorize
one. Coordinate failure-handoff/receipt retention with the linked disposition and host sweep
plans so cleanup cannot invalidate pending delivery or uncertain-commit reconciliation.


Ship H1–H3 (including H3a) independently of G. Record migrations, package identities, and consumer resolution
for each release. H1's safety guarantee requires every active drainer to use guarded transitions;
a rolling mixture containing old unguarded drainers does not establish that guarantee. Stop
old drainers before relying on the new behavior. If H1 adds schema, migrate before starting
new drainers and retain that schema on rollback. Reverting to unguarded code reintroduces the
ownership defect. Never run a pre-lease enum mapping against current outbox status values
without an explicit state conversion and a stopped-drainer deployment procedure.

## Closeout

Mark H1–H3 (including H3a) complete only with their implementation and regression evidence. Track H4/H5
independently if deferred, and leave G explicitly deferred unless its consumer gate is met.
Synchronize the redesign status and the linked durability acceptance record without rewriting
historical results or equating package delivery with runtime acceptance.

## Local release closeout — dev.8

[Release evidence](../evidence/audit-pipeline-2026-10/release-dev8/README.md) records source commit
`6f560a1`, 2,644 SQL-enabled regression passes, 416 final committed-source passes, package-only
consumer validation and hash-verified delivery of all five packages/symbols to both local
feeds. No failures; 679 existing feed artifacts remained unchanged. This closes local package
delivery for H2/H3a/H3. Host producer/authorization/receipt/queue-data preparation and activation
remain open, as do H4/H5 and optional G. No API build/restart or runtime acceptance occurred.
