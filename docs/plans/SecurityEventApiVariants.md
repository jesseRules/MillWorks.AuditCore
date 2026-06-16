# Security Event API Variants

**Status:** Proposed  
**Date:** 2026-06-07  
**Scope:** Explicit API differentiation for fail-closed vs buffered security-event recording  
**Parent:** SecurityEventHardeningRoadmap.md (Workstream 4)

## Problem

`IAuditSecurityEventService.RecordEventAsync` is currently fail-closed: it persists synchronously and propagates exceptions. This is correct for break-glass and other critical paths where the caller must know persistence succeeded before proceeding.

If AuditCore later adds retry, dead-letter, or async fanout for security events (e.g., SIEM delivery, alert notifications), using the same `RecordEventAsync` name risks ambiguity. Callers may not realize whether their call is fail-closed or fail-open.

## Decision

When a second recording path is added, introduce explicit API variants:

- `RecordCriticalEventAsync` — Fail-closed. Returns only after durable persistence succeeds. Throws on failure.
- `RecordEventAsync` — Remains fail-closed for backward compatibility.
- `EnqueueEventAsync` (future) — Fail-open. Enqueues for reliable delivery but returns before persistence confirmation.

This plan is optional and should be implemented only when a non-fail-closed path is actually needed.

## Current State

`RecordEventAsync` today:

1. Maps DTO to entity
2. Stamps metadata
3. Calls `repository.AddAsync` + `SaveChangesAsync`
4. Returns mapped DTO or throws

This is already fail-closed. No changes needed until a second path exists.

## Future Implementation

### RecordCriticalEventAsync

Explicit fail-closed variant:

```csharp
/// <summary>
/// Records a security event with fail-closed semantics.
/// Returns only after durable persistence (and integrity, if enabled) succeeds.
/// Throws on any persistence failure — callers should NOT proceed if this fails.
/// </summary>
Task<SecurityEventDto> RecordCriticalEventAsync(
    SecurityEventDto securityEvent,
    CancellationToken cancellationToken = default);
```

Implementation identical to current `RecordEventAsync`. The name documents intent.

### EnqueueEventAsync (Future)

Fail-open variant for non-critical paths:

```csharp
/// <summary>
/// Enqueues a security event for reliable delivery.
/// Returns immediately after enqueueing — does NOT guarantee persistence.
/// Use for non-critical events where caller does not need confirmation.
/// </summary>
Task EnqueueEventAsync(
    SecurityEventDto securityEvent,
    CancellationToken cancellationToken = default);
```

Would write to an outbox table or message queue, with background processing for:

- Retry on transient failures
- Dead-letter on permanent failures
- Fanout to SIEM, alerts, notifications

### Migration Path

1. Add `RecordCriticalEventAsync` as alias for current behavior.
2. Keep `RecordEventAsync` with same semantics (no breaking change).
3. Add `EnqueueEventAsync` when buffered delivery is needed.
4. Document which method to use in which scenario.

## When To Implement

Implement this plan when:

- A consumer needs fire-and-forget security-event recording.
- SIEM/alert fanout is added and should not block the caller.
- Retry/dead-letter infrastructure is built for security events.

Do not implement preemptively. The current single fail-closed path is correct for all known consumers.

## Tests

1. `RecordCriticalEventAsync` propagates repository exceptions.
2. `RecordCriticalEventAsync` returns only after `SaveChangesAsync` completes.
3. `EnqueueEventAsync` returns before persistence (when implemented).
4. `EnqueueEventAsync` failures do not throw to caller (when implemented).
5. Enqueued events are eventually persisted by background processor.
6. Dead-lettered events are queryable for investigation.

## Non-Goals

- Changing current `RecordEventAsync` semantics.
- Adding buffered delivery before a consumer needs it.
- Real-time streaming (separate concern).
