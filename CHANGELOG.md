# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.0.4]: https://github.com/jesserules/millworks.auditcore/compare/v1.0.0...v1.0.4
[1.0.0]: https://github.com/jesserules/millworks.auditcore/releases/tag/v1.0.0
