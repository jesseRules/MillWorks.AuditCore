# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.1.0]: https://github.com/jesserules/millworks.auditcore/compare/v1.0.4...v1.1.0
[1.0.4]: https://github.com/jesserules/millworks.auditcore/compare/v1.0.0...v1.0.4
[1.0.0]: https://github.com/jesserules/millworks.auditcore/releases/tag/v1.0.0
