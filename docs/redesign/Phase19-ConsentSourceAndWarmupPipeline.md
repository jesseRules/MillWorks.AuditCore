# Phase 19 — Consent Source And Warmup Pipeline

**Status: Deferred until needed**

Master plan context: [`../RedesignPlan.md`](../RedesignPlan.md)
Research basis: [`Phase15-ConsentStorageSpike.md`](Phase15-ConsentStorageSpike.md)

## Why this phase exists

Phase 15 established that the current architecture intentionally keeps
`IConsentVerificationService` synchronous and cache-only for the interceptor
path. That should not be replaced with a DB-querying implementation.

The missing capability is elsewhere:

- durable consent storage
- startup hydration of the local consent cache
- optional multi-node propagation

This phase captures that future work.

## Goal

If activated, add a durable consent-source pipeline that preserves the current
runtime contract:

- `HasActiveConsent(...)` stays synchronous and local
- durable consent data is loaded into the local cache before or during startup
- optional multi-instance propagation keeps nodes reasonably fresh

## Recommended architecture

### Core rule

Do not change `IConsentVerificationService` into an I/O-bound service.

Instead:

1. Keep `IConsentVerificationService` as the runtime read model.
2. Add a separate abstraction for durable consent records.
3. Hydrate/synchronize the local cache from that abstraction.

### Candidate abstractions

- `IConsentRecordStore`
- `IConsentSnapshotSource`
- `IConsentWarmupSource`

The exact name matters less than the split of responsibilities:

- **verification service:** fast sync runtime checks
- **record store/source:** async durable read/write model

## Likely implementation slices

### Slice A — Durable source abstraction

Add a new abstraction that can:

- enumerate active consents for warmup
- persist consent grants
- persist revocations

### Slice B — Cache warmup hosted service

Make `ComplianceOptions.WarmConsentCacheOnStartup` real by:

- resolving the consent source at startup
- loading active consents
- seeding `IConsentVerificationService`

### Slice C — Multi-instance propagation

Use one of:

- existing `IDistributedConsentCache`
- short-lived local cache with repeated refresh
- explicit eventing / invalidation later if needed

This should be optional and separate from the core warmup behavior.

## Key decision before implementation

Should AuditCore itself ship a default durable consent-store implementation?

### Option A — Consumer-owned only

AuditCore defines the abstraction and warmup pipeline, but consumers provide the
store implementation.

**Pros**
- smallest library scope
- avoids imposing one storage model

**Cons**
- more work for consumers
- `WarmConsentCacheOnStartup` remains abstract unless consumers wire it up

### Option B — AuditCore ships an audit-DB-backed store

AuditCore provides a default store for consumers already using the audit
database.

**Pros**
- easier adoption
- gives `WarmConsentCacheOnStartup` a concrete default path

**Cons**
- introduces a new consent table / migration decision
- expands AuditCore’s ownership into runtime consent state

## Recommendation if activated

Start with **Option A** unless a concrete consumer requires the default library
implementation.

Reason: the repository does not yet prove that every AuditCore consumer wants
consent state physically stored in the audit database. The abstraction split is
safer to standardize first than the storage implementation.

## Candidate files if activated

Exact file list depends on whether AuditCore ships a default store, but likely
includes:

| Action | Path | Purpose |
|---|---|---|
| Edit | `src/MillWorks.AuditCore.Abstractions/Interfaces/IConsentVerificationService.cs` | Clarify runtime/read-model boundary if needed |
| New | `src/MillWorks.AuditCore.Abstractions/Interfaces/IConsentRecordStore.cs` | Durable consent source abstraction |
| Edit | `src/MillWorks.AuditCore.Services/ConsentVerificationService.cs` | Accept warmup/seeding path if needed |
| Edit | `src/MillWorks.AuditCore.Services/Options/ComplianceOptions.cs` | Finalize warmup-related configuration |
| Edit | `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs` | Register warmup pipeline |
| New | `src/MillWorks.AuditCore.Services/...` | Hosted warmup service |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Compliance/...` | Verify warmup and revocation behavior |
| Edit | `README.md` | Document consent architecture |

## Activation trigger

Do not start this phase proactively.

Start it only when at least one of these becomes true:

1. FERPA enforcement must survive process restarts reliably.
2. Multi-instance regulated deployments need consistent consent behavior.
3. `WarmConsentCacheOnStartup` needs to become a real supported feature.

## Done when

This phase remains deferred until activated.

Once activated, it is done only when:

- the durable consent-source boundary is explicit
- warmup behavior is implemented and tested
- runtime synchronous verification semantics are preserved
- documentation explains the storage/freshness model clearly
