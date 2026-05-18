# Phase 04.5 — Lift marker attributes + `AuditLogDto` to Abstractions

**Completed 2026-04-26**

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase04-AuditContextSource.md`](Phase04-AuditContextSource.md)

## Goal

Two related lifts in one phase:

1. **Marker attributes.** Move `[PHI]`, `[FERPA]`, `[SensitiveData]`,
   `[NoAudit]` from `MillWorks.AuditCore.EntityFramework.Attributes` to
   `MillWorks.AuditCore.Abstractions.Attributes`. Consumer libraries
   that decorate their entity properties for audit-policy purposes can
   then depend only on `MillWorks.AuditCore.Abstractions` — no EF
   coupling needed for what is fundamentally a metadata concern.

2. **`AuditLogDto`.** Move `MillWorks.AuditCore.EntityFramework.Dto.AuditLogDto`
   to `MillWorks.AuditCore.Abstractions.Dto.AuditLogDto`. The DTO is
   a pure data carrier (24 fields, references only
   `MillWorks.AuditCore.Abstractions.Enums.AuditAction`). It is currently
   parked in the EF package by historical accident; that placement
   forces `IAuditQueryService` consumers (Services package) to bring an
   EF dependency they don't otherwise need. Sibling DTOs
   (`AuditEventDto`, `AuditIntegrityDto`, etc.) already live in
   `MillWorks.AuditCore.Abstractions.Dto/`; this brings `AuditLogDto`
   into line.

`[EncryptedField]` stays in `MillWorks.AuditCore.EntityFramework`. It is
not a pure marker — it pairs with `EncryptedValueConverter`, which lives
in EF and uses `IFieldEncryptionService` through EF's
`ValueConverter<TModel, TProvider>` plumbing. Lifting the converter
would require lifting EF's value-converter machinery into Abstractions,
which is out of scope for this redesign.

The other DTOs in `MillWorks.AuditCore.EntityFramework.Dto/`
(`ArchiveMetadata`, `ArchiveRecord`, `AuditArchive`, `AuditEntry`) stay
where they are — they are EF-archive-coupled and outside this phase's
scope.

## Why this phase exists

This phase was added after the Phase 09 spec collided with the live
codebase. Direct grep on 2026-04-25 of all 9 consumer libraries found
the `MillWorks.AuditCore.EntityFramework` references break down as:

| Library | Reference exists for |
|---|---|
| Compliance | Inline `AuditLogEntity` mapping + `[EncryptedField]` × 6 + `UseFieldEncryption` extension. Zero lifted-attribute usage. |
| DataProcessing | `AuditSaveChangesInterceptor` constructor injection (Phase 08) + `[NoAudit]` × 2 (`StreamCheckpointEntity`, `TempDataRecordEntity`) + `AuditLogDto` import in `DataProcessingAuditService.cs` |
| Identity | `[NoAudit]` × 1 (`PasswordHistoryEntity`) via `GlobalUsings.cs:25` |
| Notification | `[NoAudit]` × 1 (`NotificationPreferencesEntity:21`) |
| Ai | `[NoAudit]` × 6 (six chat / cache / token entities) |
| SqlBuilder | Dead `global using` in `GlobalUsings.cs:21` (no actual entity uses any lifted attribute) |
| Document, Media, Git | None (meta-package reference is unused) |

`[NoAudit]` is the only lifted attribute actually used in MillWorks
today; `[PHI]`, `[FERPA]`, and `[SensitiveData]` are mentioned in some
project-level docs but not applied to entities. They lift anyway —
keeping the four together preserves the conceptual grouping ("policy
markers") and avoids future churn if any of them gets adopted.

If Phase 09 only deletes the inline `AuditLogEntity` mapping (Compliance)
and the interceptor constructor injection (DataProcessing), four
libraries (Identity, DataProcessing, Notification, Ai) still need the
EntityFramework reference solely to import `[NoAudit]` — an attribute
that has nothing to do with EF. That's the architectural smell. Lifting
the four marker attributes is a one-shot fix that lets every library
except Compliance narrow its dependency to Abstractions. The
`AuditLogDto` lift in the same phase removes the matching reason
DataProcessing's service code keeps EF.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- **Greenfield rule applies — no type forwarders.** Delete the old
  attribute classes from `MillWorks.AuditCore.EntityFramework.Attributes`.
  Consumer libraries that import from the old namespace will fail to
  compile until Phase 09 updates their `using` statements. Phase 04.5
  ships in AuditCore; consumers update in Phase 09. There is a build
  break window between the AuditCore release and the consumer migration —
  acceptable because there are no external consumers and the gap is
  bounded by the next session.
- **`[EncryptedField]` stays where it is.** Do NOT attempt to also lift
  it; that's a separate and larger refactor.
- **No semantic changes.** Attributes are pure markers — moving them
  changes the namespace, nothing else. Property-level scanning logic in
  `AuditSaveChangesInterceptor` updates the namespace it reads from but
  does not change behavior.

## Files

### Attribute lift (4 attributes)

| Action | Path | Purpose |
|---|---|---|
| New | `src/MillWorks.AuditCore.Abstractions/Attributes/PHIAttribute.cs` | Lifted from EF package |
| New | `src/MillWorks.AuditCore.Abstractions/Attributes/FERPAAttribute.cs` | Lifted from EF package |
| New | `src/MillWorks.AuditCore.Abstractions/Attributes/SensitiveDataAttribute.cs` | Lifted from EF package |
| New | `src/MillWorks.AuditCore.Abstractions/Attributes/NoAuditAttribute.cs` | Lifted from EF package |
| Deleted | `src/MillWorks.AuditCore.EntityFramework/Attributes/PHIAttribute.cs` | Removed |
| Deleted | `src/MillWorks.AuditCore.EntityFramework/Attributes/FERPAAttribute.cs` | Removed |
| Deleted | `src/MillWorks.AuditCore.EntityFramework/Attributes/SensitiveDataAttribute.cs` | Removed |
| Deleted | `src/MillWorks.AuditCore.EntityFramework/Attributes/NoAuditAttribute.cs` | Removed |

### `AuditLogDto` lift

| Action | Path | Purpose |
|---|---|---|
| New | `src/MillWorks.AuditCore.Abstractions/Dto/AuditLogDto.cs` | Lifted from EF package; namespace becomes `MillWorks.AuditCore.Abstractions.Dto` |
| Deleted | `src/MillWorks.AuditCore.EntityFramework/Dto/AuditLogDto.cs` | Removed |
| Modified | `src/MillWorks.AuditCore.Services/IAuditService.cs` | Update `using` (was `MillWorks.AuditCore.EntityFramework.Dto`) |
| Modified | `src/MillWorks.AuditCore.Services/IAuditQueryService.cs` | Same |
| Modified | `src/MillWorks.AuditCore.Services/AuditService.cs` | Same |
| Modified | `src/MillWorks.AuditCore.Services/AuditQueryService.cs` | Same |
| Modified | `src/MillWorks.AuditCore.Services/AuditQueryServiceWithMetaTracking.cs` | Same |

### Reference updates (apply to all `src/` and `tests/` files that imported from the old namespaces)

| Action | Path | Purpose |
|---|---|---|
| Modified | All AuditCore source files that imported `MillWorks.AuditCore.EntityFramework.Attributes` | Update `using` to `MillWorks.AuditCore.Abstractions.Attributes` |
| Modified | All AuditCore source files that imported `MillWorks.AuditCore.EntityFramework.Dto` (where the only used type is `AuditLogDto`) | Update `using` to `MillWorks.AuditCore.Abstractions.Dto` |
| Modified | All AuditCore test files that imported from either old namespace | Same treatment |

### New tests

| Action | Path | Purpose |
|---|---|---|
| New | `tests/MillWorks.AuditCore.Tests/Abstractions/LiftLocationTests.cs` | Sanity tests that the four attributes AND `AuditLogDto` resolve to their Abstractions namespaces |

## Refactor outline

### Per-attribute move

For each of the four attributes:

1. Read the existing class file under
   `src/MillWorks.AuditCore.EntityFramework/Attributes/`.
2. Create the same file under
   `src/MillWorks.AuditCore.Abstractions/Attributes/` with namespace
   `MillWorks.AuditCore.Abstractions.Attributes`. Body byte-identical
   except for the namespace.
3. Delete the original file.

### `AuditLogDto` move

1. Read `src/MillWorks.AuditCore.EntityFramework/Dto/AuditLogDto.cs`.
   Today: namespace `MillWorks.AuditCore.EntityFramework.Dto`,
   `using MillWorks.AuditCore.Abstractions.Enums;` plus standard System
   imports. No EF references.
2. Create `src/MillWorks.AuditCore.Abstractions/Dto/AuditLogDto.cs`
   with namespace `MillWorks.AuditCore.Abstractions.Dto`. Body
   byte-identical except for the namespace.
3. Delete the original file.

### Reference update

```bash
grep -rn "MillWorks.AuditCore.EntityFramework.Attributes\|MillWorks.AuditCore.EntityFramework.Dto" \
    /Users/jesse/RiderProjects/MillWorks.AuditCore/src \
    /Users/jesse/RiderProjects/MillWorks.AuditCore/tests
