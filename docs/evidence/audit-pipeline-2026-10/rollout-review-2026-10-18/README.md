# Producer inventory and host rollout review — 2026-10-18

Steps 1 and 2 of the batch-hardening follow-up were investigated against current source,
compiled test artifacts and the configured development endpoint. **The inventory is complete
for the inspected MillWorks checkout; the rollout gates have not passed.** No application
configuration, source, database, package feed or managed process was changed by this review.
H4's small internal interface over `OutboxStateStore` is approved by Jesse; implementing that
interface and isolated orchestration tests remains a separate follow-up.

## 1. Producer inventory and contract verification

[Reproducible inventory](inventory.py) scanned 32,796 production C# files in MillWorks,
then inventoried every production `AuditEnvelope` reference (15 files) and manually reviewed
those files for aliases, target-typed construction, factories, forwarding and `with` copies.
There are 14 explicit-event construction sites in 11 producer files. No additional target-typed
producer was found. The generic `AuditBridge.PublishAsync(AuditEnvelope)` entry point forwards
a supplied envelope; no production caller of that entry point was found in this checkout.
The lexical field scan is evidence of construction, not proof of authority or runtime acceptance.
[Source locations, field expressions, hashes and actual project references](producer-inventory.json)
are retained. The original ten-missing-ID inventory is superseded: all 14 sites now supply an
ExplicitEventId, but supplying any GUID does not establish safe retry identity.

Paths below are relative to `MillWorks.Api/`. “Bound” means the current global router delegates
to a policy that holds the exact producer observation through publication. It does not mean
that every producer has passed live end-to-end acceptance.

| Producer / source | Identity and occurrence time | Authorization / tenant metadata | Disposition |
|---|---|---|---|
| Speech completion — `Hubs/AiSpeechSessionRegistry.cs` | Retained session ID and EndedAt | Bound speech policy; tenant metadata present | Source contract established; outbox observation explicitly saves source and receipt together |
| Dataset snapshot — `Bridge/Audit/MlDatasetSnapshotAuditBridge.cs` | Supplied EventId and OccurredAt | Bound dataset policy; tenant metadata present | Source contract established; outbox consumer transaction still requires host-specific proof |
| Breach export generation/download — `Bridge/Compliance/ComplianceEvidencePackageBridge.cs` | New ID/time for each observed generation/download call | Bound breach policy checks authenticated actor/tenant; resource metadata present | Individual observation supported; replay of a failed publication has no retained observation token |
| Document DLP — `Bridge/Document/DocumentDlpAuditSinkBridge.cs` | Supplied EventId and OccurredAt | Tenant/resource metadata present; no routed producer grant | Blocked by current global authorization router |
| CRM hub and section access (two sites) — `Bridge/Crm/CrmHubAuditBridge.cs` | New ID on each call; constructor-default occurrence time | No governance identity or envelope UserId; no producer grant | Publication rejected; bridge catches and logs the failure |
| Portal — `Bridge/Audit/AuditBridge.cs` | Supplied EventId/OccurredAt; request-derived actor/organization/correlation/IP/user agent | No governance identity or producer grant | Publication rejected; preserve original request provenance when retrying |
| Finance event/payment (two sites) — `Bridge/Audit/AuditBridge.cs` | New ID on each call; constructor-default occurrence time | No governance identity, envelope actor or producer grant | Publication rejected; current signatures do not carry a retry observation identity/time |
| Decision Models governance — `Bridge/Audit/DecisionModelGovernanceAuditBridge.cs` | Supplied EventId/OccurredAt; correlation can fall back to current HTTP trace | No governance identity or producer grant | Publication rejected; retain original correlation on retry |
| Decision Models workbench — `Bridge/Audit/DecisionModelWorkbenchAuditBridge.cs` | Supplied EventId and OccurredAt | No governance identity or producer grant | Publication rejected |
| Artifact retention — `Bridge/DecisionModels/DecisionModelArtifactRetentionAuditBridge.cs` | Supplied EventId and OccurredAt | Checks tenant accessor, but does not put governance identity on envelope or bind a grant | Publication rejected |
| Training manual reconciliation — `Bridge/DecisionModels/DecisionModelTrainingOperationsAdminBridge.cs` | ReconciliationId and RequestedAt | Tenant scope is used for domain work; envelope has no governance identity or producer grant | Publication rejected |
| Visitor protected-reference access — `Bridge/VisitorManagement/VisitorProtectedReferenceService.cs` | New ID/time on every observation | No governance identity or producer grant | Publication rejected; distinguish a new dereference from replay of one retained observation |

Concrete remaining work:

