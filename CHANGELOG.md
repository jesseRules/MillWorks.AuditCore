# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.6.1] - 2026-04-23

### Fixed
- **Nested transaction in strict-mode tamper detection** — `TamperDetectionService.CreateIntegrityRecordAsync` and `CreateIntegrityRecordBatchAsync` now detect an outer transaction already active on the shared `DbContext` and join it instead of opening a nested `ExecuteInTransactionAsync`. In v1.6.0 the unconditional inner transaction was rejected by EF ("connection already in a transaction") whenever `AuditLogger.LogAsync` wrapped event+integrity in a single atomic transaction (`EnableTamperDetection = true, EnableBatchedIntegrityWrites = false`). The failed lambda stranded the already-added `AuditEventEntity` in the change tracker and `ResilientAuditLogger`'s retry hit an identity-map conflict on the same `EventId`, cascading through the retry budget. The fix checks `IAuditIntegrityRepository.CurrentTransaction` and binds `sp_getapplock` to the existing transaction when present; when no outer transaction exists (e.g. batched-integrity path, direct callers), the service opens its own as before. Added `AuditLoggerTamperNestedTransactionTests` (SQLite) as a regression canary.

## [1.6.0] - 2026-04-23

### Added
- **Fail-closed EF audit policy** (ProdHardening Phase 4) — `AuditFailureMode`, `AuditOptions.FailureMode`, `AuditIntegrityException`, `IAuditFailurePolicy`, and the default `RegulatedEntityFailurePolicy` let EF `SaveChanges` audit failures stay permissive by default or fail closed for regulated entities / all entities.
- **Request-audit overflow policy** (ProdHardening Phase 5) — `RequestAuditOverflowPolicy` and `AuditMiddlewareOptions.OverflowPolicy` add explicit `Throw`, `DropAndLog`, and `RouteToDeadLetter` modes for bounded request-audit queue saturation.
- **Request-audit DLQ and shutdown-drain coverage** (ProdHardening Phase 5) — `InProcessRequestAuditDispatcher` now accepts an optional `IAuditDeadLetterQueue`, routes configured overflow to DLQ, drains queued events on host stop, and exposes request-dispatcher diagnostics counters for enqueue timeout, DLQ routing, and shutdown drain.
- **SQL Server Testcontainers lane** (ProdHardening Phase 6) — added a dedicated `Integration.SqlServer` test lane using `Testcontainers.MsSql` and `Respawn`, plus the `SQL Integration` GitHub Actions workflow for SQL Server scenario coverage.

### Changed
- **Runtime EF schema selection** (ProdHardening Phase 3) — `EntityFrameworkOptions.Schema` moved into the EntityFramework package and now drives `AuditApplicationDbContext` through `HasDefaultSchema(...)`; entity-level hard-coded `Schema = "audit"` attributes were removed and `AuditModelCacheKeyFactory` keys compiled models by configured schema.
- **Audit interceptor failure handling** (ProdHardening Phase 4) — `AuditSaveChangesInterceptor` now consults `IAuditFailurePolicy`, increments `InterceptorAuditFailureCount`, and rethrows `AuditIntegrityException` when the configured failure mode requires fail-closed behavior.
- **Integrity append serialization** — `TamperDetectionService` now serializes hash-chain appends inside the write transaction with SQL Server `sp_getapplock` (`@LockOwner = 'Transaction'`, resource `audit:integrity:append`) instead of the general-purpose `IAuditDistributedLockService`. The prior distributed lock was either process-local (the in-memory default — ineffective across API replicas) or Redis-leased with a 5 s TTL that could expire mid-critical-section; both allowed concurrent writers to collide on `IX_AuditIntegrity_SequenceNumber`. The applock is bound to the transaction, so it auto-releases on commit / rollback / connection drop and never expires mid-write. Consumers on non-SQL-Server providers (SQLite test harness) are serialized by a process-local semaphore gated on `IAuditIntegrityRepository.SupportsCrossProcessAppendLock`. The retry loop remains as defense-in-depth but should essentially never fire after this change.
- **Integrity sequence assignment** (ProdHardening Phase 6) — `AuditIntegrityEntity.SequenceNumber` is now application-assigned inside the applock-serialized transaction instead of SQL Server identity-generated.
- `IAuditIntegrityRepository.GetNextSequenceNumberAsync` is no longer marked obsolete; explicit sequence assignment is safe when called under the integrity append lock.