```

For each match:
- `using MillWorks.AuditCore.EntityFramework.Attributes;` →
  `using MillWorks.AuditCore.Abstractions.Attributes;`
- `using MillWorks.AuditCore.EntityFramework.Dto;` (where the only used
  type is `AuditLogDto`) →
  `using MillWorks.AuditCore.Abstractions.Dto;`

If a file uses both `AuditLogDto` AND another EF.Dto type
(`ArchiveMetadata`, `ArchiveRecord`, `AuditArchive`, `AuditEntry`),
keep both `using` lines side by side — only `AuditLogDto`'s namespace
changed.

The interceptor (`AuditSaveChangesInterceptor.cs`) is the highest-impact
caller for the attribute lift — it scans for these attributes via
reflection. The reflection calls (`GetCustomAttribute<FERPAAttribute>()`,
etc.) work by type identity, not namespace string, so once the
using-statement points to the new namespace the call site is unchanged.

`PropertyAuditMetadata` (which caches per-property attribute readings)
likely references the attribute types directly — same treatment.

For `AuditLogDto`, the affected AuditCore.Services files are listed in
the table above. Each file currently imports
`MillWorks.AuditCore.EntityFramework.Dto` solely for `AuditLogDto`; after
the lift they import the Abstractions namespace instead.

## Decisions left to Jesse

1. **Type forwarders.** Greenfield rule says no. The cross-repo consumer
   libraries (Compliance, Identity, DataProcessing, Notification,
   SqlBuilder, Ai) and their tests will fail to compile against the new
   AuditCore release until their Phase 09 migration runs.
   **Recommendation:** ship Phase 04.5 in a pre-release version (e.g.,
   `2.0.0-preview`); MillWorks pins to the stable `1.6.2` until Phase 09
   is ready, then bumps. Confirm.
2. **Attribute base class.** Today the four attributes inherit from
   `Attribute` directly. Is there value in an `AuditCoreAttribute` base
   class for grouping / discovery? **Recommendation:** no — adds no
   behavior, costs reflection cost.
3. **Are there other attributes worth lifting?** A grep of
   `MillWorks.AuditCore.EntityFramework/Attributes/` shows only the
   five (PHI, FERPA, SensitiveData, NoAudit, EncryptedField).
   `EncryptedField` stays. The other four move. No other candidates.
4. **AuditCore.Services usage of the attributes / DTO.** Confirmed by
   grep on 2026-04-25:
   - `MillWorks.AuditCore.Services` does not currently `using` the
     attribute namespace at the file level — attribute scanning is
     centralized in the interceptor.
   - `MillWorks.AuditCore.Services` files that import
     `MillWorks.AuditCore.EntityFramework.Dto` solely for `AuditLogDto`:
     `IAuditService.cs`, `IAuditQueryService.cs`, `AuditService.cs`,
     `AuditQueryService.cs`, `AuditQueryServiceWithMetaTracking.cs`.
     All five get the using-statement update per the table above.
5. **Other DTOs in `MillWorks.AuditCore.EntityFramework.Dto/`.** The
   directory also contains `ArchiveMetadata.cs`, `ArchiveRecord.cs`,
   `AuditArchive.cs`, `AuditEntry.cs`. These are EF-archive-coupled
   (they reference EF entities like `AuditArchiveRecordEntity` directly)
   and are NOT lifted. Out of scope for this phase.

## Verification

```bash
# After all moves complete
dotnet build MillWorks.AuditCore.sln

