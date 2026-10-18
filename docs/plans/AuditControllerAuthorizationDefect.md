# Defect: Audit.Read authorizes administrative writes and user export

**Date:** 2026-10-16  
**Last reviewed:** 2026-10-18\
**Status:** Dev.10 delivered; rebuilt running host verified; all 15 administration HTTP boundary tests pass. Broader read/disclosure findings, an existing query assertion mismatch and deployment readiness remain open.\
**Priority:** High; independent prerequisite for the audit experience  
**Owning implementation:** MillWorks.Api with MillWorks.Identity policy ownership and AuditCore service integration  
**Related research:** [Audit experience and MCP spike](AuditExperienceAndMcpResearchSpike.md)

## Original finding (2026-10-18 review baseline)

`MillWorks/MillWorks.Api/Controllers/Audit/AuditController.cs` applied `[Authorize(AuditPermissions.Read)]` at class level. At that review, the actions below had no method-level authorization requirements or explicit subject/resource authorization checks in their action bodies. They delegate directly to audit services. The inherited `AuthorizedControllerBase` also requires authentication, but adds no operation-specific permission or audit-resource scope check. The current read policy therefore serves as their operation authorization, although these are mutations or sensitive exports.

| Route under `/api/v1/audit` | Exposure at original review | Required remediation |
| --- | --- | --- |
| `POST compliance/anonymize/{userId}` | A reader can request user anonymization. | Require dedicated operation permission, authorized tenant/subject scope, and explicit destructive-action flow. |
| `POST archive` | A reader can request archival by date. | Require archival administration and constrain the affected scope. |
| `POST archive/restore/{archiveId}` | A reader can request restoration. | Require restore administration and authorize the archive and destination scope. |
| `GET compliance/export/{userId}` | A reader can request another user's audit-data export: selected fields from up to 100,000 events, including raw `JsonData` as `Description`. | Require export authority, subject/tenant authorization, and disclosure filtering. Define any self-service export separately. |
| `POST test-event`, `POST test-event-scope`, `POST test-operation` | A reader can create synthetic audit records. | Remove test routes from the normal application surface or enforce explicit development/test controls and write authorization. |

This is a source-confirmed authorization defect. It is not a claim of a live exploit or a completed review of every host policy. Validate the complete authorization pipeline during remediation; do not treat unspecified global policy as the fix.

## Current source evidence (2026-10-18)

Reviewed the local AuditCore and consuming MillWorks working trees (HEADs `e45b3ac` and `2a8d299697`, respectively). These identify the checkout bases, not a claim that all reviewed files are committed; the consuming tree includes local audit infrastructure changes.

