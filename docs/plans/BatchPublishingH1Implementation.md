# H1 implementation decisions

This is the implementation contract for H1 in [Batch Publishing Hardening](BatchPublishingHardening.md).
It does not implement H2/H3a/H3, publish packages, or authorize a host deployment.

- `LeaseOwner` is a fresh acquisition token, derived from a new operation UUID, not an instance
  identifier. An expired but still-current token may finalize; conditional recovery invalidates it.
- SQL Server uses its database clock (`SYSDATETIMEOFFSET`) and one as-of timestamp per recovery pass. Portable relational
  providers use the injected TimeProvider. Selection is deterministic and capped at 128 rows.
  SQL claims use READ COMMITTED with UPDLOCK/READPAST/READCOMMITTEDLOCK, including RCSI ON.
- Every state operation opens an independent scope/context, suppresses ambient transactions,
  and executes its own transaction under the configured provider execution strategy. SQL locked
  readback plus guarded grouped updates produce exact applied/rejected IDs. Outbox entities
  never enter a writer's tracker. Pending rows are rechecked during claim updates.
- `AuditOutboxOperations` stores operation UUID, request hash, kind, exact result metadata,
  creation time and a 24-hour payload-verification window in the same transaction as mutations.
  A transaction-owned SQL application lock, scoped to the receipt table and operation UUID,
  serializes concurrent retries without locking unrelated GUID index ranges.
  Operation UUID and request stay stable across retries. Read the receipt on a fresh connection
  after an uncertain COMMIT; an unavailable verifier throws an exception carrying that UUID.
  A successful absence check permits the configured strategy to retry. Different input under
  an existing operation UUID is rejected. No-op polling creates no receipt.
- Receipt metadata is retained indefinitely in H1; automatic receipt pruning is not implemented.
  It contains no envelope JSON. Replayed claim results hydrate the unchanged source/projection
  payload; after authorized disposal, the receipt still proves commit, but cannot replay a
  disposed payload. Source payload deletion is blocked during verification windows.
- Failure changes the owned source to Failed and creates an external projection in one SQL
  transaction. FailureCycleId is that failure operation's UUID. Projection identity is derived
  from source row plus failure cycle; uniqueness is `(OutboxRowId, FailureCycleId)`. Each later
  terminal cycle gets its own projection. A restrictive FK prevents age-only deletion of the
  authoritative source while its projection remains. No cascade deletes are used.
- The existing drainer invokes a separately scoped projection dispatcher even on empty audit
  batches. Missing DLQ leaves Pending. A fresh dispatch token guards ack/recovery; bounded
  exponential backoff runs from 5 seconds to 1 hour without a destructive retry cap. A crash
  after enqueue may redeliver the same projection ID. A lost ack never republishes audit work.
- `AuditCore.OutboxFailureProjection.v1` is a reserved diagnostic event type. Built-in DLQs refuse
  ordinary event replay for this type. Custom DLQs must preserve this distinction. A successful
  external write increments `dlq_routed`; a conditional SQL acknowledgement increments
  `handoffs_completed`. Neither is an exactly-once external delivery guarantee.
- `IAuditOutboxRecovery` provides payload-free inspection and guarded replay/skip/cleanup.
  It requires a host-supplied `IAuditOutboxRecoveryAuthorization`; there is no permissive default.
  The host derives the actor and holds current authorization/disposition authority through commit.
  Nullable tenant identifies an explicitly authorized quarantine scope, never a wildcard.
  Mutations recheck that scope against source metadata and require the observed failure cycle.
- Operator replay resets attempts while preserving logical identity. Skip resolves a failure;
  neither action claims a successful audit write. Both record actor/time/reason/resolution ID,
  suppress pending/in-flight projections, and report potential external delivery already in flight.
  They cannot retract external effects. Re-failure creates a new cycle, not a reused Delivered flag.
- Cleanup is opt-in, individually authorized and limited to explicitly skipped or successfully replayed sources.
  Resolution, projection completion, and any relevant operation verification window must all be
  at least 24 hours old; pending/dispatching projections and live/unresolved source work block it.
  Cleanup records its own actor/time/action linked to the original resolution; resolution
  evidence and operation metadata survive deletion. This is not an out-of-band erasure
  or restore-replay ledger; [AuditDispositionRecovery](AuditDispositionRecovery.md) still owns that.

The shared Messaging recipes were reviewed. `ClaimTokenRecipe` requires its own Guid-token
entity/state contract; AuditCore's string token/status schema and immutable receipts are not
that contract. `ReadCommittedClaimTransaction` supplies a compatible isolation principle,
but AuditCore needs independent contexts, exact results and durable receipts across subsequent
state changes. `OutboxFailurePolicy` does not model retained failure authority or external
projection cycles. No shared processing engine or package dependency is introduced; production
operation tests provide equivalent claim/recovery/overlap conformance for SQL Server and SQLite.

Before rollout, migrate through the new owning-library migration, stop all old unguarded
workers, update the host's terminal-outbox retention path to use the recovery/disposition
boundary, and register its authorization policy. A rolling mixture is not H1-safe. The new
FK deliberately rejects unsafe old source cleanup rather than silently discarding failures.


## Verification and rollout status

[Retained H1 evidence](../evidence/audit-pipeline-2026-10/h1-ownership-2026-10-16/README.md)
records 299 passing tests, 19 expected provider-specific skips, exact commands/fingerprints,
real SQL overlap/fault tests and incremental migration execution. H1 is implemented in this
working tree and published as [dev.7 to both local feeds](../evidence/audit-pipeline-2026-10/release-dev7/README.md)
after full Release verification (2,536 passed, zero failed). It is not deployed to a host.
External DLQ copy handling and coordinated host
retention/authorization remain rollout gates. Metadata receipt retention is intentionally
conservative; volume qualification is H5.
