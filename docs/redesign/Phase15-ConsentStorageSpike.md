# Phase 15 — Consent Storage Architecture Spike

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)

## Problem

`ConsentVerificationService` is currently process-local and backed only by
`IMemoryCache`:

- consent state disappears on restart
- consent state diverges across replicas
- `ComplianceOptions.WarmConsentCacheOnStartup` exists as a contract hint, but
  no warmup pipeline or backing consent source exists today

That creates a real operational gap for FERPA enforcement in multi-instance or
restart-sensitive deployments.

**Severity:** Medium

## Research outcome

The original spike assumption was too broad. After reviewing the codebase, the
key architectural constraint is already explicit:

- [`IConsentVerificationService`](../../src/MillWorks.AuditCore.Abstractions/Interfaces/IConsentVerificationService.cs)
  states that the service never queries the database.
- [`ConsentVerificationService`](../../src/MillWorks.AuditCore.Services/ConsentVerificationService.cs)
  implements synchronous cache-only reads.
- [`AuditSaveChangesInterceptor`](../../src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs)
  calls `HasActiveConsent(...)` synchronously on the save path and is written
  around that assumption.
- [`MillWorksAuditBuilder.UseCompliance`](../../src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs)
  registers the consent service as a singleton with `IMemoryCache`.
- [`IDistributedConsentCache`](../../src/MillWorks.AuditCore.Services/IDistributedConsentCache.cs)
  already exists as a forward-compatibility seam for multi-instance consent
  replication.

Because of that, a DB-backed `IConsentVerificationService` is **not** the right
default implementation direction. It would conflict with the published
interface contract and with the interceptor’s synchronous enforcement path.

## What this means

The real design question is not:

- “Should `HasActiveConsent(...)` hit the database or Redis directly?”

The real design question is:

- “How does the synchronous local consent cache get populated and kept accurate
  enough for the enforcement posture?”

That narrows the viable solution space substantially.

## Questions answered from the codebase

### Q1: What is the expected consent volume?

Unknown from the repository. No code, tests, or docs establish expected volume.

However, the current API shape implies:

- read-heavy runtime checks on the interceptor path
- comparatively infrequent writes via explicit `RecordConsentAsync(...)` /
  `RevokeConsentAsync(...)`

So the read path is the one the architecture is protecting most aggressively.

### Q2: What consistency guarantees are required?

The codebase strongly prefers:

- synchronous, non-I/O read checks on `SaveChanges`
- fail-closed behavior in `Enforce` mode when consent is absent

That means stale or cold cache is operationally significant. The existing
`WarmConsentCacheOnStartup` option description explicitly warns that a cold
cache under `Enforce` blocks FERPA saves until consent is loaded or recorded.

Inference: exact immediate cross-node consistency is not implemented today, but
cache correctness matters enough that any future design should treat this as a
compliance/operational concern, not a best-effort convenience.

### Q3: What deployment topology is assumed?

The repo does not define one universal topology, but it does show these
intended paths:

- single-process/default: `IMemoryCache`
- future multi-instance: `IDistributedConsentCache`
- possible startup hydration: `WarmConsentCacheOnStartup`

That is a stronger design signal than the original spike gave credit for.

### Q4: Should consent be queryable/auditable?

The current library does not model consent as an audit-schema entity and does
not expose consent-query APIs. FERPA validator tests look for consent **events**
in the audit log, not a first-class consent table.

Inference: AuditCore currently treats runtime consent verification and audit
evidence of consent as related but separate concerns.

## Candidate approaches after research

### Option A: Keep current in-memory-only design

**Pros**
- matches current contract exactly
- no new schema or infrastructure
- simplest implementation

**Cons**
- not durable across restarts
- unusable for reliable multi-instance enforcement
- leaves `WarmConsentCacheOnStartup` as an unfulfilled promise

**Assessment**
- acceptable only for simple/single-node or test scenarios

### Option B: Make `IConsentVerificationService` database-backed

**Pros**
- durable source of truth
- shared across replicas

