# H3a — Logical event identity and immutable replay

**Status, 2026-10-16:** Library implementation and focused SQL verification complete:
[178 identity/ownership passes and 114 final identity passes](../evidence/audit-pipeline-2026-10/h3a-verification-2026-10-16/README.md),
zero failures. The broader local regression previously passed 2,420 cases. Host rollout remains
open. H2/H3a/H3 source and dev.8 metadata are committed as `6f560a1` and
[published to both local feeds](../evidence/audit-pipeline-2026-10/release-dev8/README.md). This is local release acceptance; host acceptance remains open.

**Post-review correction:** The split receipt/source read race is fixed with a bounded evidence
recheck. [Retained review verification](../evidence/audit-pipeline-2026-10/h3-review-fixes-2026-10-16/README.md)
records 14 new SQL cases, 322 focused passes and 2,443 broad passes, zero failures. Historical
rows without receipts remain unverified; no version/schema change is required by these fixes.

## Identity and comparison contract

Jesse confirmed global EventId uniqueness, matching the existing database keys. An explicit
event requires a non-empty ExplicitEventId, persisted as EventId and used as the outbox key.
EnvelopeId remains per-input correlation. Entity-change identity remains EnvelopeId; the dev.9 correction below changes its producer
from content-derived IDs to mutation-operation tokens. Explicit-event GUID generation is unchanged: new GUIDs avoid accidental collisions, while
retries must reuse the original identity and semantic content. Dev.9 introduces UUIDv7
identities for EF mutation tokens, separately from the explicit-event contract. Repeated EnvelopeIds are rejected across
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
  producers remain a separate inventory gap. MillWorks integration is now in progress;
  the first scoped authorization implementation is described below.
- Map the receipt entity in mapped consumer models and apply the receipt migration to every
  owning audit/consumer database. Verify schema names, host authorization, custom capability
  conformance, retention/holds and DLQ copies before activation. Rollback must preserve identity
  evidence/schema and avoid running an older key-only drainer against the new protocol.
- Local release completed as dev.8 from commit `6f560a1`, with 2,644 regression and 416 final
  committed-source passes plus package-only consumer validation. Dev.7 remains immutable.
  Host consumer update, API build/restart and deployment have not been performed for H3a.

## MillWorks host integration — speech completion

**Status:** Source and regression tests prepared; compilation, test execution and runtime
acceptance remain pending. No API build/restart or database migration was performed for this item.

The first background producer is `MillWorks.Api/Hubs/AiSpeechSessionRegistry.cs`:

- The authenticated hub's resolver returns an immutable server-owned actor/tenant identity.
  The registry rejects options that disagree with it before starting the provider, then retains
  that identity with the session through timeout, stop or disconnect cleanup.
- The completion envelope carries tenant governance metadata, the session ID as both envelope
  and explicit event ID, and the retained summary's completion time. No transcript or audio is added.
- Scoped `SpeechSessionAuditAuthorization` grants only the exact completion envelope for that
  captured actor, tenant and session. It rejects unbound scopes, substitute envelopes and mixed
  batches. Outstanding permits expire when the completion binding ends; a later binding cannot
  revive them. Connection cancellation does not revoke authority to record the observed outcome.
- Immediate mode publishes directly. Transactional-outbox mode stages the source and identity
  receipt on an owned `AuditDbContext`, then saves both before releasing the publication binding.
  This records a completed session observation; it does not claim atomicity with an earlier
  speech-provider operation. Existing warning-on-failure behavior remains best effort.
- Added tests cover actor/tenant mismatch, unauthenticated resolution, publication scope and
  purpose boundaries, cancellation/disposal, disconnected completion, and real package outbox
  acceptance plus injected save failure using a disposable SQLite database.

Acceptance requires compiling `MillWorks.Api` and `MillWorks.Api.Tests`, then running the
`AiSpeechIdentityResolverTests`, `AiSpeechSessionRegistryTests` and
`SpeechSessionAuditAuthorizationTests` fixtures. These changes have not yet been executed.
The MillWorks `AGENTS.md` requires explicit permission for the API build; AI-dependent test
compilation must be batched with the user's rebuilds. Do not report old test artifacts as proof.

The host routes through `MillWorksAuditWriteAuthorization`: speech completion uses its
captured grant; EF change capture uses the active host-composed consumer context as described
below. Other explicit producers still need individual integration; this is not host rollout
acceptance.
During that review, entities whose changes do not merit auditing may be explicitly marked
`[NoAudit]` as authorized by Jesse. That attribute applies to entity/property change capture;
explicit usage events require their own keep/remove decision.

### Correction: global speech-only registration rejected EF capture

The user observed `AuditPublicationAuthorizationException` from
`SpeechSessionAuditAuthorization.AcquireAsync` during startup on the EF interceptor path.
The initial global registration incorrectly routed every audit producer to the speech-only
policy. Adjacent recurring-job messages do not identify the failing entity and are not a basis
for suppressing its audit.

