# H3a implementation discovery — 2026-10-16

**Status:** Initial discovery retained below as a historical baseline. Jesse confirmed global
EventId uniqueness on 2026-10-16. Library implementation and SQL verification are complete under the
[H3a implementation contract](../../../plans/BatchPublishingH3aImplementation.md); SQL results
are retained in the [verification record](../h3a-verification-2026-10-16/README.md). This record does not establish
host inventory, release or runtime acceptance. H2 changes are preserved.

## Pre-implementation source behavior

- `AuditEventEntity.EventId` is the global primary key. TenantId is an indexed nullable
  attribute, not part of that key. Outbox IdempotencyKey is globally unique as well.
- `AuditEventBatchWriter` persists EnvelopeId as EventId, while the outbox prefers ExplicitEventId
  and falls back to EnvelopeId. Both sinks therefore need coordinated changes.
- Both outbox paths (mapped tracker and raw SQL) classify duplicates using keys alone. Content
  comparison must cover tracked, same-batch and persisted candidates, plus concurrent inserts.
- The existing logger also treats unique-key failures as duplicates. Changing only the mapper
  would leave changed-content reuse accepted silently. Conflict enforcement must reach the
  persistence/duplicate recovery boundary, including direct logger collisions.
- `AuditEnvelope` carries governance tenant/resource and typed subject links. The explicit-event
  mapper currently does not carry them into the stored event. They must participate in the
  comparison contract and survive whichever persistence seam is selected.
- `IAuditContext` has TenantId, but it is mutable context metadata rather than a write-
  authorization permit. The producer entry point and committed-outbox processing need an
  explicit authority model before replay/conflict details are disclosed.
- The existing integrity canonicalizer normalizes arbitrary timestamp-like strings and falls
  back to double for large numbers. Do not silently equate its integrity algorithm version
  with a new immutable event-content comparison contract.

## Producer inventory

[Source initializer inventory](producer-inventory.json) found 14 explicitly typed production
`new AuditEnvelope` initializers for explicit events across 11 MillWorks API files. Ten omit
ExplicitEventId. No explicitly typed production explicit-event initializer was found in the
scanned AuditCore source/sample paths. This is a bounded syntactic inventory, not proof that
all producers were found: target-typed initializers, external packages and other host projects
remain to be inventoried. JSON records file hashes and source baseline commits.

The ten missing-ID locations are:

- `MillWorks.Api/Bridge/Crm/CrmHubAuditBridge.cs:28`
- `MillWorks.Api/Bridge/Crm/CrmHubAuditBridge.cs:63`
- `MillWorks.Api/Bridge/Compliance/ComplianceEvidencePackageBridge.cs:34`
- `MillWorks.Api/Bridge/DecisionModels/DecisionModelArtifactRetentionAuditBridge.cs:23`
- `MillWorks.Api/Bridge/DecisionModels/DecisionModelTrainingOperationsAdminBridge.cs:164`
- `MillWorks.Api/Bridge/VisitorManagement/VisitorProtectedReferenceService.cs:137`
- `MillWorks.Api/Bridge/Audit/DecisionModelWorkbenchAuditBridge.cs:20`
- `MillWorks.Api/Bridge/Audit/DecisionModelGovernanceAuditBridge.cs:65`
- `MillWorks.Api/Bridge/Audit/AuditBridge.cs:133`
- `MillWorks.Api/Bridge/Audit/AuditBridge.cs:159`

No host files were changed. Producer updates must preserve the originating event's identity
and occurrence time across retries; generating a fresh ID on every attempt is not a fix.
Some producers supplying IDs still set occurrence time from the current clock, which needs
review under the immutable-content contract.

## Implementation gates

1. Global EventId uniqueness confirmed; existing keys are preserved.
2. Record canonical semantic fields, pre/post-redaction timing, comparison version and
   retained evidence; entity-change repair must compare original semantics without losing
   missing-property repair. Keep envelope correlation separate from logical identity.
3. Specify authority for producer replay/conflict comparison and internal committed-outbox
   processing. Return only payload-safe classifications and caller-supplied correlation IDs.
4. Select the persistence/result seam necessary to atomically compare and insert under
   uniqueness races. Never infer content equality from existence alone or report unpersisted
   same-batch references as successful duplicates.
5. Inventory actual queued/persisted data before host rollout. Missing historical comparison
   evidence is unknown, not equal. No data reset, deletion or EventId rewrite is authorized.
6. Update producers/fixtures and cover immediate/outbox, same-batch/concurrent, post-drain,
   redaction, tenant/actor/payload changes, malformed IDs and entity partial repair before
   marking H3a implemented. No package publication is part of this discovery step.
