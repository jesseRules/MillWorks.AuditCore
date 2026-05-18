# Phase 17 — Query Count And Pagination Enhancements

**Status: Deferred until needed**

Master plan context: [`../RedesignPlan.md`](../RedesignPlan.md)
Related immediate fix: [`Phase13-QueryPageSizeLimits.md`](Phase13-QueryPageSizeLimits.md)

## Why this phase exists

Phase 13 addresses the immediate safety issue: unbounded caller-controlled page
sizes in `AuditSearchService` and `AuditQueryService`.

That phase intentionally does **not** change how total counts are computed or
how pagination is modeled. Today, the services still execute full
`CountAsync(...)` queries to populate `AuditEventsResponse.TotalItems` and
`TotalPages`.

That is acceptable for the current contract, but it may become too expensive
for very large audit tables or API-heavy deployments.

## Problem

Even after page-size clamping, the query services still have these potential
costs:

1. `CountAsync(...)` scans can remain expensive on large filtered result sets.
2. Offset-based pagination degrades as offsets grow.
3. The response contract assumes exact totals even when a caller may only need
   "has more" semantics.

These are performance and API-shape concerns, not correctness bugs.

## Goal

If this phase is activated, improve large-table query efficiency by choosing a
clear pagination/count contract instead of always computing exact totals with
offset pagination.

## Why this is deferred

The current library contract returns `TotalItems`, `TotalPages`, `Offset`, and
`CurrentPage`. Changing that behavior is more invasive than Phase 13's clamp
fix because it can affect callers, tests, and documentation.

There is no evidence in the current repository that these services are yet
failing because of count-query cost. Until that becomes a real problem, the
simpler contract should remain.

## Candidate directions when activated

### Option A — Keep exact totals, optimize selectively

- Preserve the current response shape.
- Optimize only the count/query execution plan where possible.
- Lowest API risk, but limited upside.

### Option B — Add optional exact counts

- Introduce a request flag such as `IncludeTotalCount`.
- Skip full `CountAsync(...)` when callers do not need totals.
- Return enough metadata for paging without forcing exact counts every time.

### Option C — Add cursor/keyset pagination

- Keep offset pagination for compatibility.
- Add a new query mode for large-table traversal.
- Best long-term scale characteristics, but a larger API and documentation
  change.

## Recommendation if this phase is ever started

Start with **Option B**:

1. Add an opt-in `IncludeTotalCount` flag.
2. Preserve the current default behavior until a caller adopts the new mode.
3. Defer cursor pagination unless a real consumer needs deep traversal across
   large audit tables.

That gives meaningful performance relief without forcing an immediate API
break.

## Decisions left to Jesse when activated

1. **API compatibility.** Must `AuditEventsResponse.TotalItems` remain exact on
   every call, or can it become optional?
2. **Caller needs.** Do current consumers actually use exact totals and total
   pages, or do they only need next/previous navigation?
3. **Pagination model.** Is offset pagination sufficient, or is keyset/cursor
   pagination needed for audit-table scale?
4. **Provider specificity.** Are SQL Server-specific optimizations acceptable,
   or must behavior stay provider-agnostic?

## Candidate files if activated

Exact file list depends on the chosen direction, but likely includes:

| Action | Path | Purpose |
|---|---|---|
| Edit | `src/MillWorks.AuditCore.Services/AuditSearchService.cs` | Adjust count/pagination behavior |
| Edit | `src/MillWorks.AuditCore.Services/AuditQueryService.cs` | Adjust count/pagination behavior |
| Edit | `src/MillWorks.AuditCore.Abstractions/Requests/AuditSearchRequest.cs` | Add optional count/pagination flags if needed |
| Edit | `src/MillWorks.AuditCore.Abstractions/Responses/AuditEventsResponse.cs` | Adjust response contract if needed |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Query/AuditSearchServiceTests.cs` | Verify chosen behavior |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Query/AuditQueryServiceTests.cs` | Verify chosen behavior |
| Edit | `README.md` | Document the query contract |

## Activation trigger

Do not start this phase proactively.

Start it only when at least one of these becomes true:

1. Query endpoints show measurable count-query cost in production or soak runs.
2. Consumers need high-offset traversal over very large audit tables.
3. API callers do not actually need exact totals, making the current cost wasteful.

## Done when

This phase remains deferred until activated.

Once activated, it is done only when the chosen count/pagination contract is
explicit, implemented, tested, and documented.
