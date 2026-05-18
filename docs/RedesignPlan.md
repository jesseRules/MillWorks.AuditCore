# Plan — AuditCore Sink Redesign

**Completed 2026-04-26** — All 11 phases implemented. See `docs/redesign/Phase11-VerificationLog.md` for verification results.

This document is the master orchestration for restructuring `MillWorks.AuditCore`
from an interceptor-coupled audit library into a sink-based audit subsystem.

## Why this exists

The current design assumes a single saving DbContext (`AuditApplicationDbContext`)
that owns audit tables, audit-row construction, and chain persistence. That
assumption broke when nine MillWorks consumer libraries (`Identity`,
`DataProcessing`, `Notification`, `SqlBuilder`, `Document`, `Media`, `Git`,
`Ai`, `Compliance`) registered the interceptor on **their** DbContexts. Each
consumer was forced to map `AuditLogEntity` into its own model as a workaround
for `AuditSaveChangesInterceptor.GetAuditableEntries`'s early-return on missing
`AuditLogEntity`. The Compliance code carries the workaround comment in
`ComplianceDbContext.cs:93-98`, citing the AuditCore line number it works around.

`docs/ConsumerDbContextAuditing.md` (now superseded) proposed patching the
interceptor to make the workaround pattern first-class. That plan is correct
about the symptom but treats the wrong layer:

- Consumer libraries shouldn't need to know about AuditCore entities at all —
  it violates MillWorks's own architectural rule "a library never imports
  another library" (`MillWorks/README.md:332`).
- The interceptor doing both "build audit data" and "persist audit data"
  conflates two concerns that should be separable. Items 01-02 of the
  superseded plan extend the conflation rather than break it.
- AuditCore is greenfield (one integrated app — MillWorks — and two more
  about to integrate). This is the smallest blast radius the redesign will
  ever have. Doing it later means rewriting code that three apps and nine
  libraries depend on.

The redesign introduces an `IAuditSink` abstraction that owns persistence.
The interceptor becomes a producer of `AuditEnvelope` objects; the sink decides
where and when to commit them. This matches MillWorks's existing
local-abstraction + bridge pattern, lets consumer libraries depend only on
`MillWorks.AuditCore.Abstractions`, and removes every concern that motivated
the superseded plan.

## What "done" looks like

- `MillWorks.AuditCore.Abstractions` exposes `IAuditSink` and `AuditEnvelope`.
  Consumer libraries can opt into audit by depending on Abstractions only.
- `AuditSaveChangesInterceptor` no longer touches the saving DbContext's
  `DbSet<AuditLogEntity>`. It builds envelopes and publishes via `IAuditSink`.
- A default `ImmediateSink` writes synchronously through an audit-owned
  DbContext (`AuditDbContext`). A `TransactionalOutboxSink` is available for
  consumers that need audit writes to share the saving transaction.
- Consumer DbContexts (`ComplianceDbContext`, etc.) no longer map
  `AuditLogEntity` in `OnModelCreating`. Most consumer libraries narrow
  their package reference to `MillWorks.AuditCore.Abstractions` only.
  Compliance is the standing exception — it keeps the
  `MillWorks.AuditCore.EntityFramework` reference because
  `[EncryptedField]` + `EncryptedValueConverter` + `UseFieldEncryption`
  remain in the EF package (EF value-converter coupling, deliberately
  not lifted in this redesign).
- `MillWorks.Api`'s `AuditBridge` (already exists at
  `Bridge/Audit/AuditBridge.cs`, today implements only
  `IFinanceAuditService`) is extended to expose `IAuditPublisher` and
  routes through `IAuditSink`. The interceptor is registered on consumer
  DbContexts by Api, not by the consumer libraries themselves.
- The hash chain covers every audit row, regardless of whether it originated
  from interceptor capture or explicit `IAuditLogger.LogAsync` calls.
  `FailClosedForRegulated` works for any consumer DbContext.
- `MillWorks.AuditCore/README.md` documents the new contract accurately.
  `MillWorks/README.md` adds the `AuditBridge` row to the Bridge Taxonomy
  table and removes the stale "audit logging" mention from the
  `SecurityBridge` row.
- The two new apps about to integrate AuditCore use the new sink contract
  from day one — they never see the legacy interceptor-coupled surface.

## Architecture target