| Evidence | Current implementation and implication |
| --- | --- |
| MillWorks: `MillWorks.Api/Controllers/Audit/AuditController.cs:21`, `:40`, `:61`, `:83`, `:501`, `:512`, `:548`, `:559` | The class-level read policy and all seven routes remain. There is no action-level authorization or environment guard on the synthetic write routes. |
| MillWorks: `MillWorks.Api/Controllers/AuthorizedControllerBase.cs:17`; `MillWorks.Api/Extensions/AuthorizationServiceExtensions.cs` | The base controller requires authentication; the default host policy requires an authenticated principal. Neither supplies the missing audit operation or resource checks. |
| MillWorks: `MillWorks.Abstractions/Authorization/Permissions/Permissions.Audit.cs` | Despite its file location, the permission type is in namespace `MillWorks.Identity`. `Read`, `Export`, and `Admin` already exist; `View` aliases `Read`. The controller does not use `Export` or `Admin`. Separate anonymize/archive/restore permissions are not defined in this audit permission group. |
| MillWorks: `MillWorks.Identity/Handlers/PermissionPolicyProvider.cs`; `PermissionAuthorizationHandler.cs` in the same directory | Dynamic permission policies require the named permission; the handler can satisfy `Read` from the principal's permissions without requiring export or administration. No audit subject/archive authorization is added here. |
| AuditCore: `src/MillWorks.AuditCore.Services/AuditComplianceService.cs:138`, `:202`; `src/MillWorks.AuditCore.EntityFramework/Repositories/AuditEventRepository.cs:27` | Anonymization and export take a user ID without an authorized tenant/subject context. The repository query filters by `UserId`, orders by date, and caps results; it has no tenant predicate. Export selects event metadata plus `JsonData`, with no disclosure filtering in its projection. The former description of a “full” export was inaccurate. |
| AuditCore: `src/MillWorks.AuditCore.Services/AuditArchivalService.cs:110`, `:479`; `IAuditArchivalService.cs` in the same directory | Archive accepts a date and optional archive ID; restore accepts an archive ID. Storage configuration, archive status, and integrity checks are operational safeguards, not caller authority or tenant/destination authorization. |
| MillWorks: `MillWorks.Api/Infrastructure/Audit/MillWorksAuditWriteAuthorization.cs`; AuditCore: `src/MillWorks.AuditCore.Services/AuditLogger.cs`, `AuditLogger.Identity.cs` | Newer envelope publication permits validate trusted producers and EF observations. They do not authorize these administrative routes. Synthetic actions call `IAuditLogger` directly, whose direct event-write path does not acquire an envelope publication permit. Identity-ledger checks protect event identity, not permission to invoke test writes. |
| MillWorks: `MillWorks.Api.Tests/ControllerTests/Audit/AuditChainStatusTests.cs` | Existing controller tests exercise chain-status responses with mocked services and direct method calls. No tests for these seven routes' permission/subject boundaries were located in the API test source search. This does not establish HTTP acceptance. |

Review method: source inspection only; no builds, tests, live API calls, or audit mutations were performed. Runtime package resolution and deployed behavior were not verified. Closure still requires the HTTP and state evidence below.

## Implementation (2026-10-18)

The source changes now enforce the following boundaries. HTTP acceptance remains pending compilation of the consuming API and its tests; do not treat library tests as proof of the deployed API.

| Surface | Implemented behavior |
| --- | --- |
| User export | Requires both `Audit.Read` and `Audit.Export`. The host verifies the persisted active actor and target subject in the current tenant; the service also filters every event by tenant and subject. Export is an explicit `event-metadata-v1` allow-list containing event ID, timestamps, and duration. It excludes arbitrary JSON, producer-controlled event/action labels, names, IP addresses, and natural entity keys. Exports over 100,000 matching events fail rather than silently truncate. Responses use `Cache-Control: no-store`. This is a staff permission, not self-service export. |
| Anonymization | Requires `Audit.Read` and `Audit.Admin`, an exact reviewed target, current actor/subject scope, and a Compliance tenant legal-hold permit held through execution. Content redaction is limited to that tenant and subject. If tamper detection is enabled or matching integrity records, queued integrity work, or event identity receipts exist, execution returns conflict without rewriting the evidence. Unprotected content redaction retains event and subject locators; it does not claim full erasure. Protected evidence must use governed erasure/retention. |
| Archive | Requires `Audit.Read` and `Audit.Admin`, exact reviewed cutoff, and a tenant legal-hold permit. Selection and event deletion are tenant-bound. The archive record stores tenant ownership in existing metadata and the initiating actor. |
| Restore | Requires `Audit.Read` and `Audit.Admin` and an exact reviewed archive ID. Unknown, foreign, malformed, and unscoped archive ownership is denied. Every restored event must have the same tenant, and each integrity record must reference an event in that tenant. The existing whole-file hash and database transaction remain authoritative; failures roll back restored batches. A private archive transport retains event content and tenant/subject identity without expanding the public query DTO. |
| Integrity-proof export | The additional export found during implementation now requires `Audit.Read` and `Audit.Export`, verifies event tenant ownership, and uses `no-store`. It follows the administration evidence path. |
| Synthetic writes | `test-event`, `test-event-scope`, and `test-operation`, plus their unused request model, are removed from the consuming controller. |
| Administrative evidence | The library writes append-only `AuditAdministration` security events with tenant, actor, subject where applicable, operation, correlation, exact cutoff/archive/event ID, and started/succeeded/failed outcome. Successful new archives record the returned archive ID. No exported payload enters these receipts. A durable started receipt precedes cross-storage operations; a crash can leave it without an outcome and requires reconciliation. Unprotected redaction and its success receipt share a serializable database transaction. |