- **11 of 14 construction sites lack a matching producer grant.** The router accepts only
  speech, datasets and breach exports; other explicit events fall through to the speech policy
  and fail its exact binding checks. Add producer-specific trusted bindings, tenant/resource
  provenance and substitution/expiry tests before calling those producers onboarded. Do not
  authorize arbitrary envelope names or client-submitted actors merely to make them publish.
- **Ten sites lack governance metadata.** The CRM payload's nested UserId is not the envelope
  UserId. Finance also omits the envelope actor. Governed retention cannot infer these fields
  from arbitrary payload JSON.
- **Six sites mint observation IDs inside each bridge call** (CRM x2, Finance x2, breach export,
  visitor); four of those also use default occurrence time. This can represent a new observation,
  but it cannot deduplicate a retry of the same observation. Retain the completed observation's
  ID, time and original semantic input outside publication retries; where absent, update the
  owning producer contract rather than hash equal values into a supposed retry identity.
- Canonical comparison includes CorrelationId, IpAddress, UserAgent, actor, governance metadata
  and explicit occurrence time (`AuditEnvelopeContent.Fingerprint`). Portal reconstruction and
  governance's HTTP-trace fallback can change content under a retained logical ID. Replay the
  original semantic envelope rather than refreshing request metadata.
- No custom host `IAuditSink`, `IAuditOutboxWriter`, identity-aware outbox writer or envelope batch
  logger implementation was found. Built-in dev.9 implementations supply those capabilities.
  Direct `IAuditLogger` call sites are separately enumerated in the JSON; this review does not
  pretend they use the envelope publication authorization/outbox protocol.

## 2. Host, database, drainer and recovery verification

### Owning contexts and receipts

`Program.cs` configures `AuditDbContext` against `DefaultConnection`, schema `audit`, startup
migration enabled. AuditCore owns `audit.__EFMigrationsHistory` and the shared audit tables.
Ten host-composed consumer registrations use `UseExternalAuditTransactionalOutbox`:
Identity, Compliance, DecisionModels, Visualization, Diagramming, Chat, Finance, Payments,
DataProcessing and Resource. Their host model customizer maps `AuditOutbox` and
`AuditEnvelopeIdentities` into `audit` and excludes both from domain migrations. The receipt
mapping is present in source; its presence does not establish an applied owning migration.

**CommentThreads is the eleventh intercepted context and is a concrete gap.** Its library
extension attaches `AuditSaveChangesInterceptor`, but the host's `AddCommentThreads` options
callback only configures SQL. It does not install the external model customizer. The global
EF publication policy requires that customizer even in Immediate mode, so those observations
cannot pass the current policy. Compose the CommentThreads consumer model consistently and
prove rollback plus authorized capture before closing this context's gate.

Expected hardening migrations in the owning source chain are:

- `20261016210852_AddOutboxOwnershipEvidence`
- `20261016211640_RetainOutboxFailureSources`
- `20261016230118_AddAuditEnvelopeIdentities`

The earlier idempotency/lease migration and the complete owning chain must also agree with
the database history. This review generated/applied no migrations and changed no history.

### Runtime and queued-data boundaries

[Host source/artifact inventory](host-source-and-artifacts.json) records dev.9 in both test
projects and the API dependency file. The API dependency file was rebuilt October 18 at
16:18 UTC. All five output module hashes are in [the runtime observation](host-runtime.json);
[comparison with the cached dev.9 packages](host-package-module-check.json) confirms all five
API output DLLs match their package entries. This verifies files, not a running process.
The source configuration files specify no AuditSinkMode; `Program.cs` falls back to Immediate.
No running host's effective configuration was read, so environment/secret overrides are unknown.

The configured application database `127.0.0.1:1433/MillWorks` was unavailable during the
read-only probe. Docker listed only the disposable SQL-test service on port 1435. Process
inspection found dotnet processes but no mapped MillWorks API/AuditCore modules in those
candidates; this is a bounded local observation, not proof that no drainer exists elsewhere.
No process was stopped, restarted or replaced. Current database migration history, actual
queue-format counts, lease owners and handoff state therefore remain **unverified**. The
October 17 EF startup acceptance remains historical evidence, not a fresh October 18 scan.

[Read-only SQL probe](host-readiness.sql) and [host probe](probe-host.py) are retained for the
next running-host check. The probe returns metadata and aggregate counts, not envelope
payloads. Queue format 1 is unsupported by the new drainer and receives guarded terminal
handling; its actual presence and reviewed disposition must be established before activation.
Never infer an empty queue from an unavailable database or use the test SQL instance as the
application acceptance target.

### Recovery, retention and copied data