### Fixed
- `TamperDetectionService.CreateIntegrityRecordBatchAsync` no longer relies on SQL Server preserving input order when assigning identity values during batched inserts. The SQL Server 10k tamper-chain test exposed that `MERGE ... OUTPUT` identity ordering can differ from writer input order, causing verifier order-by-`SequenceNumber` checks to fail. The service now assigns contiguous `SequenceNumber` values in writer order and caches both the previous hash and max sequence.

### Breaking Changes
- `AuditSaveChangesInterceptor` gained optional `AuditFailureMode` and `IAuditFailurePolicy` constructor parameters. Default behavior remains permissive for existing DI construction.
- `TamperDetectionService` construction no longer accepts an `IAuditDistributedLockService` parameter; the integrity-write path uses SQL Server `sp_getapplock` via `IAuditIntegrityRepository.AcquireAppendLockAsync` instead. The lock service interface is still registered and used by other callers (e.g. `DeadLetterQueueProcessor`). Consumers constructing `TamperDetectionService` manually must drop the lock-service argument.
- `IAuditIntegrityRepository` gained `SupportsCrossProcessAppendLock` and `AcquireAppendLockAsync`. Custom implementations must return `true` from the property on providers where the applock is a real cross-process lock (otherwise the caller takes a process-local semaphore).
- The greenfield EF migration baseline was regenerated as a single `Init` migration after changing `AuditIntegrityEntity.SequenceNumber` to application-assigned. Existing experimental databases created from earlier migrations should be dropped and recreated.
- Custom SQL Server schemas remain fresh-database-only: changing `EntityFrameworkOptions.Schema` does not migrate an existing `audit` schema deployment into another schema.

## [1.5.5] - 2026-04-23

### Fixed
- `IAuditDistributedLockService` now registered as **Singleton** (was Scoped). The in-memory implementation's backing dictionary was an instance field, so under the Scoped registration every DI scope got its own empty lock table and concurrent audit writers within one process could both acquire the same named resource instantly. This defeated the integrity-chain critical section in `TamperDetectionService` and produced a duplicate-key race on `AuditIntegrity.SequenceNumber` whenever DB latency was low enough (localhost Docker; masked by Azure SQL round-trip time). Fix: register as singleton; also moved `InMemoryDistributedLockService._locks` to a `static` field as defense-in-depth so the lock serializes process-wide regardless of how it's registered.
- Removed the process-static chain-head cache (`_cachedPreviousHash` / `_cachedMaxSequenceNumber` / `_previousHashCacheInitialized`) from `TamperDetectionService`. The cache was stale across multiple processes — another instance could advance the chain between lock acquisitions and the stale cache would drive a duplicate-key retry on the next write. Both `CreateIntegrityRecordAsync` and `CreateIntegrityRecordBatchAsync` now read the chain head from the database under the distributed lock on every iteration. Costs one extra DB roundtrip per integrity write; eliminates the cross-instance staleness window.
- Removed `TamperDetectionService.ResetPreviousHashCache` (test-only helper) and all test call sites — no longer needed once the cache is gone.
- `ConfigureMapster` no longer constructs a fresh `TypeAdapterConfig` and registers it via `AddSingleton`. Consumer pipelines register their Mapster configs against `TypeAdapterConfig.GlobalSettings` and expose `IMapper` from that instance; AuditCore's fresh-config registration won last-writer-wins on single-service DI resolution, silently dropping every consumer mapping not defined in AuditCore. Fix: apply `AuditMappingConfiguration` onto `TypeAdapterConfig.GlobalSettings` (the same instance every other library's `IRegister` targets) and register via `TryAddSingleton` so any consumer-owned `TypeAdapterConfig` registration wins. Structurally identical to the 1.5.4 `IConnectionMultiplexer` fix.

