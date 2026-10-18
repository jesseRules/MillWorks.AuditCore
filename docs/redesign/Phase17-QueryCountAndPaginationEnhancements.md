# Phase 17 — Query Count And Pagination Enhancements

**Status: Absorbed into enterprise audit experience G1–G3; old optional-count offset proposal superseded. Planning only; implementation pending. Reviewed 2026-10-18.**

Master plan context: [`../RedesignPlan.md`](../RedesignPlan.md)
Completed prerequisite: [`Completed/Phase13-QueryPageSizeLimits.md`](Completed/Phase13-QueryPageSizeLimits.md)

## Enterprise audit experience dependency — 2026-10-18

The [enterprise audit experience plan](../plans/AuditExperienceAndMcpResearchSpike.md)
owns a single shared evidence contract. Phase 17's count/pagination requirements,
code inventory and verification work are absorbed into its G1–G3 gates. The older
proposal to ship optional `int` totals with offset paging first and activate
keyset/64-bit work later is superseded. Do not activate that proposal independently
or leave it as a second audit pagination API after G1 ships.

Replace affected audit requests/responses and migrate concrete repository, sample,
and host callers in the same implementation scope; remove superseded audit APIs.
Do not retain forwarding overloads or compatibility shims for hypothetical
consumers. The generic repository's offset helper may continue to serve unrelated
non-audit consumers; that does not create a second audit evidence contract. This
decision selects the public paging/count contract without selecting SQL Builder
inline versus queued execution or the storage topology.

