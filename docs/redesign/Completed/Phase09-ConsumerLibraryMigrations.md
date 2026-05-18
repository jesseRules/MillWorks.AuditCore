# Phase 09 — Consumer library migrations

**Completed 2026-04-26**

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on:
- [`Phase04-5-AttributesToAbstractions.md`](Phase04-5-AttributesToAbstractions.md)
  (attributes must be in Abstractions before consumers can switch)
- [`Phase08-ApiBridgeWiring.md`](Phase08-ApiBridgeWiring.md)
  (DataProcessing's library-side self-attach is removed in Phase 08;
  this phase doesn't redo that work)

## Goal

Per-library cleanup of the 9 audited consumer libraries. The work is
**not** uniform — the survey on 2026-04-25 showed each library is in a
different starting state. This phase delivers per-library targeted
edits, not a templated 9-way fan-out.

After this phase:

- Each library's `using` statements for the four lifted attributes
  point at `MillWorks.AuditCore.Abstractions.Attributes`.
- Compliance's inline `AuditLogEntity` mapping is gone (it's the only
  library that has one).
- Each library uses the narrowest AuditCore package reference that
  satisfies its actual code (not the meta package by default).
- Libraries that need user/correlation context to flow into audit
  envelopes implement `IAuditContextSource` on their DbContext.

## Current state matrix (verified 2026-04-25 by direct grep)

Attribute usage measured via `grep -rn '\[NoAudit\]\|\[PHI\]\|\[FERPA\]\|\[SensitiveData\]\|\[EncryptedField\]'`
across each library. Only `[NoAudit]` and `[EncryptedField]` are
actually used in the live codebase — `[PHI]`, `[FERPA]`, and
`[SensitiveData]` are described in some library docs but not applied to
entities.

| Library | Package(s) today | Lifted-attribute usage (Phase 04.5 affects) | `[EncryptedField]` (stays in EF) | `AuditLogEntity` mapping | `UseFieldEncryption` | Self-attach interceptor | After-Phase-09 package |
|---|---|---|---|---|---|---|---|
| Compliance | `MillWorks.AuditCore.EntityFramework` | none | 6 properties (`BreachNotificationEntity` × 1, `DataBreachReportEntity` × 5) | **yes** (delete) | **yes** (keep) | no | Stays on `MillWorks.AuditCore.EntityFramework` (`[EncryptedField]` + `UseFieldEncryption` keep the EF reference live) |
| Identity | `MillWorks.AuditCore` (meta) | `[NoAudit]` × 1 (`PasswordHistoryEntity.cs:8`) | none | no | no | no | Switch to `MillWorks.AuditCore.Abstractions` |
| DataProcessing | `MillWorks.AuditCore` + `MillWorks.AuditCore.EntityFramework` | `[NoAudit]` × 2 (`StreamCheckpointEntity.cs:11`, `TempDataRecordEntity.cs:10`) | none | no | no | yes — handled by Phase 08 | `MillWorks.AuditCore.Abstractions` + `MillWorks.AuditCore.Services` (drops both EF and meta packages; `AuditLogDto` lifted in Phase 04.5 unblocks this) |
| Notification | `MillWorks.AuditCore.EntityFramework` | `[NoAudit]` × 1 (`NotificationPreferencesEntity.cs:21`) | none | no | no | no | Switch to `MillWorks.AuditCore.Abstractions` |
| SqlBuilder | `MillWorks.AuditCore` (meta) | none in entities (the `global using` at `GlobalUsings.cs:21` is unused — confirm and drop) | none | no | no | no | Switch to `MillWorks.AuditCore.Abstractions`; drop the unused `global using` |
| Document | `MillWorks.AuditCore` (meta) | none | none | no | no | no | D5 — drop entirely (recommended) |
| Media | `MillWorks.AuditCore` (meta) | none | none | no | no | no | D5 — drop entirely (recommended) |
| Git | `MillWorks.AuditCore` (meta) | none | none | no | no | no | D5 — drop entirely (recommended) |
| Ai | `MillWorks.AuditCore` (meta) | `[NoAudit]` × 6 (`ChatMessageEntity.cs:8`, `ChatMessageAttachmentEntity.cs:8`, `ChatArtifactEntity.cs:8`, `CacheMetricEntity.cs:8`, `AiCacheBaselineEntity.cs:9`, `TokenUsageEntity.cs:14`) | none | no | no | no | Switch to `MillWorks.AuditCore.Abstractions` |

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- **Cross-repo phase.** Same as Phase 08 — touches
  `/Users/jesse/RiderProjects/MillWorks/`. Get explicit Jesse
  go-ahead per library or per batch.
