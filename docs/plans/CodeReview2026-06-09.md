# Code Review — 2026-06-09

**Status:** Findings recorded
**Scope:** Full review of `src/` (Abstractions, AspNetCore, EntityFramework, Providers, Services), DI wiring, and sample project

Findings are grouped into one document per theme, ordered here by recommended priority. Severities are per-finding inside each document.

| Priority | Document | Headline |
|---|---|---|
| 1 | [AuditWritePipelineDurability.md](AuditWritePipelineDurability.md) | ✅ **Done** — A mixed replay batch hitting one duplicate key silently loses every other event in the batch and reports success. Plus outbox/DLQ false-success paths. |
| 2 | [TamperDetectionIntegrityGaps.md](TamperDetectionIntegrityGaps.md) | ✅ **Done** — HMAC/signatures now cover chain position (v3 algorithm); deleted-event detection, boundary validation, malformed-JSON detection added. Deferred: GDPR anonymization re-chaining (#5), tail truncation via head anchor (#3) to Merkle pipeline. |
| 3 | [EfInterceptorCoverageGaps.md](EfInterceptorCoverageGaps.md) | ✅ **Done** — Mixed-batch bypass removed (#1); sync saves throw NotSupportedException (#7); FERPA AuditOnly safe on bare contexts (#3); archive cleanup uses ExecuteDeleteAsync (#5); SaveChanges(bool) overloads added (#6); re-entrancy guard fixed (#8); deterministic EnvelopeId (#4); TruncateSafe, non-Guid keys, PK ordering, InvariantCulture, ExecuteMethod fixes (#10); encrypted-query warnings (#9). Deferred: disconnected updates (#2) log+count only — snapshot fallback broke existing test contract. |
| 4 | [ComplianceValidatorAccuracy.md](ComplianceValidatorAccuracy.md) | ✅ **Done** — Wildcard boundary fixed (#1 User. not User); server-side aggregates for integrity checks (#2/#3 All not Any); Min/Max date queries for retention (#4); FromEvents test helper added. Deferred: #5 recursive anonymization (coordinate with tamper-detection supersession). |
| 5 | [HostingAndConfigurationIssues.md](HostingAndConfigurationIssues.md) | ✅ **Done** — UseRequestAuditDispatcher fixed via wrapper class (#1); subsection binding (#2); CorrelationId width (#3); ExplicitlySetProperties tracking (#4); command timeout (#5); pass-through redactor production throw (#6); middleware identity documented (#7); TryAddEnumerable for validators (#8); configurable excluded paths with segment matching, dispatcher idempotency (#9). |
| 6 | [ProviderAndModelCorrectness.md](ProviderAndModelCorrectness.md) | ✅ **Done** — Target sanitization via CreateSanitizedSnapshot (#1); POCO enrichment via GetScalarProperties fix (#2-#3); culture-invariant masking (#4); Duration JSON round-trip (#5); CalculateDuration overflow clamp, InsertedDate UtcNow, non-nullable entity param, AuditProviderTypeMap freeze (#6). |
| 7 | [RedisJobQueueDurability.md](RedisJobQueueDurability.md) | ✅ **Deleted** — No production callers; greenfield policy. |

## Areas reviewed clean

- `AuditCanonicalizer` core mechanics (ordinal sort, NFC normalization, invariant dates, numeric cascade) — deterministic; only gap is the unhandled `JsonException` on the verify path (doc 2).
- `AuditIntegrityRepository` `sp_getapplock` usage, transaction helpers, repository `AsNoTracking`/ordering discipline.
- `InProcessRequestAuditDispatcher` (in-flight capture on cancellation, post-complete drain, DLQ on every loss path) and `AuditContextMiddleware` scope hygiene — no cross-request context bleed.
- `FieldEncryptionService` AES-GCM construction (random 96-bit nonce, AAD binding, buffer zeroing) — minor KDF label ambiguity noted in doc 2.
- IP extraction (no proxy-header parsing; relies on `ForwardedHeadersMiddleware`), options validators (`ValidateOnStart` wired), DI lifetimes, `Decorate` mechanics.
- `IntegrityWriteBatcher` / `IntegrityReconciliationService` outbox + lease semantics.
- Latest migration vs. model snapshot vs. entity configurations — consistent.
