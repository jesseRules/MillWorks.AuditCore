# Security Event Append-Only Cleanup

**Status:** Scope A done (1.9.1) — Scope B proposed/deferred  
**Date:** 2026-06-28  
**Scope:** Remove the now-vestigial resolution/lifecycle surface from the append-only `AuditSecurityEventEntity`

## Background

As of 1.9.0, `AuditSecurityEventEntity` implements `IAppendOnlyEntity` and is enforced immutable by
`AppendOnlyInterceptor`. The in-place `IAuditSecurityEventService.ResolveEventAsync` mutation was
removed, and security-event triage/resolution lifecycle is owned by the application security layer
(**MillWorks.Security**), not AuditCore. See [SecurityEventHardeningRoadmap.md](SecurityEventHardeningRoadmap.md)
for the boundary decision.

Verification (2026-06-28) shows the leftovers are **not uniform** — they split into two groups of
different size and risk:

1. **Genuinely dead** — `ResolvedAt` / `ResolvedBy` / `Resolution` are never written by any path.
2. **Degenerate but still executed** — `Status` is set to `SecurityEventStatus.Open` on *every*
   insert by **three** production paths, and `GetOpenEventsAsync` is a live, severity-sorted query
   with its own repository tests. These run today; removing them deletes functioning (if currently
   trivial) capability, not dead code.

1.9.0 deliberately shipped all of this unchanged (the approved scope was "remove the
`ResolveEventAsync` mutation"). The surface below is therefore split into **Scope A** (drop the dead
fields — low risk) and **Scope B** (also retire the `Status` lifecycle — larger). Pick one.

## Scope A — remove the dead resolution fields (low risk) — ✅ DONE in 1.9.1

> Shipped in 1.9.1 (2026-06-28): `ResolvedAt`/`ResolvedBy`/`Resolution` removed from the entity, DTO,
> and mappings; migration `SecurityEventAppendOnlyCleanup` drops the three columns. `Status` retained.


| Location | Member(s) | Notes |
|----------|-----------|--------|
| `AuditSecurityEventEntity` | `ResolvedAt`, `ResolvedBy`, `Resolution` columns | never written post-insert |
| `SecurityEventDto` | `ResolvedAt`, `ResolvedBy`, `Resolution` | no AuditCore producer; no MillWorks reader (confirmed 2026-06-28) |
| `AuditSecurityEventMappings` | the `Resolved*` copy lines (entity→DTO and DTO→entity) | follows entity/DTO |
| Migration + model snapshot | drop the three columns | see Migration & model snapshot |

## Scope B — also retire the `Status` lifecycle (larger; eliminates the status concept)

Everything in Scope A, **plus**:

| Location | Member(s) | Notes |
|----------|-----------|--------|
| `AuditSecurityEventEntity` | `Status` property + `IX_SecurityEvents_Status` index attribute | always `Open` |
| `SecurityEventDto` | `Status` | |
| `AuditSecurityEventMappings` | the `Status` copy lines | |
| `AuditSecurityEventService.RecordEventAsync` (`:45`) | `entity.Status = SecurityEventStatus.Open;` | **Status write-site #1** |
| `AuditSaveChangesInterceptor.AddComplianceSecurityEvent` (`:455`) | `Status = SecurityEventStatus.Open` on the compliance `AuditSecurityEventEntity` it creates | **Status write-site #2 — breaks the build if `Status`/enum are removed without updating this** |
| `IntegrityReconciliationService` (`:147`) | `Status = SecurityEventStatus.Open` initializer | **Status write-site #3** |
| `IAuditSecurityEventService` + `AuditSecurityEventService` (`:130`) + `ISecurityEventRepository` (`:36`) + `SecurityEventRepository` (`:56`) | `GetOpenEventsAsync` (and the repo's `Status == Open \|\| Investigating`, severity-sorted query) | meaning gone once `Status` is removed |
| `SecurityEventStatus` enum | the enum itself | no references outside the rows above in AuditCore, and **none in MillWorks** (confirmed 2026-06-28) |
| Migration + model snapshot | drop the `Status` column + `IX_SecurityEvents_Status` index | see Migration & model snapshot |

## Migration & model snapshot

Run `dotnet ef migrations add SecurityEventAppendOnlyCleanup` (schema-only; no data backfill —
greenfield DB, seed/test data only). The migration drops:

- **Scope A:** `SecurityEvents` columns `ResolvedAt`, `ResolvedBy`, `Resolution`.
- **Scope B (additional):** the `Status` column and the `IX_SecurityEvents_Status` index.

Then regenerate and verify `src/MillWorks.AuditCore.EntityFramework/Migrations/AuditDbContextModelSnapshot.cs`:
`Status` is currently in the snapshot at `:809` and its index at `:848`; the `Resolved*` columns are
also present. After the migration, confirm **no** `Status` / `ResolvedAt` / `ResolvedBy` /
`Resolution` property and **no** `IX_SecurityEvents_Status` index remain in the snapshot (the scope
chosen determines exactly which must be gone).

## Tests to update

**Scope A:**
- `AuditSecurityEventServiceTests` — drop the `Resolved*` assertions; keep `AuditSecurityEventEntity_IsAppendOnly`.
- `SecurityEventIntegrationTests` — remove the `Resolved*` assertions from the record/round-trip tests.
- `Mapping/AuditMappingTests` — drop the `Resolved*` round-trip assertions.

**Scope B (additional):**
- `AuditSecurityEventServiceTests` — remove `GetOpenEventsAsync_DelegatesToRepository` (`:191`) and the `Status` assertion(s) (e.g. `:95`).
- `Repositories/SecurityEventRepositoryTests` — remove the entire `GetOpenEventsAsync` region (`:81`) and the `Status`-seeded helper (`SeedSecurityEvent(..., SecurityEventStatus status, ...)` at `:205`), reworking the remaining seed calls to drop the `status` argument.
- `EntityFramework/FerpaEnforcementTests` (`:198`) — remove the `securityEvent.Status == SecurityEventStatus.Open` assertion on the compliance event.
- `Mapping/AuditMappingTests` (`:213`, `:222`) — drop the `Status` round-trip assertion.
- `SecurityEventIntegrationTests` — remove the `Status = SecurityEventStatus.Open` initializers throughout.

## Docs

- README — the schema table and *Append-Only Enforcement* section reference `SecurityEvents`; for
  Scope B, drop the `Status` lifecycle language.
- `CHANGELOG.md` entry under the release that lands this.

## Versioning / breaking impact

Removing public `SecurityEventDto` properties (both scopes), plus an interface method
(`GetOpenEventsAsync`) and the `SecurityEventStatus` enum (Scope B), is a **breaking API change** →
major bump. Cross-repo scan 2026-06-28: the only AuditCore security-event API MillWorks consumes is
`RecordEventAsync` (via MillWorks.Api); no MillWorks caller of `GetOpenEventsAsync` and no reader of
`SecurityEventDto.Status`/`Resolved*` or reference to `SecurityEventStatus` was found. **Re-verify at
implementation time** before removing.

## When To Implement

Bundle with the next intentional breaking release, or whenever the `SecurityEvents` schema is next
revised. Not urgent — the dead columns are inert and `Status` is harmless-if-degenerate.