**Cons**
- conflicts with the current interface documentation
- conflicts with interceptor expectation of synchronous cache-only reads
- pushes I/O into the hot save path unless heavily reworked

**Assessment**
- reject as the default direction

### Option C: Preserve cache-only reads, add a separate backing consent source

**Pros**
- preserves the current synchronous enforcement contract
- enables durable startup warmup if a store is provided
- allows explicit record/revoke flows to persist asynchronously
- keeps read-path behavior predictable

**Cons**
- requires a new abstraction for durable consent storage
- still needs a strategy for cross-node freshness after startup

**Assessment**
- best fit for the current codebase

### Option D: Preserve cache-only reads, add distributed cache replication

**Pros**
- aligns with existing `IDistributedConsentCache`
- keeps reads fast and synchronous locally
- better multi-instance behavior than memory-only

**Cons**
- distributed cache alone is not durable by itself
- does not solve restart hydration unless paired with another source or
  explicit re-recording workflow

**Assessment**
- useful as a multi-instance enhancement, but not sufficient alone for durable
  restart-safe consent

### Option E: Combined model — durable consent source + local cache + optional distributed cache

**Pros**
- durable source of truth
- synchronous local reads preserved
- startup warmup becomes meaningful
- distributed cache can help with multi-node propagation

**Cons**
- most complex option
- requires clear invalidation/freshness semantics

**Assessment**
- this is the long-term architecture if AuditCore itself chooses to own
  regulated multi-instance consent handling

## Recommendation

Do **not** implement a DB-backed `IConsentVerificationService`.

If this area is pursued, the correct direction is:

1. keep `IConsentVerificationService` synchronous and cache-only for the
   interceptor path
2. introduce a **separate durable consent source abstraction**
3. make `WarmConsentCacheOnStartup` real by hydrating the local cache from that
   source
4. optionally add `IDistributedConsentCache` implementation(s) for multi-node
   propagation

That keeps the enforcement path coherent with the current design while still
opening a path to durability.

## What I would implement if you decide to proceed later

Not in this spike, but as a follow-on phase:

1. Add a new abstraction such as `IConsentRecordStore` or
   `IConsentSnapshotSource`.
2. Keep `IConsentVerificationService` as the local synchronous runtime cache.
3. Add a hosted warmup service activated by `WarmConsentCacheOnStartup`.
4. Decide whether AuditCore should ship:
   - no store implementation, consumer-owned only, or
   - a default audit-DB-backed store for consumers already using the audit DB
5. Treat `IDistributedConsentCache` as optional replication/propagation, not as
   the only source of truth.

## Recommended follow-on phase

If this moves forward, the next phase should be framed as:

- **“Consent source and cache warmup pipeline”**

not:

- **“Database-backed `IConsentVerificationService`”**

That distinction matters because it preserves the existing interceptor and
interface contract.

## Files reviewed

| Path | Finding |
|---|---|
| `src/MillWorks.AuditCore.Abstractions/Interfaces/IConsentVerificationService.cs` | Explicitly forbids DB-query fallback on reads |
| `src/MillWorks.AuditCore.Services/ConsentVerificationService.cs` | Current implementation is local cache-only |
| `src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs` | Uses synchronous consent checks on save path |
| `src/MillWorks.AuditCore.Services/IDistributedConsentCache.cs` | Existing seam for multi-instance cache replication |
| `src/MillWorks.AuditCore.Services/Options/ComplianceOptions.cs` | `WarmConsentCacheOnStartup` already anticipates future hydration |
| `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs` | Registers singleton memory-backed consent verification today |

## Spike acceptance

- [x] Questions answered as far as the repository allows
- [x] Original recommendation revised based on actual codebase constraints
- [x] Recommended future direction identified
- [x] Follow-on phase needed before implementation work

## Out of scope

- actual durable consent-store implementation
- actual warmup hosted service
- actual distributed consent cache implementation
- GDPR consent workflows
- consent UI / consent management UX
