# Security Event Query Surface

**Status:** Proposed  
**Date:** 2026-06-07  
**Scope:** First-class querying for security-event normalized fields and pagination  
**Parent:** SecurityEventHardeningRoadmap.md (Workstream 3)

## Problem

Break-glass and other security investigations require filtering by `TenantId`, `ActorUserId`, `SubjectUserId`, `CorrelationId`, and `Operation`. These normalized fields were added in the break-glass implementation, but the repository and service layers only expose queries by `EventType`, `Severity`, `DateRange`, and `RelatedAuditEvent`.

Investigators currently must load all events and filter client-side, or scan opaque `DetailsJson`. That does not scale and bypasses existing indexes.

## Decision

Add repository and service methods that query by indexed normalized fields. Add bounded pagination to prevent unbounded result sets.

## Implementation

### Repository Methods

Add to `ISecurityEventRepository`:

```csharp
Task<IEnumerable<AuditSecurityEventEntity>> GetByTenantIdAsync(
    Guid tenantId,
    CancellationToken cancellationToken = default);

Task<IEnumerable<AuditSecurityEventEntity>> GetByActorUserIdAsync(
    Guid actorUserId,
    CancellationToken cancellationToken = default);

Task<IEnumerable<AuditSecurityEventEntity>> GetBySubjectUserIdAsync(
    Guid subjectUserId,
    CancellationToken cancellationToken = default);

Task<IEnumerable<AuditSecurityEventEntity>> GetByCorrelationIdAsync(
    string correlationId,
    CancellationToken cancellationToken = default);

Task<IEnumerable<AuditSecurityEventEntity>> GetByOperationAsync(
    string operation,
    CancellationToken cancellationToken = default);
```

### Composite Query Method

Add a flexible query method that combines filters:

```csharp
Task<PagedResult<AuditSecurityEventEntity>> QueryAsync(
    SecurityEventQueryFilter filter,
    CancellationToken cancellationToken = default);
```

Where `SecurityEventQueryFilter` includes:

- `TenantId?`
- `ActorUserId?`
- `SubjectUserId?`
- `CorrelationId?`
- `Operation?`
- `EventType?`
- `Severity?`
- `Status?`
- `StartDate?`
- `EndDate?`
- `PageNumber` (1-based, default 1)
- `PageSize` (default 50, max 500)

### PagedResult

Add `PagedResult<T>` to shared abstractions if not already present:

```csharp
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageNumber,
    int PageSize)
{
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasNextPage => PageNumber < TotalPages;
    public bool HasPreviousPage => PageNumber > 1;
}
```

### Service Layer

Expose `QueryAsync` through `IAuditSecurityEventService` with DTO mapping. Individual `GetByXxx` methods are optional at the service layer if composite query covers all use cases.

### DetailsJson Round-Trip

When mapping `AuditSecurityEventEntity` back to `SecurityEventDto`, deserialize `DetailsJson` into `Details` dictionary. Handle malformed JSON gracefully:

```csharp
if (!string.IsNullOrEmpty(entity.DetailsJson))
{
    try
    {
        dto.Details = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.DetailsJson)
            ?? new Dictionary<string, object?>();
    }
    catch (JsonException)
    {
        dto.Details = new Dictionary<string, object?> { ["_parseError"] = true };
    }
}
```

## Tests

1. Each `GetByXxxAsync` method returns matching records only.
2. `QueryAsync` combines multiple filters with AND logic.
3. `QueryAsync` pagination returns correct page with accurate `TotalCount`.
4. `QueryAsync` with no filters returns paginated results ordered by `DetectedAt` descending.
5. `PageSize` is clamped to max 500.
6. `DetailsJson` round-trips through mapping when valid JSON.
7. Malformed `DetailsJson` produces `_parseError` marker instead of throwing.
8. Empty `DetailsJson` produces empty `Details` dictionary.

## Non-Goals

- Full-text search on `Message` or `DetailsJson`.
- Export to external formats (that belongs in a separate export feature).
- Real-time streaming or subscription queries.