# Confirm no residual references to the old namespaces within AuditCore:
grep -rn "MillWorks.AuditCore.EntityFramework.Attributes" \
    /Users/jesse/RiderProjects/MillWorks.AuditCore/src \
    /Users/jesse/RiderProjects/MillWorks.AuditCore/tests
# Expected: no output

# AuditLogDto-only consumers must also be clean (other EF.Dto types
# remain valid imports for archive code):
grep -rn "MillWorks.AuditCore.EntityFramework.Dto" \
    /Users/jesse/RiderProjects/MillWorks.AuditCore/src/MillWorks.AuditCore.Services
# Expected: no output (Services no longer needs the EF.Dto namespace
# once AuditLogDto moves)

dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj
```

Acceptance:
- All AuditCore tests pass — both moves are mechanical; behavior is
  unchanged.
- `LiftLocationTests` confirms each of the four attribute types AND
  `AuditLogDto` resolves to its Abstractions namespace.
- Zero residual references to `MillWorks.AuditCore.EntityFramework.Attributes`
  within AuditCore.
- Zero references to `MillWorks.AuditCore.EntityFramework.Dto` within
  `MillWorks.AuditCore.Services` (Archive-related EF.Dto references in
  the EF package itself are unaffected).

Cross-repo build break is expected and acceptable here. Phase 09 closes
it.

## README impact

Phase 10 will update:
- `MillWorks.AuditCore/README.md` Packages table:
  `MillWorks.AuditCore.Abstractions` description gains "marker
  attributes for compliance policy and redaction; the `AuditLogDto`
  query-result type."
- Any code snippets in the README that show `using
  MillWorks.AuditCore.EntityFramework.Attributes;` or `using
  MillWorks.AuditCore.EntityFramework.Dto;` (for `AuditLogDto`) switch
  to the new namespaces.

Do NOT edit README in this phase.

## Out of scope

- `[EncryptedField]` move — explicitly out of scope.
- `EncryptedValueConverter` move — out of scope (would require lifting
  EF's value-converter machinery).
- Other DTOs in `MillWorks.AuditCore.EntityFramework.Dto/`
  (`ArchiveMetadata`, `ArchiveRecord`, `AuditArchive`, `AuditEntry`) —
  out of scope; EF-archive coupled.
- Consumer library migration (updating their `using` statements) →
  Phase 09.
- Pre-release versioning policy / NuGet tagging → handled outside the
  redesign phase docs.

## Done when

- Four attribute files exist in
  `src/MillWorks.AuditCore.Abstractions/Attributes/`; old files deleted
  from `src/MillWorks.AuditCore.EntityFramework/Attributes/`.
- `AuditLogDto.cs` exists in
  `src/MillWorks.AuditCore.Abstractions/Dto/`; old file deleted from
  `src/MillWorks.AuditCore.EntityFramework/Dto/`.
- All AuditCore-internal references updated (5 Services files for
  `AuditLogDto`; the interceptor + property metadata cache + tests for
  attributes).
- Full AuditCore test suite green.
- Phase doc updated with "Completed YYYY-MM-DD".

Completed 2026-04-26 — four marker attributes (`[PHI]`, `[FERPA]`, `[SensitiveData]`, `[NoAudit]`) plus the paired `SensitiveDataType` enum (D6 — confirmed at session start; lifted alongside its attribute because `SensitiveDataAttribute.DataType` is typed `SensitiveDataType`, so leaving the enum in EF would break the dependency direction) shipped under `MillWorks.AuditCore.Abstractions.Attributes`. `AuditLogDto` shipped under `MillWorks.AuditCore.Abstractions.Dto`. Old EF-namespaced files deleted; no type forwarders. Reference-update blast radius (D4/D7 spec corrections vs the 2026-04-25 audit): 22 AuditCore-internal call sites — interceptor (`AuditSaveChangesInterceptor`), regulated-failure policy, model-builder encryption extension (mixed: keeps EF.Attributes for `EncryptedField`, adds Abstractions.Attributes for `SensitiveData`), two integrity entities, **`ComplianceAttributeScanner`** in Services (the spec said Services didn't import the attribute namespace; it does and the matching test fixture imports it too), and 12 test fixtures. AuditLogDto: 5 Services files (`IAuditService`, `IAuditQueryService`, `AuditService`, `AuditQueryService`, `AuditQueryServiceWithMetaTracking`) had EF.Dto removed; `AuditMappingConfiguration` keeps both usings (mixed); `IAuditArchivalService` and `AuditArchivalService` keep EF.Dto (they consume `ArchiveMetadata`, which stays in EF). Verification grep at completion: `MillWorks.AuditCore.EntityFramework.Attributes` retains only `AuditableAttribute` + `EncryptedFieldAttribute` namespace declarations and 6 legitimate consumer `using` lines for those two types; `MillWorks.AuditCore.EntityFramework.Dto` retains the 4 stay-in-EF DTO declarations plus consumer `using` lines for those types. New 7-test `Abstractions/LiftLocationTests.cs` pins namespace + assembly for each lifted type. Full unit suite green: 1048 passed / 0 failed / 4 skipped (Phase 04 baseline 1041 + 7 lift-location tests). Cross-repo build break window opens here for the six MillWorks consumer libraries (Compliance, Identity, DataProcessing, Notification, SqlBuilder, Ai); MillWorks pins `1.6.2` until Phase 09 migrates their `using` statements. Versioning posture: `2.0.0-preview` for this drop; stable `2.0.0` after Phase 09.
