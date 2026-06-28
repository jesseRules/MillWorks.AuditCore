# Mapster Removal Plan

**Status:** Implemented (2026-06-24, commit `5a7a115`)
**Verified:** 2026-06-28 — build clean (0 warnings/0 errors); 30 mapping tests + 156 affected service tests pass; grep sweep empty; no `Mapster.*` transitive packages in `Services`/`AspNetCore`.

## Goal

Remove the Mapster dependency from `MillWorks.AuditCore` entirely and replace it with explicit, hand-written static mapping methods. This aligns the codebase with the project's mapping convention (no AutoMapper/Mapster; static `MapTo`-style methods or dedicated mapping services) and removes a reflection-based mapper from a library whose value proposition is deterministic, tamper-evident integrity.

**Non-goal:** behavioral change. Every mapping must produce byte-identical output to the current Mapster configuration. This is a pure mechanical/structural refactor.

## Current state (inventory)

### Packages (`src/MillWorks.AuditCore.Services/MillWorks.AuditCore.Services.csproj`)
- `Mapster` 10.0.9
- `Mapster.DependencyInjection` 10.0.9

`MillWorks.AuditCore.AspNetCore` consumes Mapster transitively through its `Services` project reference (no direct PackageReference).

### Mapping configuration (`src/MillWorks.AuditCore.Services/AuditMappingConfiguration.cs`)
`IRegister` implementation defining six type pairs. Exact semantics to preserve:

| # | Mapping | Custom rules |
|---|---|---|
| 1 | `AuditEventEntity` → `AuditEventDto` | Ignore `dest.Data` |
| 1 | `AuditEventDto` → `AuditEventEntity` | Ignore `dest.AuditIntegrity` |
| 2 | `AuditLogEntity` ↔ `AuditLogDto` | straight copy both directions |
| 3 | `AuditIntegrityEntity` → `AuditIntegrityDto` | straight copy |
| 3 | `AuditIntegrityDto` → `AuditIntegrityEntity` | Ignore `dest.AuditEvent` |
| 4 | `AuditArchiveRecordEntity` → `ArchiveMetadata` | `Status = src.Status.ToString()`, `ArchiveHash = src.Hash` |
| 5 | `AuditSecurityEventEntity` → `SecurityEventDto` | `Details = ParseDetailsJson(src.DetailsJson)` |
| 5 | `SecurityEventDto` → `AuditSecurityEventEntity` | Leave `DetailsJson` unset (Mapster `Ignore`). **Intentional, not a gap** — `RecordEventAsync` serializes `Details` → `DetailsJson` itself with a size guard + truncation-summary fallback (`AuditSecurityEventService.cs:67–90`). The hand-written `ToEntity` must keep `DetailsJson` unset; add a `// DetailsJson is set by RecordEventAsync, not here` comment so the omission isn't later "fixed" into a double-write. |
| 6 | ~~`AuditEntry` → `AuditEventEntity`~~ | **REMOVED — dead mapping, see D1 (resolved).** Not ported. |

The file also contains `ParseDetailsJson` and `ConvertJsonElement` helpers (malformed-JSON-safe). **These must be carried over verbatim** into the SecurityEvent mapper — they are real logic, not Mapster glue.

### Registration (`src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs`)
- `using Mapster;` (line 42)
- `ConfigureMapster()` (lines 297–304): applies `AuditMappingConfiguration` onto `TypeAdapterConfig.GlobalSettings`, `TryAddSingleton(config)`, `AddMapster()`.
- One call site of `ConfigureMapster()` (find and remove).

### Production call sites (`MapsterMapper.IMapper` injected, `.Map<T>()` calls)
| File | Mappings used |
|---|---|
| `AuditSecurityEventService.cs` | `SecurityEventDto`→entity (×1), entity→`SecurityEventDto` (×3), `IEnumerable<SecurityEventDto>` (×2) |
| `AuditService.cs` | `IEnumerable<AuditLogDto>` (×2), `AuditEventDto` (×1), `List<AuditEventDto>` (×2) |
| `AuditSearchService.cs` | `List<AuditEventDto>` (×1) |
| `AuditQueryService.cs` | `List<AuditEventDto>` (×1), `AuditEventDto` (×1) |
| `AuditArchivalService.cs` | `AuditEventDto`, `AuditIntegrityDto`, `AuditEventEntity`, `AuditIntegrityEntity`, `List<ArchiveMetadata>` |

Each service takes `IMapper mapper` as a primary-constructor parameter — these parameters get removed.

### Tests touching Mapster
| File | Action |
|---|---|
| `tests/Mapping/AuditMappingTests.cs` | Rewrite to call the new static mappers; keep every round-trip/edge-case assertion (the regression safety net — do NOT drop coverage). **Exception:** delete the three `AuditEntry → AuditEventEntity` tests (~lines 407/427/443) — that mapping is being removed per D1. |
| `tests/AspNetCore/MillWorksAuditBuilderTests.cs` | `UseEntityFramework_Mapster_PreservesConsumerRegistrationsOnGlobalSettings` becomes obsolete (no shared `GlobalSettings`). Delete that test + its `Fake*` POCO helpers + Mapster usings. |
| `tests/Integration/SearchAndQueryIntegrationTests.cs` | Drop `IMapper` field; construct services without it. |
| `tests/Integration/SecurityEventIntegrationTests.cs` | Same — drop Mapster usings/wiring. |