```
┌─────────────────────────────────────────────────────────────────────┐
│ Consumer library (Compliance, Identity, ...)                        │
│   - References MillWorks.AuditCore.Abstractions (and EF only when   │
│     it uses [EncryptedField]/UseFieldEncryption — Compliance only)  │
│   - Defines local abstraction I{Library}AuditPublisher              │
│   - Service layer calls I{Library}AuditPublisher.PublishAsync       │
│     OR DbContext has interceptor registered (by Api, not library)   │
└─────────────────────┬───────────────────────────────────────────────┘
                      │
                      │ AuditEnvelope
                      ▼
┌─────────────────────────────────────────────────────────────────────┐
│ MillWorks.Api / AuditBridge   (scoped lifetime)                     │
│   - Implements every I{Library}AuditPublisher                       │
│   - Forwards to IAuditSink (resolved per request scope)             │
│   - Registers AuditSaveChangesInterceptor on consumer DbContexts    │
└─────────────────────┬───────────────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────────────┐
│ MillWorks.AuditCore.AspNetCore / Services                           │
│   IAuditSink (one of:)                                              │
│   ├── ImmediateSink (default)                                       │
│   │     - Builds AuditLogEntity / AuditEventEntity / AuditIntegrity │
│   │     - Writes directly to AuditDbContext (its own connection)    │
│   │     - Chain construction lives here                             │
│   └── TransactionalOutboxSink (opt-in)                              │
│         - Writes AuditOutboxEntity in saving DbContext's txn        │
│         - Background drainer commits to AuditDbContext              │
└─────────────────────┬───────────────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────────────┐
│ MillWorks.AuditCore.EntityFramework / AuditDbContext                │
│   - Owns audit schema (tables: AuditLogs, AuditEvents,              │
│     AuditIntegrity, SecurityEvents, ArchiveRecords, IntegrityWork,  │
│     AuditOutbox)                                                    │
│   - Owns its own connection (may differ from consumer connection)   │
│   - Owns its migrations                                             │
└─────────────────────────────────────────────────────────────────────┘
```

The interceptor still exists. It still auto-captures entity changes. But it
is now a **producer**, not a persister. The split makes every architectural
debt from the old design dissolve: cross-context coupling, single-DB
assumption, transaction coupling, chain-lock contention inside business
transactions, the duplicate AuditLog/AuditEvent persistence paths.

## Hard constraints (apply to every phase)

These are the binding rules from `feedback_plan_is_spec.md` and
`feedback_greenfield_no_back_compat.md`. Every phase doc restates them in its
"Constraints" section.

1. **Plan is the spec.** Each phase doc names the files that change. Do not
   touch files outside that list, even to improve them. No new helper classes
   unless the phase doc names them.
2. **No backwards-compat shims.** AuditCore is pre-release. Delete obsolete
   types; do not add `[Obsolete]` forwarders. Do not preserve removed
   constructor parameters or namespaces.
3. **Build and test after every file change.** Do not batch multi-file
   rewrites before the first `dotnet build` / `dotnet test` cycle.
4. **List unresolved decisions before editing.** Each phase has a "Decisions
   Left to Jesse" section; if the phase leaves a fork, raise it before
   touching code.
5. **Ambiguity is a stop, not a permission.** Ask Jesse rather than infer.

The only standing exception: built-in EF migrations remain anchored to the
default `audit` schema (per the existing greenfield carve-out).

## Phase orchestration

Phases land in order. Each phase ends with a green build and a green test
run before the next phase starts. Cross-repo phases (08, parts of 09) need
explicit Jesse sign-off because they touch `/Users/jesse/RiderProjects/MillWorks/`.

