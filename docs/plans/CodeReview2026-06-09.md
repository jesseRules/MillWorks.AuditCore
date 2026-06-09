# Code Review — 2026-06-09

**Status:** Findings recorded
**Scope:** Full review of `src/` (Abstractions, AspNetCore, EntityFramework, Providers, Services), DI wiring, and sample project

Findings are grouped into one document per theme, ordered here by recommended priority. Severities are per-finding inside each document.

| Priority | Document | Headline |
|---|---|---|
| 1 | [AuditWritePipelineDurability.md](AuditWritePipelineDurability.md) | ✅ **Done** — A mixed replay batch hitting one duplicate key silently loses every other event in the batch and reports success. Plus outbox/DLQ false-success paths. |
| 2 | [TamperDetectionIntegrityGaps.md](TamperDetectionIntegrityGaps.md) | ✅ **Done** — HMAC/signatures now cover chain position (v3 algorithm); deleted-event detection, boundary validation, malformed-JSON detection added. Deferred: GDPR anonymization re-chaining (#5), tail truncation via head anchor (#3) to Merkle pipeline. |
| 3 | [EfInterceptorCoverageGaps.md](EfInterceptorCoverageGaps.md) | Three paths persist entity changes with zero audit records (mixed batches, disconnected updates, sync saves); FERPA AuditOnly can crash the consumer's save; archive cleanup SQL fails on SQL Server. |
| 4 | [ComplianceValidatorAccuracy.md](ComplianceValidatorAccuracy.md) | Retention wildcard `"User.*"` matches `UserProfile.*` (data-destroying); integrity rules read a never-loaded navigation; retention rules computed over a recency-biased sample. |
| 5 | [HostingAndConfigurationIssues.md](HostingAndConfigurationIssues.md) | `UseRequestAuditDispatcher` unregisters unrelated hosted services (kills `IntegrityWriteBatcher`); flat `"Audit"` config binding makes the sample's nested appsettings dead config; long `X-Correlation-Id` headers suppress audit rows. |
| 6 | [ProviderAndModelCorrectness.md](ProviderAndModelCorrectness.md) | Raw entities (incl. `PasswordHash`/`RefreshToken`) persist via `Target.New` under the default redactor; `UserAuditProvider` enrichment is dead code on the real dispatch path. |
| 7 | [RedisJobQueueDurability.md](RedisJobQueueDurability.md) | Job recovery is dead code (scans for a state Redis deletes); `CompleteAsync` deletes the wrong hash field; failed jobs are dropped. No production caller yet — fix or remove. |

## Areas reviewed clean

- `AuditCanonicalizer` core mechanics (ordinal sort, NFC normalization, invariant dates, numeric cascade) — deterministic; only gap is the unhandled `JsonException` on the verify path (doc 2).
- `AuditIntegrityRepository` `sp_getapplock` usage, transaction helpers, repository `AsNoTracking`/ordering discipline.
- `InProcessRequestAuditDispatcher` (in-flight capture on cancellation, post-complete drain, DLQ on every loss path) and `AuditContextMiddleware` scope hygiene — no cross-request context bleed.
- `FieldEncryptionService` AES-GCM construction (random 96-bit nonce, AAD binding, buffer zeroing) — minor KDF label ambiguity noted in doc 2.
- IP extraction (no proxy-header parsing; relies on `ForwardedHeadersMiddleware`), options validators (`ValidateOnStart` wired), DI lifetimes, `Decorate` mechanics.
- `IntegrityWriteBatcher` / `IntegrityReconciliationService` outbox + lease semantics.
- Latest migration vs. model snapshot vs. entity configurations — consistent.
