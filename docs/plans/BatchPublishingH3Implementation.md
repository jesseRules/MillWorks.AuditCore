# H3 — Exact internal publishing outcomes

**Status, 2026-10-16:** Implemented and verified: [2,442 broad and 155 focused passes](../evidence/audit-pipeline-2026-10/h3-outcomes-2026-10-16/README.md), zero failures.
H2/H3a/H3 are [published locally as dev.8](../evidence/audit-pipeline-2026-10/release-dev8/README.md)
from commit `6f560a1`; release verification passed 2,644 regressions and 416 final checks, zero failures.
Dev.9 subsequently passed the EF startup and focused speech/EF host-test gate;
[release evidence](../evidence/audit-pipeline-2026-10/release-dev9/README.md) records 78 new
rows/78 receipts and 54 passing host tests. Other producer/host rollout gates remain open.
H4 is partly delivered through H1's `OutboxStateStore`; its abstraction/test follow-up, H5
load qualification and deferred public sink result slice G remain separate.

**Post-review verification:** Both reproduced concurrency findings are resolved. The new SQL
cases changed from 10 failures/four passes to all 14 passing; wider verification records
[322 focused and 2,443 broad passes](../evidence/audit-pipeline-2026-10/h3-review-fixes-2026-10-16/README.md),
zero failures. The original counts above are the pre-review baseline.

## Result contract

`IAuditEnvelopeBatchLogger` retains its existing return type, but now requires
`BatchAuditResult.EventOutcomes`. Each distinct input ExplicitEventId must have exactly one
recognized `AuditEventOutcome`: Persisted, Duplicate or Failed. Failed outcomes carry their
exception and retryability. EventCount, Success, IsDuplicate and FailedEvents must agree with
that complete list. Legacy aggregate-only results are rejected; there is no production
adapter that guesses individual outcomes. Existing direct `IAuditLogger` callers may continue
using the aggregate result. This additive logger metadata does not implement G's proposed
public `IAuditSink` per-envelope return API.

Core AuditLogger records duplicates at its content-verification boundary and new events
only after commit. Those observations survive the initial batch attempt, duplicate recovery,
second bulk attempt and per-event fallback. The single-item path preserves the configured
strict/batched integrity behavior while returning its actual duplicate status. No successful
write is classified by querying existence after its own commit. H3a conflict/unverified
exceptions retain their payload-safe classification and correlated input identities.

A conflict discovered after earlier per-event commits is returned in the complete result as
Failed with a non-retryable AuditIdentityConflictException. Already confirmed Persisted and
Duplicate observations remain intact; fallback continues with remaining unresolved events.
The writer maps those typed failures to conflict outcomes rather than retrying and relabeling
already committed candidates. Pre-write identity conflicts may still throw when no earlier
new-event outcome needs preservation. Resilient logging returns complete mixed-conflict
results without retries/DLQ conversion and records their tracing outcome as failure.

The writer groups logical IDs, validates the complete logger result, and maps each logical
outcome back to every input EnvelopeId. Matching aliases are Duplicate only if that logical
candidate is successful; failed aliases remain failed. Conflicts cannot become duplicate
successes. Core and resilient loggers implement the complete contract. Custom capability
implementations must populate and validate exact metadata before they can be used here.
The explicit mock builders in tests construct fixture expectations and are not runtime adapters.

Resilient logging validates results before returning them and does not turn contract defects
into retries or DLQ failures. An escaping unique-key exception on an envelope attempt cannot
be converted to aggregate success. Infrastructure retry attempts use fresh contexts; if a
previous attempt committed before losing its acknowledgement, replay is classified Duplicate
on the successful retry. This measures observed final-attempt persistence, not a claim that an
ambiguous earlier attempt never wrote. After retry exhaustion, explicit Failed metadata means
unconfirmed outcome and does not assert absence; retained envelope content supports safe replay.

## Validation, transitions and escalation

Both immediate and processor paths require exactly one consistent writer outcome for each
input EnvelopeId. Missing, duplicate, unknown, null or contradictory results throw
`AuditBatchContractException`. Processor results likewise require one recognized status per
claimed row, consistent retry flags, and no failure details on successful outcomes.

The processor records outcome counters only after all writer groups validate. Malformed
results produce no completion/failure/retry outcomes and no result-derived outcome counters.
The drainer applies no result-derived transitions or attempt increments from a malformed
processor result. Claimed rows retain their leases and can be recovered; writes that committed
before the defective response remain durable and replay through H3a verification. Known invalid
queued-envelope terminal handling remains a separate path.

Each drainer logs contract defects at Critical severity and counts unsuccessful contract
observations across idle cycles. A valid nonempty processed batch resets that counter.
At `max(1, OutboxDrainerCircuitBreakerThreshold)` observations, the BackgroundService faults;
the host's configured BackgroundService exception policy handles escalation (normally host
stop). There is no silent infinite contract-error loop or result-derived terminal poisoning.
The bound is a count of observed defects, not a wall-clock SLA: reclaiming the same source
requires lease expiry and the configured recovery interval. Infrastructure failures retain
the existing circuit-breaker behavior. Operators must correct the logger/processor deployment
before restarting; retained source rows remain recoverable.

## Metrics and acceptance

Newly committed rows increment `audit.envelopes.published`; verified replays/aliases increment
`audit.envelopes.duplicate`. Conflicts and unverified legacy/missing-source identities increment
`audit.envelopes.failed` with `error_type=identity_conflict` or `identity_unverified`, distinct
from other failures. Transient write outcomes retain the retry-attempt counter. These are
processing-observation counters, not an exactly-once lifetime billing ledger.

Required evidence covers new-only, duplicate-only and mixed input; matching/conflicting
aliases; two-stage concurrent-insert fallback; failure/cancellation after partial commits and
safe replay; malformed logger/writer/processor contracts with no false transitions; metrics;
and bounded contract escalation. Real SQL mixed-batch races run with RCSI off/on. Commands,
results, skip reasons and source hashes are retained in the evidence register above. No schema
migration, package overwrite, host producer update or API restart is part of H3.

### Producer/host implementation follow-up — 2026-10-18

Dev.10 rebuilt-host acceptance is recorded separately from the earlier dev.9 EF acceptance.
The [implementation follow-up](../evidence/audit-pipeline-2026-10/rollout-implementation-2026-10-18/README.md)
closes the inventoried producer binding/retry-provenance and CommentThreads composition gaps
in source, integrates tenant/hold-authorized recovery and guarded retention, and implements
H4's internal interface/orchestration tests. A read-only application SQL check matches all
18 owning migrations and finds no queued rows/handoffs. Dev.11 delivery, API compilation and
runtime acceptance are tracked there; source changes are not live-host acceptance. H5 and
separate populated sequence/host-scan gates remain open.

### Dev.11 rebuilt-host acceptance — 2026-10-18

[No-build evidence](../evidence/audit-pipeline-2026-10/dev11-host-acceptance-2026-10-18/README.md)
records 227 focused passes, two breach assertions corrected in source pending Jesse's next
rebuild, and three owned-database atomicity skips. Five deployed AuditCore DLLs match dev.11.
Two live CRM observations reconcile to two distinct tenant-bound source events/receipts;
anonymous/user/admin recovery boundary checks pass. All 18 owning migrations match and no
queued rows/handoffs were present. Current API uses Immediate mode; this does not establish
enabled Outbox-drainer, remaining producer/consumer or external-copy acceptance. H4 is complete;
H5 and the separate sequence/host-scan gates remain open. No agent build or restart occurred.