### Removed
- `SecurityOptions.RedisConnectionString` and the corresponding `SecurityOptionsValidator` rule that required it when `UseRedisLocking = true`. After 1.5.4 moved `IConnectionMultiplexer` ownership to the consumer, nothing in AuditCore read the options-level connection string anymore. The field became a dead hint that still triggered a validator failure if left unset.

### Changed
- README `UseSecurity` example and `docs/ACEDProductionConfiguration.md` reworked to show consumer-side `services.AddSingleton<IConnectionMultiplexer>(...)` registration instead of the removed `RedisConnectionString` option. The ACED sample configuration JSON no longer includes `"RedisConnectionString"`; the connection string now lives on the consumer side under whatever configuration key the app prefers (e.g. `ConnectionStrings:Redis`).

### Breaking Changes
- Consumers that set `security.RedisConnectionString = "..."` in a `UseSecurity(...)` callback or bound `Audit:RedisConnectionString` from configuration must remove those references. The Redis connection string now lives on the consumer's own `IConnectionMultiplexer` registration.
- `IAuditDistributedLockService` is now a singleton. Consumers that resolved it explicitly from a scope and relied on scoped lifetime semantics (e.g. disposing it per request) must adjust — the implementation doesn't hold per-request state, so singleton is safe for both the in-memory and Redis backings.

## [1.5.4] - 2026-04-23

### Fixed
- `UseSecurity` no longer registers `IConnectionMultiplexer` — that registration is now the consumer's responsibility when `SecurityOptions.UseRedisLocking = true`. The previous factory threw `InvalidOperationException("Redis locking is disabled.")` whenever it was resolved with `UseRedisLocking = false`, which crashed unrelated consumer components (token caches, rate limiters, token blacklists, SSO cache invalidation, login rate limiters) that depend on `IConnectionMultiplexer` directly — the AuditCore registration won last-writer-wins for single-service resolution whenever `AddMillWorksAudit(...)` ran after the consumer's own `services.AddSingleton<IConnectionMultiplexer>(...)`.
- `IAuditDistributedLockService` factory now resolves `IConnectionMultiplexer` optionally via `sp.GetService<>()`. When `UseRedisLocking = true` and no `IConnectionMultiplexer` is registered, it throws a clear `InvalidOperationException` naming the missing registration. When `UseRedisLocking = false`, it falls through to `InMemoryDistributedLockService` without touching `IConnectionMultiplexer` at all.

### Breaking Changes
- Consumers that previously relied on `UseSecurity` auto-registering `IConnectionMultiplexer` from `SecurityOptions.RedisConnectionString` must now register `IConnectionMultiplexer` themselves (e.g. `services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect("..."))`) before calling `AddMillWorksAudit(...)`. Apps with `UseRedisLocking = false` are unaffected.

## [1.5.0] - 2026-04-17

### Added
- `IAuditEventRepository.StreamByDateAsync(predicate, ct)` — streams audit events matching a predicate in ascending `InsertedDate` order without buffering the full result set; backed by `DbSet.AsNoTracking().AsAsyncEnumerable()` for bulk-export / archival scenarios
- `IAuditIntegrityRepository.StreamByEventIdsAsync(eventIds, ct)` — streams integrity records for a list of event IDs in chunked `IN`-clause queries so the relational parameter limit is respected
- `CountingHashingStream` — write-side pass-through `Stream` that feeds bytes into `IncrementalHash` and tallies the count as they flow; used by the archival pipeline to hash compressed bytes while streaming to blob storage
- `HashingReadStream` — read-side counterpart that feeds bytes into `IncrementalHash` as they flow from blob storage through decompression and JSON parsing on the restore path

