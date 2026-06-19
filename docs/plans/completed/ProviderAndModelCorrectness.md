# Provider Pipeline and Model Correctness

**Status:** Implemented
**Date:** 2026-06-09 (code review)
**Scope:** `MillWorks.AuditCore.Providers`, `AuditEvent`/DTO models in Abstractions

## Problem

The provider pipeline's redaction story has a hole — the raw entity (including the very properties the provider masks elsewhere) is persisted via `Target.New` under the default pass-through redactor — and the `UserAuditProvider` enrichment path is dead code on the only production dispatch path. Several model-level defaults and serialization details silently corrupt or drop data.

## Findings

### 1. Provider pipeline persists the raw entity via `Target.New` (High)

`Providers/Base/BaseAuditProvider.cs:67`, `AspNetCore/Services/AuditEventFactory.cs:70-75`

`UserAuditProvider` carefully excludes `PasswordHash`, `SecurityStamp`, `RefreshToken`, etc. from `GetChanges`, but `BaseAuditProvider.CreateAuditEventAsync` hands the raw entity to `CreateEntityEvent`, which sets `Target = new AuditTarget { Old = oldValues, New = entity }`. `AuditLogger` serializes `Target` into `JsonData` after `IAuditFieldRedactor.RedactTarget`, whose default interface implementation is a pass-through (`IAuditFieldRedactor.cs:42`). Net effect: password hashes and refresh tokens land in plaintext audit JSON unless the consumer registers a redactor that handles them.

**Fix:** Providers should snapshot only non-sensitive scalars into `Target` (a redacted projection), rather than relying on a downstream redactor knowing entity shapes.

### 2. `UserAuditProvider` enrichment is dead code on the production dispatch path (Medium)

`Providers/Implementations/UserAuditProvider.cs:60-108`

`EnrichAuditEventAsync`'s switch has only a `case Dictionary<string, object> entityDict:` arm, but the interceptor (`AuditSaveChangesInterceptor.CaptureForProviderDispatch`) always passes `entry.Entity` — a POCO. `UserId`/`AspNetUserId`/`Email`/`FullName`/`HasRefreshToken` custom fields are silently never populated for real user entities.

**Fix:** Add a POCO branch (cached reflection over the entity's properties) or convert the entity to the expected dictionary shape before enrichment.

### 3. `GetScalarProperties` includes interface-typed navigation/collection properties (Medium)

`Providers/Base/BaseAuditProvider.cs:166-172` (consumed at 116–161 and `UserAuditProvider.cs:119-176`)

The filter `!p.PropertyType.IsClass || p.PropertyType == typeof(string)` is documented as "scalar (non-navigation, non-collection)," but `IsClass` is false for interfaces, so `ICollection<Child>`/`IList<T>` navigations pass through. In the entity-vs-entity `GetChanges` path they're compared with `Equals` (reference equality), so two snapshots always differ, and entire collection objects — with whatever PII the children carry — get embedded into `CustomFields["Changes"]` and serialized.

**Fix:** Also exclude `p.PropertyType.IsInterface`, and filter indexers (`p.GetIndexParameters().Length == 0` — an indexer currently throws `TargetParameterCountException`, which the `catch (TargetException)` does not cover).

### 4. Culture-sensitive date masking persisted into audit data (Medium)

`Providers/Implementations/UserAuditProvider.cs:202`

`MaskPersonalData` formats `DateTimeOffset dt => dt.ToString("yyyy-MM")` without `CultureInfo.InvariantCulture`; under a non-Gregorian host culture the masked value persisted into `Changes` becomes a Hijri year-month. The rest of the codebase (canonicalizer, archival) is invariant-culture-correct.

**Fix:** `dt.ToString("yyyy-MM", CultureInfo.InvariantCulture)`.

### 5. `AuditEvent.Duration` silently lost on JSON round-trip (Medium)

`Abstractions/Models/AuditEvent.cs:50`

`Duration` has `private set` and `[JsonPropertyName]` but no `[JsonInclude]`, so System.Text.Json serializes it but cannot deserialize it. `DeadLetterAuditEvent.OriginalEvent` round-trips through `JsonSerializer` in both file and Redis DLQs — replayed events lose `Duration` with no error. (The same field is also dropped by `AuditEventRedactionHelper.RedactEvent`, tracked in `AuditWritePipelineDurability.md`.)

**Fix:** Add `[JsonInclude]` (or widen the setter).

### 6. Low-severity items

- `AuditEvent.CalculateDuration` overflows `int` for spans over ~24.8 days and goes negative when `EndDate < StartDate` (`AuditEvent.cs:213-219`), violating its own `[Range(0, int.MaxValue)]`. Clamp with `long` math or change `Duration` to `long?`.
- `AuditEventDto.InsertedDate` and `AuditEventResponse.InsertedDate` default to local-time `DateTimeOffset.Now` (`Dto/AuditEventDto.cs:141`, `Responses/AuditEventResponse.cs:27`) while every other timestamp default is `UtcNow`. The archive-restore path deserializes `AuditEventDto` directly: an absent `inserted_date` fabricates a local-time stamp — and `InsertedDate` participates in the event hash, guaranteeing an integrity mismatch. Use `UtcNow` or drop the default on read DTOs.
- `IAuditProvider.CreateAuditEventAsync(object? entity)` is declared nullable but the only base implementation throws `ArgumentNullException` (`Providers/Base/IAuditProvider.cs:18` vs `BaseAuditProvider.cs:65`). Make the parameter non-nullable.
- `AuditProviderTypeMap` is a documented singleton with a public `Register` over a plain `Dictionary` (`Abstractions/Interfaces/AuditProviderTypeMap.cs:13-28`). Safe today (startup-only registration), but any post-startup `Register` concurrent with interceptor reads is a torn-dictionary race. Make `Register` builder-only or freeze after startup.

## Implementation Outline

1. Fix the `Target` redaction hole (#1) — decide the projection shape, then update `BaseAuditProvider`/`AuditEventFactory` and add a test asserting sensitive properties never appear in serialized `JsonData` with the default redactor.
2. Make enrichment work on POCOs (#2) with a test through the real interceptor dispatch path.
3. Tighten the scalar filter (#3) and the masking culture (#4).
4. Apply the model fixes (#5, #6); the `Duration` JSON fix needs a DLQ round-trip test.

## Non-Goals

- Adding new providers or changing the provider registration model.
- Replacing reflection-based change capture with source generation (possible later optimization, not a correctness issue).