The [service-objective table](../plans/AuditExperienceAndMcpResearchSpike.md#planning-service-objectives-and-acceptance-measurements)
is the source of numerical performance targets. G0 must approve the
[agreed acceptance workload](../plans/AuditExperienceAndMcpResearchSpike.md#agreed-acceptance-workload--g0-exit-requirement)
before comparative performance can pass or fail. These are planning criteria, not
measured capacity or a deployed SLA. Benchmark count/page/enrichment costs
separately using that profile's authorized fixtures and tenant/ACL distribution.

## Constraints

These restate the master plan's implementation constraints for this phase:

1. **Plan is the spec.** G1 must finalize exact file scopes, including current
   cross-repository callers, before code changes. The inventory below is a
   starting checklist, not approval to create unnamed helpers or interfaces.
2. **No compatibility shims.** Replace superseded audit contracts and migrate
   named callers together. Preserve unrelated repository behavior.
3. **Incremental verification.** Build/test each implementation change according
   to the master plan and applicable repository instructions. MillWorks.Api builds
   require explicit authorization naming that project; documentation edits do not
   require building it.
4. **Resolve decisions before affected code.** Record choices under “Decisions
   Left to Jesse”; do not silently select unresolved paging/source-basis policies.
5. **Resolve ambiguity before implementation.** Raise a concrete unresolved scope
   or policy conflict before changing affected code. This planning review performs
   no runtime implementation or database changes.

## Current implementation and review findings


Paths below are relative to the repository root.

| Surface | Current behavior | Planning implication |
|---|---|---|
| `AuditSearchService.SearchAuditEventsAsync` | Filtered `AsNoTracking` query, unconditional `CountAsync`, then timestamp-descending `Skip`/`Take`; parses JSON after materialization | Primary shared-contract migration path |
| `AuditSearchService.SearchByEntityAsync` | Separate entity-filtered count and page implementation | Must be updated explicitly; it does not delegate to general search |
| `AuditSearchService.SearchByUserAsync` | Builds an `AuditSearchRequest` and delegates to general search | Must forward shared continuation and explicit aggregate requests |
| `AuditQueryService.GetAuditEventsAsync` | Unfiltered exact count followed by timestamp-descending offset page; emits an overall `audit.query` activity | Needs the same contract; current activity does not isolate count cost |
| `AuditService.GetAuditEvents` / `SearchAuditEvents` | Call inherited `IRepository<AuditEventEntity>.GetByOffsetAsync`, which always counts before fetching the page | The original Phase 17 file list missed this public service and its repository dependency |
| `AuditService.GetAuditEventsByDateRange` / `GetAuditEventsByUser` | Build requests and delegate to `SearchAuditEvents` | Must forward shared continuation and explicit aggregate requests too |
| Sample `Controllers/AuditController.cs` | Main list/search/date/user endpoints use `IAuditService`; entity search uses `IAuditSearchService` | Updating only the query/search services would leave the sample's main endpoints always counting |
| `AuditQueryServiceWithMetaTracking` | Forwards the list call and logs the returned item count | Must forward the shared contract and retain meta-tracking |

Additional findings:

- Phase 13's clamps are present in `AuditSearchService` and `AuditQueryService`:
  non-positive limits become 50, maximum is 1,000, and negative offsets become zero.
  **That normalization is not universal.** `AuditService` rejects non-positive
  limits, does not cap positive limits, and its repository rejects negative offsets.
  Resolve this gap before adding a `limit + 1` fetch to that surface.
- `AuditEventsResponse.TotalItems` and `TotalPages` are non-nullable `int` values.
  A request flag alone cannot represent unknown totals honestly. Returning zero
  for a skipped count would misrepresent a non-empty result set.
- All page implementations above order only by nullable `InsertedDate`. Equal
  timestamps have no unique tie-breaker, so page membership is not deterministic
  even for a static dataset. Add a unique secondary order as part of G1;
  this correctness issue is distinct from measured count cost.
- Counts and pages are separate statements with no shared snapshot guarantee.
  Concurrent inserts/deletes can make an exact count disagree with the page.
- `AuditSearchService` and `AuditService` have different filter semantics:
  the former supports `EntityType`, username/full-name matching, a minimum search
  length, and a wider wildcard search; the latter does not implement the same
  predicates. Preserve each surface's filters during this phase. Do not silently
  redirect one service through the other to share pagination code.
- Existing indexes include `(InsertedDate, EventType)`, `(EntityType, EntityId)`,
  and a filtered `(EventType, InsertedDate)` index. SQL Server's date/type index
  includes `User`. These are not evidence that every filtered count is cheap;
  wildcard searches and large offsets can still be expensive. Inspect actual
  plans before adding indexes.
- Cursor pagination already exists for a **different use case**:
  `AuditSubjectLookupService` uses bounded lookahead and a timestamp/store/ID
  cursor for tenant-scoped subject discovery. Its models live in
  `Abstractions/Models/AuditSubjectLookupModels.cs`. Use it as a local reference,
  without repurposing its cross-store contract for general event search.
- Existing unit and SQLite integration tests assert exact totals and pagination.
  `RepositoryPerformanceTests` measures other repository operations, not the
  count-versus-page cost of these service paths on large SQL Server tables.

## Shared contract and implementation gate

Implement this work through G1's authorized fixture and contract, G2's execution
ADR, and G3's assembled experience. Correctness and workload characterization are
prerequisites to performance comparisons; count cost need not be demonstrated
before honoring the selected shared paging contract.

### Request and response contract

- Use bounded, group-aware keyset continuation with a total seekable order and a
  unique tie-breaker. Define null-timestamp behavior; validate GUID comparison
  translation/order against SQL Server rather than relying on in-memory ordering.
- Bind versioned cursors to actor, tenant, filters, sort, source basis and relevant
  authorization context. Reauthorize every continuation, detail and aggregate
  request. Define invalid, expired and end-of-results behavior; tokens cannot
  widen access or imply arbitrary page jumps or exact page numbers.
- Group operations before selecting the History window. Define a cap and explicit
  continuation for an unusually large operation so one operation cannot create
  an unbounded response or silently lose constituent evidence.
- Use nullable 64-bit totals with an explicit count unit and exact/partial/estimated
  semantics where applicable. Unknown totals serialize as null even with host
  null suppression; exact empty totals are zero. Count the authorized filtered
  set in the backend using SQL aggregate types capable of 64-bit results.
- Exact totals are explicit aggregate work, not required to render a History page.
  Use bounded lookahead for continuation; exclude the lookahead entry before
  mapping/enrichment/meta-tracking and return no more than the normalized limit.
- Replace `Offset`/`CurrentPage`/`TotalPages` audit navigation semantics in affected
  callers with the selected shared continuation contract. Do not add a second
  default-exact-count offset API beside it. Final wire names and contract location
  are specified in G1's file scope.

### Query execution and consistency

1. Normalize limits consistently, closing the existing AuditService cap gap.
   Specify whether a limit counts operations or constituent records. Preserve
   cancellation and read-only behavior.
2. Apply tenant, resource and disclosure policy before joins, grouping, paging and
   aggregates. Share the normalized filters/source basis across page and summary;
   preserve documented filter behavior or record intentional changes and update
   callers/tests. A callback after unrestricted aggregation is insufficient.
3. Separate live keyset traversal from a pinned snapshot. Define behavior for
   late/backdated inserts, timestamp changes, deletion and archival; keyset order
   does not itself freeze the dataset. Do not imply count/page consistency unless
   the chosen source basis provides it.
4. Evaluate existing SQL Builder inline and canonical queued execution with
   equivalent authority, masking, warning/approval behavior and receipts. Never
   bypass a blocked or review-required query through another path. Keep libraries
   independent; the API supplies cross-domain composition.
5. Reuse the selected facade/read projection. Remove superseded audit page paths
   instead of building duplicate paging implementations. Leave shared
   `IRepository.GetByOffsetAsync` behavior unchanged for unrelated consumers.
6. Choose supporting tenant-first indexes from actual query plans and measured
   write/storage cost. The subject-lookup cursor is a reference, not the general
   evidence contract or proof that suitable indexes exist.

## Candidate file inventory for G1

Retain the existing code inventory as input to G1. Reconcile exact request/response
replacement names, new types, current in-repo and MillWorks host callers, frontend,
AI/MCP bridges and tests before implementation. Cross-repository composition files
must be added explicitly; this inventory is not yet a complete implementation
file scope. The old optional-count-only test proposal is replaced by the shared
pagination integration fixture below.

| Action | Path | Purpose |
|---|---|---|
| Edit | `src/MillWorks.AuditCore.Abstractions/Requests/AuditSearchRequest.cs` | Shared evidence request and explicit aggregate choice |
| Edit | `src/MillWorks.AuditCore.Abstractions/Responses/AuditEventsResponse.cs` | Replace offset/int page contract with shared keyset/64-bit evidence contract |
| Edit | `src/MillWorks.AuditCore.Services/IAuditSearchService.cs` | Shared authorized search/continuation contract |
| Edit | `src/MillWorks.AuditCore.Services/IAuditQueryService.cs` | Shared list/detail/continuation contract |
| Edit | `src/MillWorks.AuditCore.Services/IAuditService.cs` | Coordinated caller migration to shared contract |
| Edit | `src/MillWorks.AuditCore.Services/AuditSearchService.cs` | Authorized keyset pages, explicit aggregate and bounded lookahead |
| Edit | `src/MillWorks.AuditCore.Services/AuditQueryService.cs` | Authorized shared reads and bounded continuation |
| Edit | `src/MillWorks.AuditCore.Services/AuditService.cs` | Remove superseded offset path; shared contract and consistent limits |
| Edit | `src/MillWorks.AuditCore.Services/AuditQueryServiceWithMetaTracking.cs` | Contract forwarding and returned-item tracking |
| Edit | `samples/MillWorks.AuditCore.SampleProject/Controllers/AuditController.cs` | Migrate sample calls and forward cancellation |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Query/AuditSearchServiceTests.cs` | Search and convenience modes |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Query/AuditSearchServiceEdgeCaseTests.cs` | Filter and empty-page regressions |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Query/AuditQueryServiceTests.cs` | Query modes and normalization |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Query/AuditQueryServiceEdgeCaseTests.cs` | End-of-results and invalid/expired continuation |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/AuditQueryServiceTests.cs` | Shared query contract coverage |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/AuditServiceUnitTests.cs` | Shared query setup and coordinated caller replacement |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/AuditQueryServiceWithMetaTrackingTests.cs` | Forwarding and item counts |
| Edit | `tests/MillWorks.AuditCore.Tests/Integration/AuditServiceIntegrationTests.cs` | Public core service behavior |
| Edit | `tests/MillWorks.AuditCore.Tests/Integration/AuditQueryServiceIntegrationTests.cs` | Relational keyset and count contract |
| Edit | `tests/MillWorks.AuditCore.Tests/Integration/SearchAndQueryIntegrationTests.cs` | Filtered relational behavior |
| New | `tests/MillWorks.AuditCore.Tests/Integration/AuditEvidencePaginationIntegrationTests.cs` | No implicit count; 64-bit totals, keyset serialization and lookahead |
| Edit | `README.md` | Shared cursor/count semantics, limits and concurrency/source basis |

## Verification and acceptance criteria

- Command-level relational tests prove ordinary History pages do not issue an
  implicit full count. Explicit aggregates use the same authorized filters and
  count unit and return 64-bit totals; cover values beyond `int.MaxValue` without
  requiring a multi-billion-row fixture, then verify provider aggregate types.
- Cover zero, fewer-than-limit, exactly-limit, limit-plus-one and final windows.
  Lookahead never leaks into mapping/enrichment/meta-tracking. Unknown totals
  serialize as null under host null suppression. Large operations obey the
  selected group cap/continuation without losing evidence.
- Cover limits 0, negative, 1, maximum and `int.MaxValue`; lookahead arithmetic and
  response sizes stay bounded. Tied/null timestamps and unique IDs produce a
  deterministic order on supported relational providers.
- Verify cursor version/filter/source/actor/tenant binding, expiry and tampering;
  cross-tenant/resource attempts, permission revocation, deleted-record policy,
  masking/inference and authorized aggregates. Retained policy text alone does
  not establish current domain membership.
- Test late/backdated inserts, concurrent writes, deletion and archives against
  the selected live/snapshot semantics. Preserve coverage/freshness reporting;
  do not assert snapshot consistency merely because a cursor is used.
- Exercise each current independent surface and named caller in the inventory;
  sample and host calls use the shared contract, decorator forwards cancellation
  and records returned items, and superseded audit page APIs are removed.
- Measure page/count/enrichment and end-to-end distributions, logical reads,
  plans, memory and errors under the agreed acceptance workload. SQLite tests
  establish relational correctness, not SQL Server enterprise performance.
- README, examples, OpenAPI/generated clients and frontend/AI adapters describe
  the same continuation/count contract. Complete applicable builds/tests and
  G1–G3 acceptance receipts before declaring this work implemented.

## Decisions Left to Jesse

**Resolved by this review:** absorb Phase 17 into G1's shared contract; no parallel
optional-count offset contract. Use group-aware keyset continuation, explicit
64-bit totals, no obligatory History count and coordinated caller replacement.

Before affected implementation, settle G0's exact acceptance profile and the
remaining G1 choices: live versus pinned source basis, group-size cap and large-
operation continuation, cursor expiry/versioning, final wire shape/location,
historical authorization/disclosure and provider/index requirements. SQL Builder
execution mode and storage topology remain G2 decisions. Record each choice and
finalize the corresponding file scope before code changes.

## Out of scope and completion

This document does not authorize a generic repository redesign or choose cached
aggregate infrastructure. Reporting and summary surfaces retain their platform
owners and must use equivalent evidence/authority semantics when composed.

Phase 17 has no independent deferred offset implementation after this absorption.
Its work completes only with the shared contract/caller migration, correctness
checks, agreed-workload performance receipts and G1–G3 acceptance. The full MMP
still requires the remaining gates and every feature table row in the spike.