### Changed
- **Archival now streams** — `AuditArchivalService.ArchiveAuditEventsAsync` rewritten to pipe events from `StreamByDateAsync` through `Utf8JsonWriter` → `GZipStream` → `CountingHashingStream` → `System.IO.Pipelines.Pipe` → `BlobClient.UploadAsync`. Back-pressure at 4 MiB keeps the pipe buffer small; peak managed allocation is now independent of archive size. The perf test's memory guardrail tightened from `< 6× raw` to `< 1.5× raw` and passes. Event deletion after upload switched from `DeleteRangeAsync` on loaded entities to `ExecuteDeleteWhereAsync(e => ids.Contains(e.EventId))`, so the delete transaction no longer carries the payload
- **Restore now streams** — `AuditArchivalService.RestoreArchivedEventsAsync` rewritten to pipe blob bytes from `BlobClient.OpenReadAsync` through `HashingReadStream` → `GZipStream` → a hand-rolled `Utf8JsonReader` state machine that deserializes one event / integrity record at a time and persists them via `AddRangeAsync` in 500-record batches with `ClearChangeTrackerAsync` between batches, all inside one `ExecuteInTransactionAsync`. Hash verified incrementally on the compressed bytes; mismatch throws to roll back the transaction and mark the archive `Corrupted`. Previous shape held the full compressed bytes, decompressed bytes, UTF-8 string, and object graph in memory simultaneously (~4× decompressed size)
- Archive JSON restore now rejects malformed top-level payloads (null, array, scalar) explicitly rather than silently restoring zero events
- `RowVersion` on `AuditAggregateRoot` is now registered as an EF concurrency token so `Repository<T>.ExecuteOptimisticUpdateAsync`'s retry path is actually exercised under real provider semantics (Phase 1)
- `IntegrityReconciliationService` now acquires a distributed lease before scanning stale pending work, so multi-instance deployments can't reconcile the same rows in parallel (Phase 1)
- Background services inject timing providers, validate required scoped dependencies in `StartAsync`, and log cycle timing explicitly — replacing the hard-coded startup delays and placeholder tests (Phase 2)
- `DeadLetterQueueProcessor` acquires a distributed reprocessing lock before draining failed events, eliminating duplicate-replay races across instances (Phase 2)
- `IntegrityHealthCheck` propagates cancellation as a first-class path and returns sanitized unhealthy results instead of raw exception objects that leaked connection detail through health endpoints (Phase 3)
- `AuditContextMiddleware` validates incoming correlation IDs, warns on malformed values, and falls back to a guaranteed non-empty trace identifier when the header is empty or injected (Phase 3)

### Fixed
- `SensitiveContentSanitizer` now matches `values\s*\(…\)` alongside `key value is\s*\(…\)` in SQL error strings; previously the regex missed the `Cannot insert values ('…')` shape and let PHI through (Phase 4)
- `SensitiveContentSanitizer` now detects hyphen-free SSNs (`123456789`), US + international phone numbers, and Luhn-valid credit card prefixes — closing PHI/PII coverage gaps called out in the Phase 4 plan (Phase 4)
- `SensitiveContentSanitizer` truncation now stays within the caller-specified `maxLength`; previously the `"...[truncated]"` suffix caused up to 14 bytes of overflow past the documented cap (Phase 4)
- Pin `Azure.Identity` to 1.21.0 to resolve the `Azure.Core` 1.53 / `Azure.Identity` 1.14.x `DefaultAzureCredential` ambiguous-reference compile error that otherwise blocks the solution build (Phase 5)

### Breaking Changes
- `IAuditEventRepository` and `IAuditIntegrityRepository` gain new abstract methods (`StreamByDateAsync`, `StreamByEventIdsAsync`). Consumers who implement these interfaces directly (rather than depending on the shipped `Repository<T>` via DI) must add the new methods. Apps that wire up repositories through `UseEntityFramework(...)` are unaffected.
- Restore now rejects a top-level `null` / array / scalar JSON archive as a failure instead of silently restoring zero events. Archives written by 1.4.x are valid objects and restore unchanged; only hand-crafted or corrupted payloads are affected.

## [1.4.2] - 2026-04-12

### Fixed
- `UseRequestAuditDispatcher<TDispatcher>()` now registers custom dispatchers as `Scoped` instead of `Singleton`
- Consumer integrations that inject scoped job infrastructure such as `IBackgroundJobClient` no longer fail DI validation at application startup

## [1.4.1] - 2026-04-12

### Fixed
- `UseRequestAuditDispatcher<TDispatcher>()` now removes the default in-process request-audit worker registrations when a consuming app supplies its own dispatcher
- Consumer-owned dispatcher scenarios (for example, bridging to `MillWorks.BackgroundJobs`) no longer keep the default in-process hosted worker running unnecessarily

## [1.4.0] - 2026-04-12

