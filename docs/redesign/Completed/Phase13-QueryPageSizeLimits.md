# Phase 13 — Query Page Size Limits

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)

## Problem

The query/search methods in `AuditSearchService` and `AuditQueryService`
normalize non-positive `Limit`/`take` values to 50 in some paths, but never cap
maximum values. A caller can request `Limit = int.MaxValue` or another
pathological size, causing:

1. Full `CountAsync` on potentially large tables
2. Materialization of unbounded rows
3. JSON deserialization for every returned row

This is a memory/CPU amplification vector if these methods sit behind an API.

**Severity:** Medium

**References:**
- `src/MillWorks.AuditCore.Services/AuditSearchService.cs:36-44` — `SearchAuditEventsAsync`
- `src/MillWorks.AuditCore.Services/AuditSearchService.cs:116-144` — `SearchByEntityAsync`
- `src/MillWorks.AuditCore.Services/AuditQueryService.cs:92-109` — `GetAuditEventsAsync`
- `src/MillWorks.AuditCore.Services/AuditQueryService.cs:51-75` — `GetUserActivityAsync`

## Goal

Enforce a maximum page size constant across the public audit query/search
surfaces that accept caller-controlled page sizes. Requests exceeding the cap
are silently clamped (not rejected) to maintain API compatibility while
preventing amplification.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply.

## Files

| Action | Path | Purpose |
|---|---|---|
| New | `src/MillWorks.AuditCore.Services/Query/QueryLimits.cs` | Internal constants for query limits used by query/search services |
| Edit | `src/MillWorks.AuditCore.Services/AuditSearchService.cs` | Clamp Limit to max |
| Edit | `src/MillWorks.AuditCore.Services/AuditQueryService.cs` | Clamp Limit to max |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Query/AuditSearchServiceTests.cs` | Verify clamping for `SearchAuditEventsAsync` and `SearchByEntityAsync` |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Query/AuditQueryServiceTests.cs` | Verify clamping for `GetAuditEventsAsync` and `GetUserActivityAsync` |

## Design

### QueryLimits Constants

```csharp
namespace MillWorks.AuditCore.Services.Query;

internal static class QueryLimits
{
    /// <summary>
    /// Maximum number of audit records materialized by any single caller-controlled
    /// query page in AuditSearchService or AuditQueryService.
    /// </summary>
    public const int MaxPageSize = 1000;

    /// <summary>
    /// Default page size when not specified or invalid.
    /// </summary>
    public const int DefaultPageSize = 50;
}
```

### Clamping Logic

Replace:
```csharp
if (request.Limit <= 0) request.Limit = 50;
```

With:
```csharp
request.Limit = request.Limit <= 0 
    ? QueryLimits.DefaultPageSize 
    : Math.Min(request.Limit, QueryLimits.MaxPageSize);
```

Apply the same normalization pattern to:

- `AuditSearchService.SearchAuditEventsAsync`
- `AuditSearchService.SearchByEntityAsync`
- `AuditQueryService.GetAuditEventsAsync`
- `AuditQueryService.GetUserActivityAsync` (`take` parameter)

`SearchByUserAsync` already delegates to `SearchAuditEventsAsync`, so it should
inherit the same clamp without separate logic.

### Consider Count Optimization

The full `CountAsync` runs regardless of page size. For large tables this is expensive. Options:

1. **Deferred count** — only compute count if caller requests it (add `IncludeTotalCount` flag)
2. **Estimated count** — use `APPROX_COUNT_DISTINCT` or similar (SQL Server specific)
3. **Cap count** — stop counting after N+1 to determine "has more" without full scan

**Recommendation:** Defer to a future phase. Page size capping is the immediate
correctness/safety fix; count optimization and alternate pagination models are
separate behavior decisions.

## Decisions Left to Jesse

1. **MaxPageSize value.** Proposing 1000. Alternatives: 500 (more conservative), 5000 (more permissive for batch exports). **Recommendation:** 1000 balances usability with protection.

2. **Clamp vs reject.** Proposing silent clamp to max. Alternative: throw `ArgumentOutOfRangeException` if over limit. **Recommendation:** clamp — it's more forgiving for callers and existing integrations.

3. **Count optimization.** Defer to future phase or address now? **Recommendation:** defer — it's a separate concern.

## Verification

```bash
dotnet build MillWorks.AuditCore.sln
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~AuditSearchServiceTests|FullyQualifiedName~AuditQueryServiceTests"
```

### Test Cases

1. **`SearchAuditEventsAsync` clamps above max** — request with `Limit = 5000`, verify response `Limit = 1000` and returned item count does not exceed the cap.
2. **`GetAuditEventsAsync` clamps above max** — call with `limit = 5000`, verify response `Limit = 1000`.
3. **`SearchByEntityAsync` clamps above max** — call with `limit = 5000`, verify response `Limit = 1000`.
4. **`GetUserActivityAsync` clamps above max** — call with `take = 5000`, verify returned item count is capped at `1000`.
5. **Non-positive values use default** — request with `Limit = -1` or `0`, verify normalized value is `50`.
6. **Values within range are unchanged** — verify `100` stays `100` and `1000` stays `1000`.

## Out of Scope

- Count query optimization (future phase)
- Streaming/cursor-based pagination (different pattern entirely)
- Rate limiting (infrastructure concern, not library responsibility)

## Done When

- `QueryLimits.cs` exists under `MillWorks.AuditCore.Services/Query` with `MaxPageSize = 1000` and `DefaultPageSize = 50`
- `AuditSearchService.SearchAuditEventsAsync` clamps to max
- `AuditSearchService.SearchByEntityAsync` clamps to max
- `AuditQueryService.GetAuditEventsAsync` clamps to max
- `AuditQueryService.GetUserActivityAsync` clamps to max
- Existing query/search test suites cover the clamp behavior and pass
- Existing search/query tests still pass
- `dotnet build` clean
