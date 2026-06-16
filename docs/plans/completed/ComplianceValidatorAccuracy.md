# Compliance Validator Accuracy

**Status:** Implemented (2026-06-09)
**Date:** 2026-06-09 (code review)
**Scope:** `AuditComplianceService`, the seven standard validators, `AuditRetentionPolicy` wildcard matching, GDPR anonymization depth

## Problem

The compliance reports make claims that the underlying queries cannot support: the integrity rules always evaluate a navigation property that is never loaded, the retention rules are computed over a recency-biased sample, and the retention-policy wildcard matcher can select the wrong event families for archival/deletion — a compliance-grade data-loss bug.

## Findings

### 1. Retention wildcard over-matching deletes/archives the wrong event families (High)

`Abstractions/Dto/AuditRetentionPolicy.cs:147-148` (`MatchesEventType`)

```csharp
var prefix = EventType[..^2]; // Remove ".*"
return eventType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
```

`EventType[..^2]` strips `".*"` **including the dot**, so a `"User.*"` policy does `StartsWith("User")` and matches `"UserProfile.Updated"`, `"UserGroup.Deleted"`, etc. The suffix branch has the same bug: `"*.Login"` → `EndsWith("Login")` matches `"User.FailedLogin"`. `AuditComplianceService.ApplyRetentionPolicyAsync` archives/deletes matched events, so a short-retention `"User.*"` policy can prematurely destroy audit records belonging to other event families.

**Fix:** Preserve the boundary: `var prefix = EventType[..^1]` (keep the dot, i.e. `"User."`) and `var suffix = EventType[1..]` (keep the dot, `".Login"`). Add tests for the `UserProfile` / `FailedLogin` near-miss cases.

### 2. Every validator's integrity rule reads a navigation property that is never populated (High)

`AuditComplianceService.cs:89-90` → `AuditEventRepository.GetByDateRangeAsync` (`AsNoTracking`, no `Include`) vs `FerpaValidator.cs:470`, `HipaaValidator.cs:172`, `GdprValidator.cs:266`, `Iso27001Validator.cs:43-44`, `PciDssValidator.cs:295`, `Soc2Validator.cs:348`, `StigValidator.cs:146`

No lazy-loading proxies are configured anywhere, so `e.AuditIntegrity` is always null and the integrity rules always fail — including FERPA reporting a false "CRITICAL" when tamper detection is enabled and working. The reports' integrity evidence is wrong in both directions.

**Fix:** Have the compliance loading path `Include(e => e.AuditIntegrity)` — or better, run a server-side count of events lacking integrity rows instead of materializing navigations.

### 3. Integrity rules pass if a single event is protected (Medium)

Same lines as #2, e.g. `events.Any(e => e.AuditIntegrity != null)`

Once #2 is fixed, one protected event out of 5,000 yields `Passed = true` even with tamper detection enabled. Should be `All` (or a server-side "count unprotected == 0" check) when `EnableTamperDetection` is true.

### 4. Retention rules computed over the 5,000 most-recent events (Medium)

`AuditComplianceService.cs:89-91` + `GdprValidator.cs:155-179` (and the PCI 10.5 / SOC2 CC7.2 / HIPAA / ISO minimum-retention rules)

`GetByDateRangeAsync` orders by `InsertedDate` descending and takes 5,000, so on any system with more events in range, the "oldest event" in the sample is actually recent. GDPR's max-retention check (`<= 2555 days`) then passes even when 10-year-old personal data exists — a pass-when-should-fail — while the minimum-retention rules fail spuriously.

**Fix:** Compute oldest/newest via server-side `Min`/`Max` queries, not the sample.

### 5. Root-level-only JSON anonymization leaks nested PII (Medium)

`AuditComplianceService.cs:558-598`

`AnonymizeJsonData` inspects only root properties; `{"Customer": {"Email": "...", "FullName": "..."}}` is written through verbatim by `property.WriteTo(writer)`. For a GDPR erasure feature this under-deletes.

**Fix:** Recurse into objects/arrays, mirroring the canonicalizer's traversal. Note the related integrity interaction: anonymization invalidates event hashes — see `TamperDetectionIntegrityGaps.md` finding 5; fix them together.

### 6. Empty-input edge (noted, Low)

Several validators use `events.All(...)` patterns that are vacuously true on zero events (e.g. `HipaaValidator.cs:115`, `Soc2Validator.cs:110`). Today this is mostly masked by sibling rules failing on empty input, but when touching the validators, make zero-event windows an explicit "insufficient data" outcome rather than a pass.

## Implementation Outline

1. Fix the wildcard matcher (#1) first — it is the only finding that destroys data — with boundary tests, and audit any persisted policies for over-broad patterns.
2. Rework the compliance data-loading path (#2, #4): server-side aggregates (`Min`/`Max` dates, unprotected-event count) instead of a 5,000-row sample; then tighten the rule predicates (#3, #6).
3. Make anonymization recursive (#5) in the same change as the integrity-supersession design from `TamperDetectionIntegrityGaps.md`.
4. Regression-test each standard's integrity and retention rules against a seeded database where the expected outcome is known (both pass and fail directions).

## Non-Goals

- Expanding the substring-heuristic rule content of the validators (event-type taxonomy work is separate).
- Building consent storage — `ConsentVerificationService` reviewed clean (fails closed).