### Added
- `AuditMiddlewareOptions` for request-audit middleware behavior:
  - `ExcludedReadPaths`
  - `AuditWritesOnly`
  - `QueueCapacity`
  - `EnqueueTimeout`
  - `DrainTimeout`
- `IRequestAuditDispatcher` as the public extension point for deferred HTTP request-audit dispatch
- `IRequestAuditProcessor` as the public processing contract for consumer-owned background job systems
- `InProcessRequestAuditDispatcher` as the default bounded in-memory queue + hosted worker implementation
- `RequestAuditProcessor` as the default scoped persistence handler for deferred request audits
- `MillWorksAuditBuilder.UseMiddleware(...)` to configure request-audit middleware options
- `MillWorksAuditBuilder.UseRequestAuditDispatcher<TDispatcher>()` to let consuming apps replace the default in-process dispatcher with their own job bridge

### Changed
- `AuditContextMiddleware` no longer persists HTTP request audit events inline via request-scope `ICustomAuditScope.DisposeAsync()`
- Request-level HTTP audit events are now dispatched off the request thread as completed `AuditEvent` instances
- The default request-audit path now uses a hosted worker that creates a fresh DI scope per deferred event before resolving scoped services such as `IAuditLogger`
- README and incident documentation updated to describe deferred request auditing and consumer-owned dispatcher integration

### Fixed
- Eliminated the request teardown coupling that could block HTTP responses on best-effort request-audit persistence
- Replaced the earlier hard-coded guidance for fire-and-forget scope disposal with a scoped-safe deferred processing model
- Preserved a clean consumer extension point for external job systems such as `MillWorks.BackgroundJobs` without taking a hard dependency on them

## [1.3.1] - 2026-04-02

### Added
- 176 new unit tests covering previously untested code paths (1,621 → 1,797 total; 4 skipped)
  - TamperDetectionService: digital signature create/verify round-trips, RSA key loading errors, constructor validation, `LogTamperAlertAsync` structure, cancellation propagation (17 tests)
  - AuditArchivalService: compression/decompression round-trips, hash verification in restore and validate paths, blob upload/download failures, corrupted GZip handling, deserialization failure, audit event emission failure (13 tests)
  - FieldEncryptionService: `ReEncryptFieldAsync` error paths, key provider failures during decrypt, invalid Base64/JSON payloads, sync `CryptographicException` handling, field name edge cases (18 tests)
  - PciDssValidator: all 12 requirement branches (pass and fail), keyword-specific filters, `GenerateRecommendations` high/medium severity sections (46 tests)
  - Iso27001Validator: all 7 validation rules (pass and fail), `GenerateRecommendations` all severity sections (24 tests)
  - HipaaValidator: false paths for activity review, automatic logoff, login monitoring, authorization tracking; emergency access keywords; `GenerateRecommendations` high/medium/low sections (25 tests)
  - StigValidator: 3-4 category severity ternary, `BuildMissingCategoryRecommendations` helper, `GenerateRecommendations` low severity section (6 tests)
  - DuplicateKeyDetector: all 3 database provider branches (SQL Server, SQLite, PostgreSQL) plus default path (9 tests)
  - FieldKeyDerivation: determinism, uniqueness per field/version, output size for both overloads (9 tests)
  - AuditQueryServiceWithMetaTracking: all 6 query methods with delegation and count verification (9 tests)
- Un-skipped 2 flaky DLQ statistics tests that now pass reliably (skipped count 6 → 4)

### Fixed
- `AuditIntegrityDto.Id` had `[JsonPropertyName("event_id")]` colliding with `EventId` — changed to `[JsonPropertyName("id")]`. This caused `JsonSerializer.Deserialize<AuditArchive>` to throw on .NET 10 during archive restore. The `Id` property is unused in the codebase.

### Changed
- Adjusted line coverage (excluding migrations) from 86.5% to ~90%+; Services assembly from 86.7% to 91.1% line / 79.9% to 84.0% branch

## [1.3.0] - 2026-04-01