| # | Phase | Doc | Repo touched | Risk | Sessions |
|---|---|---|---|---|---|
| 01 | IAuditSink abstraction | [`Phase01-AuditSinkAbstraction.md`](redesign/Phase01-AuditSinkAbstraction.md) | AuditCore | low (additive) | 1 |
| 02 | Default ImmediateSink | [`Phase02-DefaultImmediateSink.md`](redesign/Phase02-DefaultImmediateSink.md) | AuditCore | low (additive) | 1 |
| 03 | Interceptor → IAuditSink | [`Phase03-InterceptorRefactor.md`](redesign/Phase03-InterceptorRefactor.md) | AuditCore | medium (hot path) | 1 |
| 04 | IAuditContextSource | [`Phase04-AuditContextSource.md`](redesign/Phase04-AuditContextSource.md) | AuditCore | low (additive interface) | 1 |
| 04.5 | Lift marker attributes to Abstractions | [`Phase04-5-AttributesToAbstractions.md`](redesign/Phase04-5-AttributesToAbstractions.md) | AuditCore | low (mechanical move) | 1 |
| 05 | AuditDbContext separation | [`Phase05-AuditDbContextSeparation.md`](redesign/Phase05-AuditDbContextSeparation.md) | AuditCore | medium (rename + isolation) | 1 |
| 06 | Transactional outbox sink | [`Phase06-OutboxSink.md`](redesign/Phase06-OutboxSink.md) | AuditCore | high (new entity, txn semantics) | 1 |
| 07 | Drop AuditLogEntity coupling | [`Phase07-DropEntityCoupling.md`](redesign/Phase07-DropEntityCoupling.md) | AuditCore | medium (interceptor contract change) | 1 |
| 08 | MillWorks.Api bridge wiring | [`Phase08-ApiBridgeWiring.md`](redesign/Phase08-ApiBridgeWiring.md) | MillWorks (Api + DataProcessing) | high (cross-repo) | 1 |
| 09 | Consumer library migrations | [`Phase09-ConsumerLibraryMigrations.md`](redesign/Phase09-ConsumerLibraryMigrations.md) | MillWorks (9 libs) | varies per library | 1-3 |
| 10 | README & docs rewrite | [`Phase10-DocsRewrite.md`](redesign/Phase10-DocsRewrite.md) | AuditCore + MillWorks | low (docs only) | 1 |
| 11 | Verification & soak | [`Phase11-Verification.md`](redesign/Phase11-Verification.md) | AuditCore + MillWorks | low | 1 |

**Total:** 12 master phases, ~13 sessions (Phase 09 may split into 09a/09b/09c per its own decision section).

## Per-phase intent (one paragraph each)

### Phase 01 — IAuditSink abstraction
Add `IAuditSink`, `AuditEnvelope`, and the discriminator enum to
`MillWorks.AuditCore.Abstractions`. No implementations yet. Net effect:
new types compile; nothing else changes. Phase ends with `dotnet build` clean
and one unit test that constructs an envelope.

### Phase 02 — Default ImmediateSink
Implement `ImmediateSink : IAuditSink` in `MillWorks.AuditCore.Services`.
For now it routes envelopes to the existing `IAuditLogger` path
(`EntityChange` → `AuditLogEntity` write through current logic;
`ExplicitEvent` → `IAuditLogger.LogAsync`). DI registers it as the default
`IAuditSink` binding. Net effect: a parallel sink path exists; nothing
currently calls it.

### Phase 03 — Interceptor → IAuditSink refactor
`AuditSaveChangesInterceptor` stops calling
`context.Set<AuditLogEntity>().Add(...)`. It builds `AuditEnvelope` objects
and calls `IAuditSink.PublishAsync` instead. Persistence logic moves out of
the interceptor into `ImmediateSink`. Net behavior unchanged for callers.
Existing interceptor tests still pass.

### Phase 04 — IAuditContextSource
Add `IAuditContextSource` to Abstractions: a small interface a consumer
DbContext can implement to expose `CurrentUserId`, `CurrentCorrelationId`,
`CurrentIpAddress`, `CurrentUserAgent`. Interceptor and sink read context
via this interface instead of casting to `AuditApplicationDbContext`.
Net effect: consumer contexts can now feed user/correlation context into
audit envelopes through a public, documented contract.

### Phase 04.5 — Lift marker attributes to Abstractions
Move `[PHI]`, `[FERPA]`, `[SensitiveData]`, `[NoAudit]` from
`MillWorks.AuditCore.EntityFramework.Attributes` to
`MillWorks.AuditCore.Abstractions.Attributes`. `[EncryptedField]` stays
in EF (coupled to `EncryptedValueConverter`). Without this lift, 6 of
the 9 consumer libraries would still need `MillWorks.AuditCore.EntityFramework`
solely to import marker attributes that have nothing to do with EF —
which would defeat the Phase 09 dependency-narrowing goal. Added after
the codebase survey on 2026-04-25.

### Phase 05 — AuditDbContext separation
Rename `AuditApplicationDbContext` → `AuditDbContext` (the "Application"
suffix was a vestige of the single-DbContext era). The sink takes its own
scoped `AuditDbContext` rather than reusing whatever DbContext is currently
saving. Net behavior change: audit writes happen on the audit-owned
DbContext / connection, decoupled from the consumer's transaction. Strict
mode (audit-and-business-share-txn) becomes opt-in via the outbox sink in
Phase 06.

