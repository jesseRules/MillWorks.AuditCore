# H1 ownership and failure recovery — 2026-10-16

**AuditCore implementation verified locally: 299 passed, zero failed, 19 expected provider-specific skips.**
318 cases were discovered. H1 has not been packaged, deployed, or qualified in a running host.
H2/H3a/H3 and the separate sequence-release runtime gates remain open.

[Exact command, source/assembly fingerprints and individual results](manifest.json),
[TRX](verification.trx), and [build/test log](build-and-test.log) retain the final run. The
19 skips are intentional fixture-specific cases: SQL fault/isolation/migration cases do not
run on SQLite, and the native SQLite timestamp case does not run on SQL Server. Both SQL
Server RCSI modes were exercised on localhost:1435, never application SQL on port 1433.
The source was a dirty working tree based on the recorded commit; unrelated concurrent
changes were preserved. This is source verification, not immutable-package provenance.

## Proven behavior

- Fresh per-acquisition tokens reject stale completion, retry and terminal-failure writes,
  including successive claims by the same instance. Expiry alone does not prevent the current
  token from completing. The actual drainer is tested while processing outlives the lease.
- Production claim/apply/recovery methods use independent scopes and untracked claims.
  Grouped outcomes report exact applied/rejected IDs and keep normal command counts bounded.
  Recovery is bounded and preserves attempts; database time governs SQL Server recovery.
- Real SQL lock barriers and DMV observations overlap completion/recovery. Exactly one
  transition wins. Tests permit configured SQL retry to change the winner, not the invariant.
- Fault injection immediately before and after COMMIT covers claim, grouped outcomes,
  failure/handoff insertion, source recovery, handoff claim/recovery and acknowledgement.
  Durable receipts prevent repeated mutation. An unavailable verifier exposes an unknown
  operation identity; later verification proves the retained result. Receipts remain useful
  after claim tokens are cleared and source state changes.
- Terminal failure plus handoff insertion is atomic. Missing DLQ and enqueue failure retain
  pending work. A crash after enqueue may redeliver the same stable handoff identity; stale
  acknowledgements are rejected. External delivery never marks the source failure resolved.
- All three built-in DLQs reject ordinary replay of the reserved projection event type, and
  the generic background processor excludes it. Routing metrics count successful external
  writes; SQL handoff-completion metrics describe confirmed acknowledgements separately.
- Queued JSON/version/kind/required-field validation precedes writers. Malformed work becomes
  a guarded retained failure. Cancellation does not create retry/terminal work.
- Authorization is host-supplied and defaults to denial. Cross-tenant, stale-cycle and expired
  permit requests cannot mutate state. Replay/skip retain actor/time/reason/cycle evidence,
  suppress pending/in-flight projections, and expose possible external effects already in flight.
  A replay that fails again creates a distinct failure cycle and projection.
- Unresolved source deletion is physically blocked by a restrictive FK. Opt-in cleanup checks
  authorization, resolution, projection state and the 24-hour verification window. Explicitly
  skipped and successfully replayed sources can be cleaned afterward. Cleanup also retains
  actor/time/action evidence linked to the original resolution. Resolution and operation
  metadata survive payload removal; tracked receipt modification uses the append-only guard.

## Schema verification

Two new owning-library migrations were generated, preserving all existing migration identities:

1. `20261016210852_AddOutboxOwnershipEvidence`: three nullable source columns and three new
   tables for operation receipts, failure projections and operator resolutions.
2. `20261016211640_RetainOutboxFailureSources`: restrictive, non-cascading projection/source FK.

[Incremental SQL](incremental-migration.sql) was generated and inspected for destructive DDL
or data deletion; neither is present. SQL tests migrate from the previous envelope-index
migration, retain an existing row, apply both new migrations, then claim/fail the row through
production operations. Fixture teardown drops its owned databases. The EF CLI emitted its
existing 10.0.12-tool/10.0.13-runtime version warning; generation and execution passed.

## Corrections during verification

Earlier development runs are not counted as passing evidence. The real-overlap test first
assumed the first transaction must win despite SQL execution-strategy retry; it now asserts
exactly one winner and the corresponding durable state. Receipt coordination was narrowed to
an operation-specific transaction-owned application lock to avoid unrelated GUID-range locks.
An existing host test stopped on InFlight rather than a terminal outcome; that observation
was corrected. The older SQL fixtures now dispose observers/providers so global metric listeners
do not collect stale gauges. A newly added DLQ-processor test required its statistics mock.
The retained final run follows those corrections; no implementation changes followed it.

## Rollout and operational limits

See the [implementation contract](../../../plans/BatchPublishingH1Implementation.md). No new
packages were created, no existing dev.6 package was overwritten, and no MillWorks API process,
settings, data, migration history or scan schedule was changed by this H1 task. This source
requires a fresh unused package identity at release time.

Before host rollout: apply the migrations, replace every old unguarded drainer, register the
host's current authorization/hold/disposition policy, and adapt terminal-outbox cleanup. The FK
rejects unsafe old cleanup; a rolling mixture of old writers does not establish H1 safety.

External writes already in flight cannot be retracted by SQL suppression. SQL cleanup does
not erase external DLQ copies or backups; copied-data disposition/recovery remains a host/
[disposition-plan](../../../plans/AuditDispositionRecovery.md) gate. Diagnostic projections
remain manual DLQ work and consume backend capacity until the host's approved lifecycle
handles them. Metadata receipts have no automatic pruning in H1. Volume/retention sizing,
prolonged outages, load qualification and external-backend reconciliation are not certified
by this focused run; H5 and host rollout retain those gates.