### Added
- `SensitiveContentSanitizer` — internal utility that scrubs known sensitive patterns (connection strings, bearer tokens, emails, SSNs, SQL key values) from free-text fields via regex; used by `DefaultAuditFieldRedactor` and `ExceptionDiagnosticHelper`
- `IAuditFieldRedactor.RedactPropertyNames()` — default interface method that filters changed property names against a sensitive-name denylist (healthcare/PHI, auth/secrets, financial/PII, FERPA)
- `IAuditFieldRedactor.RedactKeyValues()` — default interface method that redacts string-typed entity key values (natural keys) while preserving numeric and GUID surrogate keys
- `InterceptorRedactionBoundaryTests` — integration test verifying that fields marked sensitive via `[SensitiveData]`/`[EncryptedField]` attributes are not in `DefaultAuditFieldRedactor.SafeFields`
- In-memory index for file-based DLQ — `ConcurrentDictionary` built lazily on first access; statistics, lookups, and capacity checks are now O(1) instead of O(n) directory scans

### Changed
- `ErrorMessage` removed from `DefaultAuditFieldRedactor.SafeFields` — now routed through `SensitiveContentSanitizer` instead of passing through unredacted; safe diagnostic content (e.g., "Timeout expired") is preserved
- `AuditEventRedactionHelper.RedactEvent` now redacts `ChangedProperties` (via `RedactPropertyNames`), `KeyValues` (via `RedactKeyValues`), and `SystemFields` (via `RedactFields`)
- `ExceptionDiagnosticHelper.GetTruncatedMessage` now sanitizes sensitive patterns before truncation
- `IntegrityWriteBatcher` drain loop replaced `Task.Delay(1)` polling with `Channel.Reader.WaitToReadAsync` — zero-polling, event-driven batching
- File DLQ read operations no longer hold `SemaphoreSlim` — reads use `ConcurrentDictionary` index; only writes acquire the lock
- `AuditSaveChangesInterceptor.MaskOrRedact` XML documentation updated to reference the interceptor/redactor boundary

### Breaking Changes
- `ResilienceOptions.ProcessedRetention` default changed from 7 days to 24 hours to minimize sensitive payload retention. Set `ProcessedRetention = TimeSpan.FromDays(7)` to restore previous behavior.
- `ErrorMessage` field values are now sanitized (not passed through) by `DefaultAuditFieldRedactor`. Audit consumers that relied on raw error messages in the audit store will see `[SANITIZED]` replacing connection strings, tokens, and other sensitive patterns.

## [1.2.0] - 2026-04-01

### Added
- `ArchiveCreationBackgroundService` — scheduled archive creation driven by `ArchivalOptions.RetentionDays` and `ArchivalOptions.ArchivalIntervalHours`; registered alongside verification when `EnableBackgroundArchival` is true
- JSON and CSV export formats in `GenerateAuditReportAsync`; unsupported formats now throw `NotSupportedException` instead of silently returning placeholder text
- Integrity-chain fields on `AuditIntegrityDto`: `EventHash`, `PreviousEventHash`, `HmacSignature`, `Checksum`, `AlgorithmVersion`, `DigitalSignature`, `TrustedTimestamp`, `SequenceNumber`, `Parameters`

### Changed
- `UseSecurity()` now registers `IAuditDistributedLockService` — `RedisDistributedLockService` when Redis is configured, `InMemoryDistributedLockService` otherwise; `TamperDetectionService` no longer silently falls back to `NullDistributedLockService`
- Archive restore (`RestoreArchivedEventsAsync`) now maps all integrity-chain fields via Mapster instead of only `EventId`, preserving hash chain, HMAC, checksum, and digital signature data through archive/restore cycles
- `ArchiveVerificationBackgroundService` now reads scheduling config from injected `ArchivalOptions` instead of ad-hoc `IConfiguration` keys
- `GenerateAuditReportAsync` default format changed from `"pdf"` to `"json"`

### Breaking Changes
- Default `IAuditFieldRedactor` is now `DefaultAuditFieldRedactor` (safe-by-default: masks all non-structural fields). Previously `PassThroughAuditFieldRedactor` which performed no redaction. Consumers who need unredacted audit storage must either register a custom `IAuditFieldRedactor` or set `AllowPassThroughRedactor = true`.
- `PassThroughAuditFieldRedactor` is now rejected in **all** environments (not just Production) unless `AllowPassThroughRedactor = true`. Previously, non-Production environments allowed it silently.