- **One library at a time, build after each.** No batching all 9
  before the first build/test cycle.

## Per-library edits

### Compliance

Note: Compliance entities use **only** `[EncryptedField]` (which stays in
EF). They do NOT use `[PHI]`/`[FERPA]`/`[SensitiveData]` despite some
project docs (`Compliance/ProjectReview.md:93,129`) describing them as
PHI-flagged. No attribute-import updates needed for Compliance.

| Action | File |
|---|---|
| Delete the inline `AuditLogEntity` block | `Data/ComplianceDbContext.cs:99-103` |
| Drop `using MillWorks.AuditCore.EntityFramework.Entities;` | `Data/ComplianceDbContext.cs:2` |
| Keep `using MillWorks.AuditCore.EntityFramework.Extensions;` (`UseFieldEncryption`) | `Data/ComplianceDbContext.cs:3` |
| Keep `[EncryptedField]` attribute usages on `BreachNotificationEntity` and `DataBreachReportEntity` (attribute lives in EF; not lifted) | `Models/Entities/Incidents/BreachNotificationEntity.cs:77`, `Models/Entities/Incidents/DataBreachReportEntity.cs:139,150,161,172,183` |
| Add `: IAuditContextSource` to the class declaration; expose four context properties (set by middleware) | `Data/ComplianceDbContext.cs:18-22` |
| Drop the `AddComplianceServices(Action<IServiceProvider, DbContextOptionsBuilder>)` overload (Api owns wiring per Phase 08) | `Extensions/ComplianceServiceExtensions.cs:60-70` |
| `.csproj`: package reference stays `MillWorks.AuditCore.EntityFramework` (`[EncryptedField]` + `UseFieldEncryption` require it) | `MillWorks.Compliance.csproj:18` |

### Identity

| Action | File |
|---|---|
| Update `global using MillWorks.AuditCore.EntityFramework.Attributes;` → `global using MillWorks.AuditCore.Abstractions.Attributes;` | `GlobalUsings.cs:25` |
| The single `[NoAudit]` usage at `PasswordHistoryEntity.cs:8` resolves through the global using; no per-file change needed | (no edit) |
| Add `: IAuditContextSource` if Identity needs context propagation | `Data/IdentityDbContext.cs` |
| `.csproj`: switch `<PackageReference Include="MillWorks.AuditCore" />` → `<PackageReference Include="MillWorks.AuditCore.Abstractions" />` | `MillWorks.Identity.csproj:35` |

### DataProcessing

DataProcessing has `[NoAudit]` on two entities (verified by grep):
`StreamCheckpointEntity.cs:11` and `TempDataRecordEntity.cs:10`. The
`Models/Entities/*.cs` `using MillWorks.AuditCore.EntityFramework.Attributes;`
statements appear in 8 files; only the 2 with `[NoAudit]` actually use
the namespace, but updating all 8 is mechanical and lower-risk than
selectively updating only the 2 (and missing one).

D4 resolved on 2026-04-25: `Services/DataProcessingAuditService.cs:1`
imports `MillWorks.AuditCore.EntityFramework.Dto` solely for the
`AuditLogDto` type. Phase 04.5 lifts `AuditLogDto` to
`MillWorks.AuditCore.Abstractions.Dto`, so DataProcessing's using
statement updates to the Abstractions namespace and the EF package
reference can be dropped entirely.