### DTO / entity locations (for `using` statements in the new mappers)
- `AuditEventDto`, `AuditIntegrityDto`, `SecurityEventDto`, `AuditLogDto` → `MillWorks.AuditCore.Abstractions.Dto`
- `ArchiveMetadata` → `MillWorks.AuditCore.EntityFramework.Dto`
- Entities → `MillWorks.AuditCore.EntityFramework.Entities`

`Services` already references both `Abstractions` and `EntityFramework`, so the new mappers live cleanly in `Services` with no new project references.

## Target design

Static extension-method mappers under `src/MillWorks.AuditCore.Services/Mapping/`, one class per source family, namespace `MillWorks.AuditCore.Services.Mapping`:

- `AuditEventMappings` — `ToDto(this AuditEventEntity)`, `ToEntity(this AuditEventDto)`
- `AuditLogMappings` — `ToDto`, `ToEntity`
- `AuditIntegrityMappings` — `ToDto`, `ToEntity`
- `ArchiveMappings` — `ToMetadata(this AuditArchiveRecordEntity)`
- `AuditSecurityEventMappings` — `ToDto`, `ToEntity` (+ moved `ParseDetailsJson`/`ConvertJsonElement` as private statics)

(No `AuditEntryMappings` — the `AuditEntry → AuditEventEntity` mapping is dead and is being removed, not ported. See D1.)

Collection sites become LINQ at the call site: `mapper.Map<List<AuditEventDto>>(xs)` → `xs.Select(x => x.ToDto()).ToList()`; `IEnumerable<...>` sites likewise (use `.ToList()` to preserve eager materialization, since Mapster's collection map is eager).

**Critical correctness rule:** Mapster copies every same-named property by convention. Each hand-written mapper must explicitly set *every* property the old config was copying, or data silently drops. For each pair, read the full property list of both types and map each one; only the `Ignore`d properties (per the table above) are intentionally left unset.

## Steps

1. **Add mappers.** Create the `Mapping/` classes above. Port `ParseDetailsJson`/`ConvertJsonElement` into `AuditSecurityEventMappings`. Map every property explicitly; honor the Ignore rules.
2. **Rewrite the mapping tests first** (`AuditMappingTests.cs`) to target the static mappers, before touching call sites — gives a green baseline to refactor against.
3. **Convert call sites.** In the five services: remove the `IMapper mapper` constructor parameter and the `using MapsterMapper;`; replace each `mapper.Map<T>(x)` with the static call / LINQ equivalent.
4. **Remove registration.** Delete `ConfigureMapster()` and its call site; remove `using Mapster;` from `MillWorksAuditBuilder.cs`.
5. **Delete `AuditMappingConfiguration.cs`** and **delete `src/MillWorks.AuditCore.EntityFramework/Dto/AuditEntry.cs`** — once the dead mapping and its three tests are gone, `AuditEntry` is fully orphaned (verified repo-wide: no other code references it; the `docs/redesign/Completed/` mentions are historical only).
6. **Drop package references** (`Mapster`, `Mapster.DependencyInjection`) from `Services.csproj`.
7. **Fix remaining tests** (`MillWorksAuditBuilderTests`, the two integration tests) per the table.
8. **Sweep** for stragglers: `grep -rn "Mapster\|IMapper\|TypeAdapterConfig\|\.Adapt" src tests --include=*.cs | grep -v /obj/` must come back empty.

## Open decisions

- **D1 — `AuditEntry → AuditEventEntity` — RESOLVED: delete (option b).** Confirmed dead: `AuditEntry` has only two production references (its own definition and this mapping config); no production code calls `mapper.Map<AuditEventEntity>(entry)`. The interceptor builds entities via `new AuditEventEntity { … }` (`AuditLogger.cs:508`), and the only `Map<AuditEventEntity>` call (`AuditArchivalService.cs:651`) maps from `AuditEventDto`, not `AuditEntry`. The "for interceptor" comment is stale — `AuditEntry` predates the `AuditEnvelope` producer design. Action: drop the mapping; do not create `AuditEntryMappings`; delete the three `AuditEntry` tests in `AuditMappingTests.cs`.
  - **Also delete the `AuditEntry` type** (`EntityFramework/Dto/AuditEntry.cs`) — once the mapping and its tests are gone it has zero references. Folded into step 5.
- **D2 — `SecurityEventDto → entity` ignores `DetailsJson` — RESOLVED: not a bug, no action beyond preserving behavior.** Verified: `RecordEventAsync` serializes `securityEvent.Details` into `entity.DetailsJson` explicitly (`AuditSecurityEventService.cs:67–90`), with a size guard and a valid-JSON truncation summary. The Mapster `Ignore` exists so the mapper does not clobber that hand-rolled serialization. `Details` is persisted correctly; there is no silent data loss. The hand-written mapper preserves the omission (see mapping table row 5).

## Verification

- `dotnet build` clean (warnings-as-errors per `Directory.Build.props` if set).
- Run mapping + affected service/integration tests **serially** (project rule: never parallel test runs). Use the standard flaky-test `--filter` exclusion for `IntegrityWriteBatcher`/`IntegrityReconciliation`.
- Round-trip assertions in the rewritten `AuditMappingTests` are the primary proof the hand-written mappers match Mapster's output.
- Final grep sweep (step 8) returns nothing.
- Confirm no `Mapster.*` assemblies in `dotnet list package --include-transitive` for `Services` and `AspNetCore`.