### Fixed
- CSV export guards against formula injection (cells starting with `=`, `+`, `-`, `@`)
- README: replaced "IRB" with "NIST" in the compliance list to match implemented standards
- README: clarified that encrypted fields are redacted in audit snapshots (not stored encrypted)
- README: changed "full-text search" to "multi-field text search"
- README: clarified that background archival now includes both creation and verification
- README: noted JSON/CSV export support in query and reporting section

## [1.1.0] - 2026-03-31

### Added
- `AuditEventRedactionHelper` — shared helper that applies `IAuditFieldRedactor` to `AuditEvent` before DLQ/emergency storage, closing the failure-path redaction bypass (F2/F8)
- `ExceptionDiagnosticHelper` — replaces raw stack trace persistence with truncated message + exception type; full traces gated behind `ResilienceOptions.IncludeStackTraces` (default: off) (F3)
- `ResilienceOptions.ProcessedRetention` — configurable retention period for processed DLQ artifacts (default: 7 days)
- `ResilienceOptions.FileBasedMaxQueueSize` — maximum event count for file DLQ with warning on overflow (default: 1000); documents file DLQ as explicitly small-volume
- Startup validation for file DLQ: write probe verifies directory is writable at construction time, fails fast instead of silently (F10)
- Startup validation for Redis DLQ: logs error if `IConnectionMultiplexer` is not connected at construction time (F10)
- Diagnostic logging in `AuditSearchService.GetFieldValue<T>()`, `AuditComplianceService.AnonymizeJsonData()`, and `AuditSaveChangesInterceptor.BuildFerpaAdditionalData()` — bare `catch` blocks now log warnings instead of silently swallowing exceptions (F9)
- Redis DLQ replay: `ReprocessEventAsync` now calls `IAuditLogger.LogAsync()` via `IServiceScopeFactory` and only marks processed after replay succeeds (F4)
- `InternalsVisibleTo` from Services project to Tests project for `TamperDetectionService.ResetPreviousHashCache()` test isolation

### Changed
- **DLQ redaction** — both file and Redis DLQ implementations now apply `IAuditFieldRedactor` to `AuditEvent` before storage, ensuring failure-path data is redacted consistently with the success path (F2)
- **Synthetic event redaction** — `ResilientAuditLogger` operation failure handlers redact `metadata`, `result`, and `ex.Message` before DLQ storage (F8)
- **Tamper detection performance** — `TamperDetectionService` caches the previous event hash in a static field, eliminating the `GetLatestBySequenceAsync` DB query from the critical section in steady state; only the first call or a duplicate-key retry reads from the database (F5, Step 10)
- **Redis DLQ storage model** — restructured from full JSON payloads as sorted set members (O(n) scans) to a hash+index model: sorted set stores event IDs as time-ordered index, hash stores payloads by ID; `GetEventByIdAsync` and `RemoveEventByIdAsync` are now O(1), batch retrieval uses single `HMGET` round-trip (F6, Step 11)
- **File DLQ indexing** — simplified filename convention from `dlq_{id}_{timestamp}.json` to `dlq_{id}.json`; `GetFilePathForEvent` and `SaveDeadLetterEventAsync` construct paths directly instead of scanning directories; `ReadFailedEventsInternalAsync` sorts then slices before reading files (F6, Step 12)
- `DeadLetterAuditEvent.ExceptionStackTrace` is now null by default instead of containing the full stack trace

### Fixed
- Redis DLQ `ReprocessEventAsync` previously marked events as processed and removed them without replaying through `IAuditLogger` — reprocessing now actually replays the event (F4)
- Redis DLQ `UpdateDeadLetterEventAsync` previously did a remove+re-store (two O(n) scans) — now a single O(1) hash set

### Breaking Changes
- Redis DLQ storage format changed from sorted-set-member payloads to hash+index. Existing Redis DLQ data stored under the old model will not be readable. Drain the queue before upgrading or accept data loss for in-flight DLQ entries.
- File DLQ filename convention changed from `dlq_{id}_{timestamp}.json` to `dlq_{id}.json`. Existing files with the old naming pattern will still be found by `ReadFailedEventsInternalAsync` (glob is `dlq_*.json`) but `GetFilePathForEvent` will not locate them by ID. Reprocess or purge existing events before upgrading.