Implementation locations:

- AuditCore: `src/MillWorks.AuditCore.Services/Administration/`, `AuditArchivalService.cs`, `IAuditArchivalService.cs`, and private `Mapping/AuditArchivedEvent.cs`; DI is in `MillWorksAuditBuilder.cs`.
- MillWorks host: `MillWorks.Api/Infrastructure/Audit/AuditAdministrationAuthority.cs`, the audit controller, and `AuditIntegrityAlertRegistrationExtensions.cs`. `Audit.Admin` is the selected administrative operation permission; there is no administrator-role shortcut.
- Package integration: AuditCore `1.15.4-dev.10`; both consuming API package references are updated together. No schema change or migration is required. Existing system-wide archival entry points remain for concrete maintenance/background-service callers; the API uses only the tenant-bound administration facade. The separate standalone sample still demonstrates the older service methods and is not the consuming API surface reviewed here.

### Destructive-action protocol

1. Call `POST /api/v1/audit/administration/review` with exactly one target: `{"operation":"Archive","archiveBefore":"2026-01-01T00:00:00Z"}`, `{"operation":"Restore","archiveId":"..."}`, or `{"operation":"Anonymize","subjectUserId":"..."}`.
2. Display the returned tenant and exact target and obtain the user's confirmation. The API does not infer human approval from a successful preview request.
3. Send the returned protected `confirmation` as `X-Audit-Confirmation` on the corresponding existing action route. The confirmation expires after five minutes and is bound to actor, tenant, operation, and every target argument. Current membership, permissions, ownership, and applicable legal holds are checked at execution. Changed targets require another review. This protocol does not substitute for any additional domain-specific approval workflow.

### Verification

- Initial library build: passed with zero warnings/errors.
- Initial focused administration/archive tests: 43 passed before the final archive/proof/transaction regression additions. Final results and local package publication are recorded below when complete.
- Added library coverage for tenant/subject disclosure, missing/revoked authority, protected evidence refusal, scoped archive selection and ownership, event-tenant validation, proof ownership, and preservation of proof inputs. SQLite integration coverage verifies rollback after an earlier 500-event batch when a later event is foreign or the final file hash is invalid.
- Added `MillWorks.Api.Tests/ControllerTests/Audit/AuditAdministrationHttpBoundaryTests.cs`: real MVC and Identity permission handling with the actual host authority and library facade. Covers anonymous/read-only denial, synthetic route absence, tenant/subject checks, metadata-only export and `no-store`, exact/expired confirmation, membership revocation, legal holds, and protected-evidence refusal. Existing chain-status constructor fixtures are updated.
- Jesse rebuilt API/tests and restarted the API on October 18. Subsequent `--no-build --no-restore` checks passed all 15 administration HTTP boundary tests, with dev.10 dependencies and matching package DLLs. The agent did not build/restart the API or mutate application audit data; see the rebuilt-host evidence below.

### Broader findings retained as open work

The route defect must not be interpreted as closure of the audit read/disclosure boundary. Source review found these separate paths still passing unscoped calls to existing services:

| Source | Remaining finding |
| --- | --- |
| MillWorks `AuditController`: `events`, `events/{eventId}`, `search`, `events/by-date`, `events/by-user`, `trail`, user/recent activity, and entity search; AuditCore `AuditSearchService.BuildSearchQuery`, `AuditQueryService` | Read permission alone still reaches query paths without an explicit authorized tenant/resource argument. Free-text payload disclosure and entity grants need a complete scoped query contract. The newly protected export routes do not prevent reads through these paths. |
| MillWorks `AuditController`: summary/chart/distribution/top-user/distinct-user/security queries, compliance reports and retention validation | Aggregation and metadata endpoints also require an explicit scope review; global totals and identities can disclose other tenants' activity. |
| MillWorks `AuditController`: archive listing/validation and global integrity verification/detection; `AuditIntegrityAlertsController.ScanAsync` | Archive metadata and global detection/verification have broader scope than a tenant read. Detection/projection can persist security observations. Define platform operational authority separately from tenant read authority. Existing tenant-scoped alert list/lifecycle actions are distinct from this global scan boundary. |

