# Entity-Change Redaction Seam

**Status:** Proposed
**Date:** 2026-07-19
**Origin:** MillWorks consultant review 2026-07-19 — "Concrete privacy problem in MillWorks Identity"
**Priority:** P0 — live PII exposure in a consumer (MillWorks Identity)
**Consumer companion:** `MillWorks/MillWorks.Compliance/Plans/Item09-IdentityAuditRedaction.md`

## Problem

The EF change-capture interceptor masks property values from **AuditCore's own
attributes only**. `AuditSaveChangesInterceptor.BuildPropertyMetadata`
(`src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs:110-117`)
reads exactly three attributes:

- `EncryptedFieldAttribute` → `[ENCRYPTED]`
- `SensitiveDataAttribute`   → mask pattern / `***`
- `NoAuditAttribute`         → property skipped

`MaskOrRedact` (same file, ~line 946) then decides masking purely from
`meta.IsEncrypted` / `meta.IsSensitive`. It **never consults
`IAuditFieldRedactor`** — that redactor is wired only into the explicit
`IAuditLogger` pipeline, not the interceptor path. The XML doc on `MaskOrRedact`
already acknowledges the split ("deliberately separate from `IAuditFieldRedactor`
… Both systems must agree").

### Why attributes alone can't close the gap

A consumer cannot always reach the properties that carry PII:

- MillWorks `ApplicationUser : IdentityUser<Guid>` classifies Email,
  NormalizedEmail, UserName, PhoneNumber, PasswordHash, and SecurityStamp with
  `[IdentityPii(...)]` **at the class level with `MemberName = ...`**
  (`ApplicationUser.cs:18-26`), because those members are declared on the
  **framework base type** `IdentityUser<Guid>` and cannot be decorated directly.
- AuditCore reads *property-level* attributes via
  `p.GetCustomAttribute<SensitiveDataAttribute>()`. It will never see a
  class-level, member-targeted attribute — and even if it could, it does not
  know what `[IdentityPii]` is.

Result: creating/updating a user writes email, username, phone, password hash,
and security stamp **unmasked** into `audit.AuditLogs`. The interceptor is
attached to `IdentityDbContext` in `MillWorks.Api/Program.cs:265-267`, so this is
live, not dormant.

## Goal

Give consumers a first-class extensibility seam so the interceptor honors a
consumer-defined sensitivity classification **in addition to** AuditCore's
attributes — without AuditCore taking a dependency on any consumer's attribute
types.

## Solution — `IAuditPropertySensitivityPolicy`

Add an optional policy interface in `MillWorks.AuditCore.Abstractions`:

```csharp
public enum AuditFieldTreatment { Audit, Mask, Encrypt, Omit }

public readonly record struct AuditPropertyRef(Type EntityType, string PropertyName);

public interface IAuditPropertySensitivityPolicy
{
    /// Return a treatment to OVERRIDE the attribute-derived default, or null to defer.
    AuditFieldTreatment? Classify(in AuditPropertyRef property);

    /// Optional mask pattern when treatment is Mask (null => "***").
    string? MaskPattern(in AuditPropertyRef property) => null;
}
```

Integration points inside the interceptor:

1. **DI:** resolve `IEnumerable<IAuditPropertySensitivityPolicy>` (zero-or-more;
   default posture unchanged when none registered).
2. **Metadata build** (`AuditSaveChangesInterceptor.cs:107-118`): after computing
   the attribute-derived `PropertyAuditMetadata`, ask each policy for the
   `(EntityType, PropertyName)`. **Strictest wins** — a policy may tighten
   (`Audit→Mask→Encrypt→Omit`) but never loosen an attribute-derived treatment.
   Cache keyed by `(EntityType, PropertyName)` exactly like the existing
   `_noAuditTypeCache` / property-metadata cache; policies must be pure so the
   cache stays valid.
3. **`MaskOrRedact`** (~line 946): unchanged in shape — it already reads the
   resolved `meta`. Only the metadata it receives changes.
4. **Omit:** treat like `IsNoAudit` in both the entity filter (line 470/696) and
   the per-property loop (line 632) so an omitted field produces no row and no
   value.

### Why a policy, not "wire in IAuditFieldRedactor"

`IAuditFieldRedactor` redacts by *field name string* against a value dictionary —
it has no entity/property-type context and is name-collision prone across
entities. A typed, per-`(EntityType, PropertyName)` policy is precise, cacheable,
and lets each consumer map its own attribute system once. AuditCore stays free of
consumer attribute types (naked-boundary discipline).

## Phases

- **P1 — Abstraction + seam (AuditCore).** Add interface, DI resolution, metadata
  merge with strictest-wins, cache, `Omit` handling. Default behavior identical
  when no policy is registered (regression-guarded).
- **P2 — Tests (AuditCore).** `InterceptorSensitivityPolicyTests`: policy tightens
  Audit→Mask/Encrypt/Omit; strictest-wins vs attributes; cache correctness;
  no-policy path byte-identical to today. Extend the existing
  `InterceptorRedactionBoundaryTests` to assert the interceptor and logger agree
  once a policy is present.
- **P3 — Consumer wiring** lives in the MillWorks companion (Item09).

## Non-goals

- Does **not** put entity-change envelopes into the hash chain — that is
  `EntityChangeChainIntegrity.md` (P1).
- Does not change the explicit `IAuditLogger` redaction path.

## Acceptance

- With no policy registered, all 129 existing targeted tests pass unchanged.
- With a policy that maps `[IdentityPii]`, an `ApplicationUser` create/update
  writes masked/omitted values for every classified member — proven red-then-green
  by the MillWorks E2E test in Item09.