## [1.0.4] - 2026-03-23

### Added
- `IAuditFieldRedactor` interface for redacting PHI/PII/credentials before audit persistence, with `RedactFields()`, `RedactValue()`, and `RedactTarget()` methods
- `PassThroughAuditFieldRedactor` default no-op implementation registered automatically
- `LogBatchAsync()` on `IAuditLogger` for atomic batch audit logging with `BatchAuditResult` return type
- `DuplicateKeyDetector` for provider-agnostic duplicate key detection across SQL Server, SQLite, and PostgreSQL
- Batch integrity record creation (`CreateIntegrityRecordBatchAsync`) on `ITamperDetectionService`
- Generic `IRepository<T>` interface extensions for the repository layer

### Changed
- Tamper detection now pre-computes event hashes, HMAC signatures, checksums, and digital signatures **outside** the distributed lock, reducing lock hold time from ~5–20 ms to ~1–3 ms
- `ResilientAuditLogger` applies field redaction to emergency fallback files and no longer serializes full event objects (potential PHI) into structured log messages
- `AuditLogger` redacts `AuditTarget` before JsonData serialization to prevent entity snapshot PHI leaks
- `AuditLogger` propagates `OperationCanceledException` without logging it as an error
- `AuditLogger.SanitizeString` uses zero-allocation loop scan instead of LINQ `Any()` for control character detection
- `AuditEventFactory` caches `Environment.MachineName`, `UserDomainName`, and `Culture` as static fields; uses compiled expression delegates for entity ID extraction instead of per-event reflection
- `AuditSaveChangesInterceptor` static fields renamed to `_camelCase` convention
- Refactored `AuditArchivalService` and `AuditComplianceService` for clarity and reduced complexity
- Refactored `Repository<T>` with expanded generic interface methods

### Fixed
- Removed `MaxLength` constraint on `AuditEventEntity.JsonData` column that caused truncation of large audit payloads (EF migration `RemoveJsonDataMaxLength`)
- Fixed tamper detection retry logic to use `Random.Shared` instead of allocating a new `Random` per call

### Removed
- `ValidationSeverityExtensions` (unused)
- `AuditConfigurationSettings` (superseded by options pattern)
- `FIELD_ENCRYPTION_GUIDE.md` and `RefactorPlan.md` from source tree
- Unused `UserAuditProvider` property-mapping overrides

## [1.0.0] - 2026-03-15

### Added
- Automatic entity change auditing via EF Core SaveChanges interceptor
- Fluent configuration API with `AddMillWorksAudit()` builder pattern
- Tamper-evident audit trail with cryptographic hash chaining
- Field-level AES-256-GCM encryption with Azure Key Vault or file-based key storage
- Multi-standard compliance validation (GDPR, SOC2, HIPAA, ISO 27001, FERPA, PCI-DSS, STIG)
- FERPA consent verification with distributed cache support
- Dead letter queue with automatic retry for failed audit events
- Distributed locking via Redis for multi-instance deployments
- Audit event archival to Azure Blob Storage with integrity verification
- Custom audit providers per entity type with property-level masking
- Comprehensive query, search, and reporting services
- Security event tracking and alerting
- Background maintenance services for cleanup and archive verification
- SQLite-based integration test suite (1000+ tests)

[Unreleased]: https://github.com/jesserules/millworks.auditcore/compare/v1.5.0...HEAD
[1.5.0]: https://github.com/jesserules/millworks.auditcore/compare/v1.4.2...v1.5.0
[1.3.1]: https://github.com/jesserules/millworks.auditcore/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/jesserules/millworks.auditcore/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/jesserules/millworks.auditcore/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/jesserules/millworks.auditcore/compare/v1.0.4...v1.1.0
[1.0.4]: https://github.com/jesserules/millworks.auditcore/compare/v1.0.0...v1.0.4
[1.0.0]: https://github.com/jesserules/millworks.auditcore/releases/tag/v1.0.0
