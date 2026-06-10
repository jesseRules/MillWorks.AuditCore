# Security Event Hardening Roadmap

**Status:** Partially Implemented  
**Date:** 2026-06-07  
**Scope:** Follow-up hardening for AuditCore security-event integrity, privacy, queryability, and event pipelines

## Implementation Status

| Workstream | Status | Plan |
|------------|--------|------|
| 1. Security-Event Integrity | Proposed | [SecurityEventIntegrity.md](SecurityEventIntegrity.md) |
| 2. Hash-Only Source Metadata | **Implemented** | Done in BreakGlassSecurityEvents |
| 3. Query And Export Surface | Proposed | [SecurityEventQuerySurface.md](SecurityEventQuerySurface.md) |
| 4. Fail-Closed Pipeline Variants | Proposed (optional) | [SecurityEventApiVariants.md](SecurityEventApiVariants.md) |
| 5. Severity Policy And Alert Integration | **Implemented** | Done — severity stored/queryable, alerts via structured logging |

## Context

`SecurityEvents` currently provide durable, queryable security-event persistence. They are intentionally separate from the main audit-event hash chain, and the current recording path writes directly through the security-event repository so critical callers can fail closed.

Break-glass recovery raises the bar for these events. The first break-glass implementation should stay focused on first-class event types, normalized investigation fields, and synchronous persistence. This roadmap captures larger hardening work that should not block the first consumer unless product requirements demand it.

## Goals

- Make security-event integrity guarantees explicit and testable.
- Reduce sensitive metadata exposure while preserving investigation value.
- Improve query and export paths for operational security workflows.
- Define future pipelines without weakening fail-closed recording semantics.

## Workstreams

### 1. Security-Event Integrity

**Status:** Proposed — see [SecurityEventIntegrity.md](SecurityEventIntegrity.md)

Design a tamper-evidence model for `SecurityEvents`.

Options to evaluate:

- Add `SecurityEventIntegrity` rows with event hash, previous hash, sequence number, and optional HMAC signature.
- Include `SecurityEvents` in the existing integrity work-item pipeline with a separate chain namespace.
- Keep a separate chain for security events to avoid coupling high-volume audit events and critical security events.

Acceptance criteria:

- Append-only security-event inserts produce integrity records.
- Verification can detect mutation, deletion, and sequence gaps.
- Verification APIs/reporting distinguish audit-event integrity from security-event integrity.
- Existing `RecordEventAsync` still fails closed when durable integrity is required.

### 2. Hash-Only Source Metadata

**Status:** Implemented in BreakGlassSecurityEvents

Move security-event source metadata toward privacy-preserving fields.

Candidate changes:

- Prefer `SourceIpHash` and `UserAgentHash` for high-risk events.
- Add configurable hashing salt/key provider guidance.
- Allow callers to suppress raw `IpAddress` stamping in `RecordEventAsync`.
- Consider deprecating raw `IpAddress` on `SecurityEventDto` after consumers migrate.

Acceptance criteria:

- Break-glass events can be recorded without raw IP or full user-agent persistence.
- Hashing behavior is deterministic enough for correlation but does not leak raw values.
- Documentation tells consumers which fields are safe for regulated environments.

### 3. Query And Export Surface

**Status:** Proposed — see [SecurityEventQuerySurface.md](SecurityEventQuerySurface.md)

Add first-class querying for security operations.

Candidate changes:

- Filter by `TenantId`, `ActorUserId`, `SubjectUserId`, `CorrelationId`, `Operation`, `EventType`, `Severity`, `Status`, and date range.
- Add bounded pagination to security-event queries.
- Include normalized fields in exports and reports.
- Parse `DetailsJson` into DTO `Details` consistently, with malformed JSON handling.

Acceptance criteria:

- Break-glass investigations do not require scanning opaque JSON.
- Queries are paginated and index-backed for expected filters.
- Exported records include normalized fields and preserve safe details.

### 4. Fail-Closed Pipeline Variants

**Status:** Proposed (optional) — see [SecurityEventApiVariants.md](SecurityEventApiVariants.md)

If security-event retry, buffering, or fanout is added later, preserve a fail-closed option for critical paths.

Candidate changes:

- Add an explicit `RecordDurableEventAsync` or `RecordCriticalEventAsync` API.
- Return only after primary durable persistence and required integrity writes succeed.
- Route secondary fanout, alerts, SIEM delivery, or notifications through retryable background pipelines.
- Keep fail-open logging paths separate from break-glass and other critical security gates.

Acceptance criteria:

- Critical callers can distinguish durable persistence from queued delivery.
- Alert/fanout failures do not corrupt primary event persistence.
- Tests prove persistence failures propagate to callers on the fail-closed path.

### 5. Severity Policy And Alert Integration

**Status:** Implemented — severity stored/queryable, alerts via structured logging

Clarify ownership between AuditCore, MillWorks.Security, and product applications.

Candidate changes:

- Document default severities for common security-event types.
- Keep caller-supplied severity as the storage contract unless AuditCore owns a policy table.
- Expose structured logs or metrics with enough tags for SIEM routing.
- Consider optional hooks for alert fanout that run after durable persistence.

Acceptance criteria:

- AuditCore can store and query severity without owning every product threshold.
- Product alerting can consume durable security-event records or structured logs.
- Threshold-based escalation remains outside the persistence transaction unless explicitly required.

## Outstanding Questions

- Should `SecurityEventType` stay a closed enum, or should AuditCore support application-defined string event taxonomies?
- Should security-event integrity share the audit-event chain or use an independent chain?
- Should `SecurityEvents` become strictly append-only at the repository/API level, with resolution modeled as separate events instead of row updates?
- Should normalized security-event fields be moved into a base event metadata object shared with audit events?