### Phase 06 — Transactional outbox sink
For consumers that need audit failures to roll back business writes (the
`FailClosedForRegulated` posture), add `TransactionalOutboxSink`. It writes
an `AuditOutboxEntity` row inside the saving consumer's transaction. A
background `AuditOutboxDrainer` reads outbox rows and commits them through
`ImmediateSink` to the audit DbContext. New `AuditOutboxEntity` table.
DI option `AuditSinkMode.TransactionalOutbox` selects it.

### Phase 07 — Drop AuditLogEntity coupling
Remove the early-return in `GetAuditableEntries` on missing `AuditLogEntity`.
With the sink owning persistence, the interceptor no longer needs the
saving DbContext to map AuditCore entities. Consumer DbContexts that have
been mapping `AuditLogEntity` inline can stop. Add an integration test that
proves a bare consumer-style `DbContext` (zero AuditCore entities mapped)
still produces audit rows via the interceptor.

### Phase 08 — MillWorks.Api bridge wiring
Extend the existing `MillWorks.Api/Bridge/Audit/AuditBridge` (which today
implements only `IFinanceAuditService`) to implement `IAuditPublisher`.
Switch its underlying call from `IAuditLogger` to `IAuditSink`. Centralize
DataProcessing's interceptor attachment in `Program.cs` (the only library
that still self-attaches via constructor injection + `OnConfiguring`); the
other 8 libraries already use the Api-central pattern per the 2026-04-25
survey. Cross-repo phase — touches MillWorks.Api and one library
(DataProcessing); requires Jesse's explicit go-ahead.

### Phase 09 — Consumer library migrations
Per-library cleanup of the 9 audited libraries. Work is **not** templated —
the survey on 2026-04-25 showed each library is in a different starting
state. Compliance is the only library with the inline `AuditLogEntity`
mapping. Three libraries (Compliance, Notification, DataProcessing) have
direct `MillWorks.AuditCore.EntityFramework` package references; the
other 6 use the meta package or no AuditCore reference. Phase 09 deliverables
include: updating `using` statements to point at the lifted attribute
namespace (depends on Phase 04.5), removing Compliance's inline mapping,
narrowing each library's package reference to the smallest one its actual
code requires (Compliance keeps EF for `UseFieldEncryption`; others switch
to Abstractions), implementing `IAuditContextSource` where context
propagation is needed, and dropping the now-unused
`AddXxxServices(Action<IServiceProvider, DbContextOptionsBuilder>)`
overloads. May split into 09a/09b/09c per the phase doc's batching decision.

### Phase 10 — README & docs rewrite
Rewrite the relevant sections of `MillWorks.AuditCore/README.md`
(Architecture, Quick Start, Tamper Detection, Configuration). In
`MillWorks/README.md`: add an `AuditBridge` row to the Bridge Taxonomy
table (it's currently undocumented — only the existing Finance scope
matches reality), and remove the stale "audit logging" mention from the
`SecurityBridge` row. Mark `docs/ConsumerDbContextAuditing.md` as
superseded with a pointer to this redesign plan. Update
`docs/ARCHITECTURE.md` if its diagrams reference the old DbContext
naming.

### Phase 11 — Verification & soak
Full `dotnet test` run; SQL Server integration lane; endurance soak. Manual
end-to-end run on MillWorks: create a Compliance record, verify chain
integrity covers it, verify `FailClosedForRegulated` triggers on a forced
encryption failure. Sign off readiness for the two new apps to integrate
against the new contract.

## README integration

This redesign maintains both READMEs as living documents. Phase 10 is the
explicit doc rewrite, but every prior phase that changes a public contract
records its README touchpoint in its own doc's "README impact" section, so
nothing falls between cracks.

| README | Section | Touched by |
|---|---|---|
| `MillWorks.AuditCore/README.md` | Features → Automatic Entity Auditing | Phase 03, 07 |
| `MillWorks.AuditCore/README.md` | Features → Tamper Detection | Phase 02, 06 |
| `MillWorks.AuditCore/README.md` | Quick Start → Minimal Setup | Phase 05, 06 |
| `MillWorks.AuditCore/README.md` | Quick Start → Automatic Entity Change Tracking | Phase 03, 07 |
| `MillWorks.AuditCore/README.md` | Architecture | Phase 05 |
| `MillWorks.AuditCore/README.md` | Packages table (Abstractions row) | Phase 04.5 (gains marker attributes) |
| `MillWorks.AuditCore/README.md` | Configuration → Database Initialization Defaults | Phase 05 |
| `MillWorks.AuditCore/README.md` | Configuration → Custom SQL Server Schemas | Phase 05 |
| `MillWorks.AuditCore/README.md` | Configuration → Fail-Closed Audit Failures | Phase 06 |
| `MillWorks.AuditCore/README.md` | Database Schema | Phase 06 (AuditOutbox table) |
| `MillWorks.AuditCore/README.md` | Production Readiness | Phase 11 |
| `MillWorks/README.md` | Architecture → Bridge Taxonomy → SecurityBridge entry | Phase 08 (remove stale "audit logging" mention) |
| `MillWorks/README.md` | Architecture → Bridge Taxonomy → AuditBridge (new row) | Phase 08 (add the now-extended bridge with its 9-library scope) |
| `MillWorks/README.md` | External Repositories → MillWorks.AuditCore | Phase 10 |