| Action | File |
|---|---|
| Update `using MillWorks.AuditCore.EntityFramework.Attributes;` → `using MillWorks.AuditCore.Abstractions.Attributes;` (8 entity files) | `Models/Entities/{StreamCheckpointEntity,TempDataRecordEntity,FileSchemaEntity,SchemaColumnEntity,ProcessedTableEntity,DataTransformationPipelineEntity,DataTransformationEntity,DataTransformationPipelineStepEntity}.cs` |
| Update `using MillWorks.AuditCore.EntityFramework.Dto;` → `using MillWorks.AuditCore.Abstractions.Dto;` | `Services/DataProcessingAuditService.cs:1` |
| Update `using MillWorks.AuditCore.EntityFramework.Dto;` → `using MillWorks.AuditCore.Abstractions.Dto;` | `MillWorks.DataProcessing.Tests/ServiceTests/DataProcessingAuditTests.cs:4` |
| Phase 08 already removed the constructor `AuditSaveChangesInterceptor` injection + `OnConfiguring` (`DataProcessingDbContext.cs:15-39`) | (no Phase 09 edit) |
| `.csproj`: drop both `<PackageReference Include="MillWorks.AuditCore" />` and `<PackageReference Include="MillWorks.AuditCore.EntityFramework" />`; add `<PackageReference Include="MillWorks.AuditCore.Abstractions" />` and `<PackageReference Include="MillWorks.AuditCore.Services" />` (Services needed for `IAuditQueryService`) | `MillWorks.DataProcessing.csproj:14-15` |
| `.csproj`: same package switch in the test project (it inherits the same dependency surface for the audit-trail mocking pattern) | `MillWorks.DataProcessing.Tests.csproj` |

### Notification

| Action | File |
|---|---|
| Update `using MillWorks.AuditCore.EntityFramework.Attributes;` → Abstractions equivalent | `Models/Entities/NotificationPreferencesEntity.cs:2` |
| `.csproj`: switch `MillWorks.AuditCore.EntityFramework` → `MillWorks.AuditCore.Abstractions` | `MillWorks.Notification.csproj:20` |

### SqlBuilder

The `global using` at `GlobalUsings.cs:21` exists but no entity in
`Models/Entities/` actually uses any of the four lifted attributes
(verified by grep). The `global using` is dead.

| Action | File |
|---|---|
| **Delete** the unused `global using MillWorks.AuditCore.EntityFramework.Attributes;` (do NOT update to Abstractions — the import is genuinely unused) | `GlobalUsings.cs:21` |
| `.csproj`: switch `MillWorks.AuditCore` → `MillWorks.AuditCore.Abstractions` (or drop entirely if no other AuditCore symbol is used — pre-implementation grep required) | `MillWorks.SqlBuilder.csproj` |

### Document, Media, Git

These three libraries reference `MillWorks.AuditCore` (meta) but have
no audit-specific code. Three options (D5):

a. **Drop the package reference entirely.** Smallest dependency
   surface. Risk: if any code path I missed actually uses an audit
   API, build breaks.
b. **Narrow to `MillWorks.AuditCore.Abstractions`.** Preserves the
   ability to reference `IAuditLogger`, `IAuditContextSource`, etc.,
   without pulling in EF.
c. **Leave alone.** No-op for these libraries.

**Recommendation:** (a) — investigate first via per-library grep for
any `MillWorks.AuditCore` symbol. If genuinely unused, drop. If
something is referenced, narrow per (b).

### Ai

Six entities carry `[NoAudit]` (verified by grep).