These are high-priority follow-up findings with source evidence, as required by remediation step 5. They remain open and are not claimed fixed by this administration change. Governed erasure, new UI wiring to the confirmation protocol, and crash reconciliation of started operation receipts also remain outside this controller authorization fix.

## Remediation scope

1. Use the existing `Audit.Export` for export authority and explicitly decide whether `Audit.Admin` is sufficient for each administrative operation or whether separate anonymize/archive/restore permissions are needed. Add missing policies with the owner; do not rely on a broad administrator role label alone. Account for combined class/action requirements: adding an action-level policy retains the class-level `Audit.Read` requirement unless the controller policy layout is changed.
2. Enforce tenant, subject, archive, and record scope in the API composition layer and relevant service contracts. A stronger route permission alone does not establish data scope.
3. Apply disclosure/redaction to export and log access without duplicating sensitive exported content into telemetry.
4. Audit administrative actions with initiator, authorized scope, outcome, and correlation. Define explicit user confirmation for destructive actions in their product flows.
5. Review the remaining controller actions and current query endpoints for equivalent issues, including unscoped reads. Track any broader findings with concrete evidence.

## Acceptance evidence

- An authenticated fixture actor with only `Audit.Read` cannot anonymize, archive, restore, export another user's data, or invoke synthetic test writes. Exercise the actual HTTP authorization pipeline; direct controller-method tests do not enforce authorization attributes.
- Each permitted operation succeeds only for the correct dedicated authority and authorized tenant/subject/archive scope.
- Unauthorized direct API calls are denied independently of hidden frontend controls or MCP tool visibility.
- Export results obey disclosure policy, including cross-tenant and unauthorized-subject denial.
- Denied mutations do not change audit state. Successful administrative operations preserve the required integrity and action evidence.
- Test routes are absent from the normal surface or demonstrably constrained by the selected development/test policy.

This repository document remains the tracked issue; no remote issue has been created. Keep source implementation, library verification, consuming HTTP acceptance, and broader read-scope findings distinct when updating closure status.

## Dev.10 local delivery — 2026-10-18

[Release evidence](../evidence/audit-pipeline-2026-10/release-dev10/README.md) records the
Release build, 2,503 broad passes with one stale SQL fixture failure, its test-only correction,
and 42 final focused passes with zero failures/skips. A fresh-cache package-only consumer
passed before all five coordinated packages/symbols were published to both local feeds.
Existing feed artifacts were preserved. MillWorks.Api restore now succeeds and all five
AuditCore dev.10 package hashes match the delivered artifacts; BackgroundJobs resolves dev.20.
The API was not built/restarted by this task. Consuming HTTP/runtime acceptance and the
broader read/disclosure findings above remain open.

## Rebuilt-host acceptance — 2026-10-18

[Rebuilt-host evidence](../evidence/audit-pipeline-2026-10/release-dev10/host-rebuilt-verification.json)
records 15 passing administration HTTP boundary cases and no skips against Jesse's rebuilt
API/tests. Runtime artifact inspection confirms AuditCore dev.10/BackgroundJobs dev.20,
matching DLLs and a process started after the build. Live anonymous export/proof requests
return 401. This closes the focused consuming HTTP test and loaded-artifact gates.

The selected general chain/query fixture additionally has four passes and one failure: an
existing test supplies 23:59:59 but expects the final tick of the day, while the unchanged
helper only expands midnight/date-only values. This mismatch remains separate from the
administration fix; no test expectation or production date semantics were changed here.
Liveness is Healthy; readiness reports Degraded. Broader read/disclosure work above is still
open, and no destructive administration operation was exercised against application data.