- No `IAuditOutboxRecoveryAuthorization` implementation or registration exists in the inspected
  MillWorks production source, nor an operator integration calling `IAuditOutboxRecovery`.
  The library intentionally has no permissive default. Replay/skip/cleanup therefore lack a
  supported authorized host path. Implement the operator permission/tenant/quarantine boundary
  and hold coordination before claiming operational recovery acceptance.
- `AuditRetentionSweepJob.DeleteTerminalOutboxAsync` still directly deletes Failed rows selected
  by CreatedAt and configured age. It does not require a resolution or check current handoffs,
  verification receipts or a tenant/hold permit for that deletion. Completed-row deletion also
  bypasses the guarded recovery cleanup boundary. H1's restrictive handoff FK may reject some
  deletions; it is not authorization or a complete cleanup/recovery protocol.
- Route eligible resolved failure payload cleanup through the supported guarded operation,
  with host approval/holds and verification windows; keep unresolved failures and live
  projections. Coordinate ordinary completed-row cleanup separately with retained identity
  evidence and holds. Do not mark the host's existing sweep as H1-compliant.
- The sweep is policy-gated (`Audit:Retention:Policy`). Its currently approved live policy could
  not be read while SQL was unavailable. No claim is made that this risky branch executed.
  External DLQ/archive copies and their disposition/restore suppression remain unverified;
  this review did not delete or manufacture reconciliation evidence for any copy.

### Other concrete local consumers

The XML inventory identifies additional named checkouts, rather than assuming hypothetical
external deployments: AceDs uses three source project references; Aced, IexcelApps and
MillWorksProject reference 1.0.4; MillWorks.BlackBox.Worker references 1.14.0; IExcel centrally pins
AuditCore to 1.15.4-dev.3 (and its EF package to 1.8.12). MillWorks-SurveyCutover contains older package pins. Historical
projects under Evidence/Reports and BackgroundJobs test consumers are retained separately by
path. They were not upgraded or activated by this MillWorks rollout review. Their owning
configuration/database/runtime gates remain unverified; a local reference is not evidence of
an active old drainer against the MillWorks database.

## Verification performed

Using the shared runner with existing artifacts (`--no-build --no-restore`):

- **100 passed, zero failed/skipped** in API tests: speech identity/session, global authorization,
  dataset and breach bindings, DLP mapping, Decision Models bridge mapping and retention hold
  authority. [TRX](rollout-host-focused.trx), [fixture counts and artifact hash](host-test-verification.json).
- **18 passed, zero failed/skipped** in bridge tests: portal mapping/retry behavior, dataset
  mapping and breach evidence publication. [TRX](rollout-bridge-focused.trx),
  [fixture counts and artifact hash](bridge-test-verification.json).

These tests prove the selected compiled fixtures. Mapping tests using mocked sinks do not
prove that an envelope passes the real global authorization router. No application SQL fixture,
H5 workload, host build/restart, migration or production publication was performed.

Commands (run from the MillWorks checkout):

```sh
python3 scripts/dev-work.py test MillWorks.Api.Tests/MillWorks.Api.Tests.csproj -- --filter 'FullyQualifiedName~AiSpeechIdentityResolverTests|FullyQualifiedName~AiSpeechSessionRegistryTests|FullyQualifiedName~SpeechSessionAuditAuthorizationTests|FullyQualifiedName~MillWorksAuditWriteAuthorizationTests|FullyQualifiedName~MlDatasetSnapshotAuditAuthorizationTests|FullyQualifiedName~BreachEvidenceAuditAuthorizationTests|FullyQualifiedName~DocumentDlpAuditSinkBridgeTests|FullyQualifiedName~DecisionModelWorkbenchAuditBridgeTests|FullyQualifiedName~DecisionModelGovernanceAuditBridgeTests|FullyQualifiedName~AuditRetentionLegalHoldAuthorityTests' --logger 'trx;LogFileName=rollout-host-focused.trx' --results-directory /tmp/audit-rollout-host-tests
python3 scripts/dev-work.py test MillWorks.Api.Bridge.Tests/MillWorks.Api.Bridge.Tests.csproj -- --filter 'FullyQualifiedName~ClientPortalAuditBridgeTests|FullyQualifiedName~MlDatasetSnapshotAuditBridgeTests|FullyQualifiedName~ComplianceEvidencePackageBridgeTests' --logger 'trx;LogFileName=rollout-bridge-focused.trx' --results-directory /tmp/audit-rollout-bridge-tests
```

Current source hashes are in [the manifest](source-manifest.json). Exact commands and the
read-only endpoint observation are retained; no prior release artifact was overwritten.
