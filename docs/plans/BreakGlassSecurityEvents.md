# Break-Glass Security Events

**Status:** Proposed  
**Date:** 2026-06-06  
**Consumer:** MillWorks.Identity network-policy break-glass recovery  
**Scope:** MillWorks.AuditCore security-event model, normalized investigation fields, and fail-closed recording path

## Problem

MillWorks.Identity is adding a SuperAdmin emergency recovery flow for tenant network-policy lockouts. The flow must create durable security events before issuing a break-glass grant, consuming a grant, or mutating tenant network policy under recovery authority.

AuditCore already exposes `IAuditSecurityEventService.RecordEventAsync(SecurityEventDto, ct)`, which writes synchronously through the AuditCore security-event repository and propagates persistence failures. That is the right durability primitive. However, the current `SecurityEventType` enum is generic (`PrivilegeEscalation`, `SuspiciousActivity`, etc.), so break-glass events would otherwise be hidden in `Details` instead of being first-class queryable security events.

Because AuditCore has no production consumers yet, prefer the cleaner model now.

Current `SecurityEvents` rows are not part of the main audit hash chain. This plan must not describe them as tamper-evident until a dedicated integrity design is implemented. Follow-up hardening is tracked in `SecurityEventHardeningRoadmap.md`.

## Decision

Add first-class break-glass security-event support to AuditCore while keeping `RecordEventAsync` synchronous and exception-propagating.

Add the normalized fields required for the first consumer now, because break-glass investigations need tenant, actor, subject, operation, and correlation filters. Do not force these into opaque JSON details.

Identity should still not reference AuditCore directly. MillWorks.Api will bridge Identity's local break-glass audit abstraction to `IAuditSecurityEventService`.

## Event Types

Add these values to `SecurityEventType`:

- `BreakGlassAttempt`
- `BreakGlassDenied`
- `BreakGlassChallengeIssued`
- `BreakGlassChallengeFailed`
- `BreakGlassGranted`
- `BreakGlassConsumed`
- `BreakGlassExpired`
- `BreakGlassRevoked`
- `BreakGlassPolicyChanged`
- `BreakGlassEnrollmentChanged`

These names are intentionally domain-neutral enough for other emergency-access scenarios beyond network-policy recovery.

## Normalized Fields

Add optional columns/properties to `SecurityEventDto` and `AuditSecurityEventEntity`:

- `CorrelationId`
- `TenantId`
- `ActorUserId`
- `SubjectUserId`
- `SourceIpHash`
- `UserAgentHash`
- `Operation`

Index at least the fields needed by the first consumer's operational queries:

- `TenantId`
- `ActorUserId`
- `SubjectUserId`
- `CorrelationId`
- `Operation`

Keep these fields nullable so existing and non-break-glass security events remain valid.

Keep event-specific context in `Details`, including:

- `BreakGlassGrantId`
- `ChallengeId`
- `GrantTtlSeconds`
- `BlockedReason`
- `ResolvedCountryCode`
- `PolicyBeforeHash`
- `PolicyAfterHash`
- `AssuranceMethod`
- `IdentityAppSecurityEventType`

Do not store raw secrets, raw grant material, raw recovery codes, raw passkey challenge material, or full user-agent strings.

For break-glass events, prefer `SourceIpHash` over raw `IpAddress`. The current service stamps raw `auditContext.IpAddress`; the implementation must avoid overwriting a caller-provided hash-only event with raw IP data. If raw `IpAddress` remains for existing security-event callers, break-glass bridge code must be able to suppress it.

## Persistence Semantics

`IAuditSecurityEventService.RecordEventAsync` must remain suitable as a fail-closed gate:

- It persists synchronously.
- It propagates repository/database failures to the caller.
- It does not route this path through a fail-open retry/dead-letter abstraction before returning success.
- It keeps direct security-event persistence out of the EF audit interceptor recursion path.

If retry or dead-letter support is later added for security events, keep a separate fail-closed variant or return only after durable persistence is confirmed.

