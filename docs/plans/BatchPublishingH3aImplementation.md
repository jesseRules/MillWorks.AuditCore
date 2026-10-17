# H3a — Logical event identity and immutable replay

**Status, 2026-10-16:** Library implementation and focused SQL verification complete:
[178 identity/ownership passes and 114 final identity passes](../evidence/audit-pipeline-2026-10/h3a-verification-2026-10-16/README.md),
zero failures. The broader local regression previously passed 2,420 cases. Host rollout remains
open. H2/H3a are uncommitted and unshipped; this is not release or host acceptance.

**Post-review correction:** The split receipt/source read race is fixed with a bounded evidence
recheck. [Retained review verification](../evidence/audit-pipeline-2026-10/h3-review-fixes-2026-10-16/README.md)
records 14 new SQL cases, 322 focused passes and 2,443 broad passes, zero failures. Historical
rows without receipts remain unverified; no version/schema change is required by these fixes.

## Identity and comparison contract

Jesse confirmed global EventId uniqueness, matching the existing database keys. An explicit
event requires a non-empty ExplicitEventId, persisted as EventId and used as the outbox key.
EnvelopeId remains per-input correlation. Entity-change identity remains EnvelopeId. No GUID
generator change is included: new GUIDs avoid accidental collisions, while retries must
reuse the original identity and semantic content. Repeated EnvelopeIds are rejected across
the entire batch before writes. The first input for a repeated explicit logical ID is the
candidate; identical aliases become duplicates only after that candidate is durably present.
Changed aliases are conflicts, and a failed candidate cannot make aliases successful.

`AuditEnvelopeContent.Version = 1` computes a SHA-256 digest of semantic input, with the
`AuditCore.EnvelopeContent.v1` domain. It includes kind, entity name/keys, action, actor,
correlation, IP/user agent, event type/description, payload, governance tenant/resource,
subject links and property changes. Explicit-event occurrence time is normalized to UTC.
Entity capture time is excluded because the interceptor reconstructs it on retries.
EnvelopeId, ExplicitEventId and transport/claim state are excluded; the logical ID is stored
separately. Comparison occurs before downstream logger redaction. Already masked/omitted
producer input cannot prove equality of original raw values that were never supplied.

JSON objects sort property names ordinally; arrays preserve order. Numbers use exact decimal
coefficient/exponent normalization, with no floating-point rounding. Strings retain their
ordinal meaning; arbitrary date-like strings and Unicode are not normalized. Duplicate JSON
property names and invalid Unicode scalars are rejected. Invalid JSON payloads compare as
opaque text, separately tagged from valid JSON. Null payload and JSON null differ. Subject
links form a distinct sorted set; property changes sort by unique property name.
Changes to this contract require a new comparison version and an explicit compatibility plan.
The independent integrity-chain canonicalizer is unchanged.

## Stored evidence and persistence boundaries

The incremental migration `20261016230118_AddAuditEnvelopeIdentities` creates
`AuditEnvelopeIdentities`, keyed by `(Channel, LogicalId)`, with kind, nullable tenant,
comparison version, digest and acceptance time. Channels `event`, `entity` and `outbox`
represent independently owned persistence boundaries; each identity is global within its
channel. The table stores no canonical payload. Existing migrations and stored EventIds
are unchanged. Historical rows receive no invented comparison evidence.

Receipts commit on the same context/transaction as accepted source writes. A matching
receipt plus existing source proves replay; changed content returns `audit_identity_conflict`.
When a source becomes visible after the initial receipt read, the ledger performs one bounded
receipt recheck for only those ambiguous IDs (chunks of at most 500). This handles a competing
atomic commit between reads at READ COMMITTED/RCSI without inventing historical evidence or
changing caller isolation. A source still lacking a receipt remains unverified.
Unknown comparison versions, historical source rows without receipts, or receipts whose
source was removed return `audit_identity_unverified`. Neither result exposes stored content,
tenant or fingerprint. Retain receipts indefinitely until coordinated disposition is designed;
there is no ledger cleanup API. Removing payload alone must not permit its recreation.
Entity fan-out repair requires matching original content. SQL Server uses a dedicated
SERIALIZABLE transaction across comparison and repair, under the configured execution strategy.

Mapped consumer outbox acceptance remains provisional until business SaveChanges/transaction
commit. Competing mapped inserts can require retry of the entire consumer transaction.
The unmapped SQL Server path uses parameterized, locked comparison and receipt/source inserts
inside the existing transaction, with a savepoint covering the entire call, including all
128-row chunks. It requires savepoints (MARS disabled); otherwise use mapped entities.
No distributed transaction is introduced. A consumer outbox can accept an event whose audit
store later reports conflicting content; drain then retains a guarded terminal source and
H1 handoff with the stable conflict/unverified reason.

Core/resilient loggers preserve SourceEnvelope through ordinary event dead-letter serialization
so replay can recompute comparison evidence and rebuild mapped fields. This adds another
payload copy governed by existing DLQ retention/disposition obligations. The lower-level
logger is an internal persistence capability for trusted producer/drainer paths, not an
untrusted public authorization boundary. Legacy direct logger calls cannot claim an ID
already owned by an envelope without its comparison context.

## Host authorization and extension contracts

Both sinks require host-provided `IAuditEnvelopeWriteAuthorization`. There is no permissive
production registration. Authority must derive from trusted actor/tenant/resource context,
including an explicit scope for absent tenant metadata, and remain current through sink
completion. The consumer host coordinates permission through its subsequent business commit.
The isolated test permit is not a production policy. Configure this before upgrade; a host
whose interceptor is configured to continue business writes after audit errors must not
mistake that behavior for successful audit publication.

Custom event loggers must implement `IAuditEnvelopeBatchLogger`; custom outbox writers must
implement `IAuditIdentityAwareOutboxWriter` and the atomic comparison contract. Legacy custom
implementations fail closed. The public fingerprint helper supports conformance; a marker
alone cannot establish an external implementation's correctness. Core and resilient loggers
implement the new capability. Exact per-event new/duplicate classification was deferred to H3 at this baseline; its
subsequent implementation is tracked in the [H3 contract](BatchPublishingH3Implementation.md).

## Rollout outcomes still required

- Library verification completed: SQLite/local regressions plus SQL concurrency, rollback,
  migration and entity repair/cleanup overlap coverage with both RCSI modes. Commands, output,
  the savepoint-limit defect/fix and final source fingerprints are retained in the evidence above.
- Inventory actual queued and persisted data before activation. Queue format is now version 2;
  version 1 is unsupported and receives H1's guarded terminal disposition. Establish a reviewed
  drain/migration disposition before enabling the new drainer. Do not reset data, rewrite IDs,
  or backfill fingerprints from redacted historical payloads without authoritative evidence.
- Update all host producers to supply stable ExplicitEventId and occurrence time across retries.
  The bounded [producer inventory](../evidence/audit-pipeline-2026-10/h3a-design-2026-10-16/producer-inventory.json)
  found ten missing IDs in explicitly typed MillWorks API initializers; target-typed/external
  producers remain a separate inventory gap. No host source has been changed by this work.
- Map the receipt entity in mapped consumer models and apply the receipt migration to every
  owning audit/consumer database. Verify schema names, host authorization, custom capability
  conformance, retention/holds and DLQ copies before activation. Rollback must preserve identity
  evidence/schema and avoid running an older key-only drainer against the new protocol.
- Publish only under a fresh unused package version after verification. Distributed dev.7 is
  immutable; current H2/H3a source is not included in it. No package publication, consumer
  update, API build/restart or deployment has been performed for H3a.