The source correction registers `MillWorksAuditWriteAuthorization` globally and retains the
same scoped speech policy as a delegated capability. Entity-change envelopes require the
interceptor's active consumer context, the host's external audit model customizer, a distinct
tracked mutation per envelope, and matching entity keys, action, actor/request provenance and
tenant/resource identity. A permit expires if its source context ends or provenance changes.
An absent tenant remains explicitly confined to that host EF observation; no synthetic tenant,
public grant or explicit-event fallback is created. Other explicit publishers still fail closed.

Added regression tests cover the actual interceptor startup path, `[NoAudit]` exclusion,
provenance substitutions, context lifetime, mixed batches and routing speech's scoped grant.
Speech lifecycle fixtures now use the global router too. Compilation and execution are pending
the next authorized API/test rebuild; whitespace checks pass. No process restart, audit
failure-mode relaxation, entity exclusion or database change was used to mask this failure.

### Host blocker: entity-change identities can reuse temporary keys

The user's populated startup run now reaches the entity writer but reports
`audit_identity_unverified`. [Read-only investigation](../evidence/audit-pipeline-2026-10/host-identity-investigation-2026-10-16/README.md)
confirms historical role-claim creation rows without receipts and reproduces 100 historical
EnvelopeIds from their reused EF temporary keys and snapshots. Exact failing runtime IDs
remain absent from the supplied logs, so the specific attribution is inferred. Host rollout
remains blocked pending mutation-operation identity handling and the regression/acceptance
outcomes in that investigation. Do not disable role/permission auditing or infer historical
fingerprints to close this gate.

### EF mutation identity correction — dev.9 candidate

[Dev.9 is published locally](../evidence/audit-pipeline-2026-10/release-dev9/README.md): 2,664 Release,
27 focused and four targeted SQL cases passed, zero failures. MillWorks references and exact
package resolution are updated. Jesse rebuilt/restarted the host: populated startup now has
78 new role-claim creation records with 78 identity receipts and no observed identity errors.
Focused host verification now passes all 54 cases (zero failures/skips) after correcting
missing test DI registrations and Jesse rebuilding the tests.
[Evidence](../evidence/audit-pipeline-2026-10/release-dev9/host-rebuilt-verification.json) records the boundary; no source commit was made by the agent. Regression verification against the old producer
failed two cases (reused temporary keys and repeated value transitions) and passed the existing
failed-save retry case. This is reproduction evidence, not acceptance of the new implementation.

- `AuditMutationIdentity.Capture(context, entity)` assigns a UUIDv7 mutation identity to the
  tracked entity in that context lease. Failed/cancelled saves retain it. Successful SaveChanges
  retires pending identities before post-save provider dispatch. Later mutations with the same
  values and separate creations with reused temporary keys receive different identities.
- `AuditMutationToken` retains type, action and unresolved primary-key names with the identity.
  Callers retrying in a replacement context must preserve this token and original semantic
  inputs/provenance, track the retry, then call `Restore`. Serialize it with a durable command
  for process-restart retries. It is not proof of business commit; callers must reconcile an
  uncertain business commit before repeating a mutation. A new context without a restored
  token is new work. Equal values are never sufficient to infer a retry.
- Initially temporary generated primary keys remain unknown in captured locators/snapshots,
  governance resource IDs and subject references, including token restoration after key fixup.
  Known client-assigned keys remain available. Other semantic inputs, including relationships,
  must still match: no missing original business input is inferred from a token.
- The content-hash helper has no remaining callers in the inspected AuditCore/MillWorks source
  inventory and is removed. Existing stored IDs, receipts, fingerprint version and queue format
  are unchanged. An old envelope addressed directly still fails as unverified when evidence is
  missing. No source rows are deleted or granted invented historical fingerprints.
- Failure diagnostics add bounded envelope/logical IDs, entity names, actions and stable error
  codes without payload values. Audit-owned receipt, handoff, resolution and subject-index
  bookkeeping is `[NoAudit]` to prevent recursive capture; business permissions remain audited.

Acceptance gates:

- [x] Focused real-interceptor SQLite lifecycle tests pass (27 focused checks, zero failures/skips): separate mutations, generated keys,
  failed-save retry, token serialization/restoration, rollback, changed retry, lost audit
  acknowledgement, historical unverified rows and pooled context lease reuse.
- [x] Four disposable SQL Server cases pass, zero failures/skips, with RCSI off/on for generated integer/composite keys,
  retained-token replay after durable audit acceptance and separate subsequent mutations.
- [x] Release build and broader SQL-enabled regression passed: 2,664 tests, zero failures.
  No model or migration change was introduced.
- [x] Publish fresh dev.9 to both local feeds with exact source/test/package fingerprints;
  preserve every distributed prior artifact and leave the commit to Jesse.
- [x] Coordinate 21 active MillWorks direct reference projects and host EF provenance policy
  with mutation tokens; API dependency restore confirms all five delivered package hashes.
- [x] Complete focused host-test acceptance: after correcting three test service registrations
  and Jesse rebuilding, all 54 cases pass with zero failures/skips.
  [Final evidence](../evidence/audit-pipeline-2026-10/release-dev9/host-final-verification.json).
- [x] Verify populated startup emits newly accepted role-claim audit records with receipts and
  no unexplained identity failures: 78 new creation rows/78 receipts after the October 17
  rebuild/restart, zero matching errors in the observed log window. Old history retained.