`RecordEventAsync` persists through AuditCore's security-event repository immediately. That means break-glass event persistence and the later Identity grant/policy mutation are not automatically atomic with each other. Identity should model events precisely:

- Precondition events: attempt, denial, challenge issued, challenge failed.
- Post-persistence outcome events: granted, consumed, policy changed, enrollment changed.
- Compensating failure events if an Identity mutation fails after a precondition event was recorded.

## Alerting

AuditCore v1.0 alert delivery is structured logging. That is acceptable for this phase.

MillWorks.Security can continue to own product alert fanout, `RequiresAlert`, notification routing, and SIEM-specific rules. AuditCore owns durable persistence and queryable security-event history.

Recommended severity defaults:

- `BreakGlassAttempt`: Medium
- `BreakGlassDenied`: Medium, escalate by threshold in consumer
- `BreakGlassChallengeIssued`: Medium
- `BreakGlassChallengeFailed`: High after threshold, otherwise Medium
- `BreakGlassGranted`: Critical
- `BreakGlassConsumed`: Critical
- `BreakGlassExpired`: Low
- `BreakGlassRevoked`: High
- `BreakGlassPolicyChanged`: Critical
- `BreakGlassEnrollmentChanged`: High

AuditCore does not need to hard-code every consumer's severity policy if that remains caller-supplied, but tests should prove these severities can be stored and queried correctly.

## Implementation Outline

1. Add break-glass values to `SecurityEventType`.
2. Add normalized investigation fields:
   - `SecurityEventDto`
   - `AuditSecurityEventEntity`
   - EF configuration/model snapshot/migration
   - Mapster mapping configuration
   - repository/query/export paths if they expose security-event fields
3. Add parsing for `DetailsJson` when mapping `AuditSecurityEventEntity` back to `SecurityEventDto`, or explicitly document that service DTO reads do not round-trip `Details`.
4. Keep `RecordEventAsync` exception-propagating on persistence failure.
5. Ensure break-glass callers can use hash-only source metadata without `RecordEventAsync` stamping raw IP data onto those events.
6. Add tests for:
   - Each new event type maps and persists.
   - Critical break-glass events are returned by `GetCriticalEventsAsync`.
   - Details JSON size guards still produce valid JSON.
   - Repository `AddAsync` / `SaveChangesAsync` failures propagate to the caller.
   - Normalized fields round-trip when present.
   - Break-glass hash-only metadata does not persist raw IP/user-agent values.
   - `Details` round-trip behavior is either implemented or intentionally documented.
7. Update README security-event documentation and table descriptions.

Adding enum values alone does not require an EF migration because `SecurityEventType` is currently stored as an `int`. The migration is required for the new normalized columns and indexes.

## Integration Contract For MillWorks.Identity

MillWorks.Api should implement an Identity-local interface such as `IIdentityBreakGlassAuditSink` by calling `IAuditSecurityEventService.RecordEventAsync`.

The bridge should:

- Map Identity break-glass events to the new AuditCore `SecurityEventType` values.
- Populate normalized fields where available.
- Put Identity-specific event ids/names in `Details`.
- Throw if AuditCore persistence fails.
- Record precondition events before break-glass grant creation, grant consumption, and recovery policy mutation are committed.
- Record outcome events after the corresponding Identity mutation has durably succeeded, or record a compensating failure event when the mutation fails after a precondition event.

## Non-Goals

- Do not make Identity reference AuditCore packages directly.
- Do not replace MillWorks.Security product alerting in this plan.
- Do not add a generic fail-open audit logger path for break-glass.
- Do not store sensitive recovery material in security-event details.
- Do not claim `SecurityEvents` are tamper-evident until security-event integrity hardening is implemented.

## Open Questions

- Should `SecurityEventType` remain a closed enum, or should AuditCore support a string event taxonomy for application-defined event types?
- Should AuditCore expose a dedicated `RecordCriticalEventAsync` helper that documents fail-closed expectations more clearly than the generic `RecordEventAsync` name?
- Should raw `IpAddress` remain on `SecurityEventDto` long-term, or should it be deprecated in favor of `SourceIpHash`?