Phase 10 consolidates and finalizes; the per-phase README impact notes
serve as the running diff Jesse can review during each session.

## Supersession

`docs/ConsumerDbContextAuditing.md` is superseded by this plan. Phase 10
adds a header to that file pointing here. The original is retained for
historical context (it documents the symptom analysis that motivated the
redesign), not as an active spec.

The original plan's three items map to phases here:

| Original item | Replaced by |
|---|---|
| Item 01 — Discoverable consumer opt-in for `AuditLogEntity` | Phase 07 (consumer DbContexts no longer need the opt-in at all) |
| Item 02 — Hash-chain + event rows for consumer DbContext saves | Phase 02 + 06 + 07 (chain coverage falls out of sink-owned persistence) |
| Item 03 — `MillWorks.AuditCore` Meter instruments | Independent — ship separately as a small standalone change. Out of scope for the redesign. |

## Locked decisions (2026-04-25)

These bind the per-phase docs. If a per-phase doc still presents one of
these as a fork, that doc is stale — fix it, don't re-ask.

1. **`AuditSinkMode` default = `Immediate`.** `TransactionalOutboxSink`
   is opt-in. The README documents the upgrade for regulated /
   zero-loss deployments (HIPAA / FERPA / PCI-DSS / any deployment
   whose posture requires that audit-subsystem failures never lose an
   in-flight envelope). Note the framing is durability posture, not a
   specific standard list.
2. **Single shared `IAuditPublisher` in `MillWorks.AuditCore.Abstractions`.**
   No per-library `I{Library}AuditPublisher` interfaces. The bridge
   implements `IAuditPublisher` once.
3. **Phase 09 splits into 09a / 09b / 09c.** 09a = Compliance +
   DataProcessing. 09b = Identity + Notification + SqlBuilder + Ai.
   09c = Document + Media + Git. Separate sessions, separate PRs.
4. **Two-entity split preserved through Phase 11.** `AuditLogEntity`
   and `AuditEventEntity` stay separate; the sink dispatches by envelope
   kind. Unification tracked as a post-Phase-11 follow-up, not part of
   the redesign.
5. **`MillWorks.AuditCore.Meter` ships as an independent PR after
   Phase 11.** Original Item 03 is not part of the redesign phase set.
6. **Document, Media, Git: drop the `MillWorks.AuditCore` meta-package
   reference entirely.** No replacement reference. If future code in any
   of the three needs an AuditCore symbol, that future migration adds
   the smaller correct reference at that time.

## Resolved during planning (no longer open)

- **D2 — Bridge surface.** Resolved by codebase fact: `AuditBridge`
  exists at `MillWorks.Api/Bridge/Audit/AuditBridge.cs`, registered in
  `BridgeServiceExtensions.cs:530` (scoped lifetime). Phase 08 extends
  the existing bridge to implement `IAuditPublisher`. `SecurityBridge`
  is a security/encryption bridge — its README's "audit logging" mention
  is stale wording, not the real wiring.
- **DataProcessing attribute-on-entity discrepancy** — resolved
  2026-04-25 by direct grep. DataProcessing carries `[NoAudit]` × 2
  (`StreamCheckpointEntity.cs:11`, `TempDataRecordEntity.cs:10`). Matrix
  in Phase 09 reflects ground truth.
- **DataProcessing `Dto` reference** (was Phase 09 D4) — resolved
  2026-04-25. `AuditLogDto` is a pure DTO with zero EF coupling; lifted
  to Abstractions in Phase 04.5. DataProcessing's post-redesign deps:
  `MillWorks.AuditCore.Abstractions` + `MillWorks.AuditCore.Services`
  (drops both EF and meta packages).

Phase 01 can start.