| Action | File |
|---|---|
| Update `using MillWorks.AuditCore.EntityFramework.Attributes;` → `using MillWorks.AuditCore.Abstractions.Attributes;` per file (6 files) | `Models/Entities/ChatMessageEntity.cs:1`, `Models/Entities/ChatMessageAttachmentEntity.cs:1`, `Models/Entities/ChatArtifactEntity.cs:1`, `Models/Entities/CacheMetricEntity.cs:1`, `Models/Entities/AiCacheBaselineEntity.cs:1`, `Models/Entities/TokenUsageEntity.cs:7` |
| `.csproj`: switch `MillWorks.AuditCore` (meta) → `MillWorks.AuditCore.Abstractions` | `MillWorks.Ai.csproj:20` |

## Decisions left to Jesse

1. **Phase 09 batching.** Per-library work varies widely; some are 2-line
   changes (Notification), some are larger (Compliance). Three options:
   a. **One session, all 9** — high context load, but the work is
      genuinely small for 7 of the 9.
   b. **Three sessions** — 09a Compliance + DataProcessing (the two
      complex ones); 09b Identity + Notification + SqlBuilder + Ai
      (attribute-update libraries); 09c Document + Media + Git
      (decision-D5 candidates).
   c. **Per-library** — 9 sessions. Overkill given how small most are.
   **Recommendation:** (b). Confirm.
2. **`IAuditContextSource` setter pattern.** Per Phase 04 and the
   shared D1 from RedesignPlan, libraries that implement
   `IAuditContextSource` need a way to populate the four properties.
   Match the existing `AuditApplicationDbContext` (now `AuditDbContext`)
   pattern: public setters that middleware writes to. Confirm.
3. **(resolved 2026-04-25 — DataProcessing carries `[NoAudit]` × 2.)**
   The discrepancy from earlier reviews is closed; the matrix above
   reflects ground truth.
4. **(resolved 2026-04-25 — `AuditLogDto` lifts to Abstractions in
   Phase 04.5.)** `AuditLogDto` is a pure DTO with zero EF coupling;
   the lift unblocks DataProcessing dropping its EF reference entirely.
   See Phase 04.5 for the lift mechanics; the DataProcessing edits
   above reflect the post-lift state.
5. **D5 — Document/Media/Git package reference.** Drop entirely vs
   narrow vs leave alone. Default recommendation: drop entirely after
   per-library grep confirms no usage.

## Verification

After each library:

```bash
cd /Users/jesse/RiderProjects/MillWorks
dotnet build MillWorks.{Library}.csproj
dotnet test MillWorks.{Library}.Tests.csproj
dotnet build MillWorks.sln  # catch cross-library breakage
```

After all 9:

```bash
cd /Users/jesse/RiderProjects/MillWorks
dotnet build MillWorks.sln
dotnet test
```

Acceptance:
- Each migrated library builds and tests green individually.
- Full MillWorks solution builds and tests green.
- For libraries that still legitimately need
  `MillWorks.AuditCore.EntityFramework` (Compliance, possibly
  DataProcessing): confirmed minimal — only the symbols documented in
  the per-library matrix above.
- For libraries that drop the EF reference: `dotnet list package`
  confirms no `MillWorks.AuditCore.EntityFramework` transitive
  dependency.
- Manual smoke: each library's primary write path produces an audit
  envelope (verifiable by querying the audit DbContext for a row that
  matches the entity).

## README impact

Phase 10 finalizes. This phase contributes:
- Updated paragraph in each library's `README.md` (where present)
  describing the audit integration pattern as of Phase 09.
- Confirmation for Phase 10 that the AuditCore README's Quick Start
  example reflects what consumer libraries actually do.

## Out of scope

- AuditCore code changes → Phases 01-07.
- MillWorks.Api wiring → Phase 08.
- Migrating libraries that don't currently use audit (Survey, Project,
  etc.) → out of scope; they keep current configuration.

## Done when

- All 9 libraries pass their per-library checklist.
- MillWorks solution builds and tests green.
- Per-library package references match the post-Phase-09 column of the
  matrix above.
- Smoke tests confirm audit still flows for each migrated library.
- Phase doc updated with completion notes per batch (e.g., "09a
  Compliance + DataProcessing — Completed YYYY-MM-DD; 09b ...; 09c ...").
