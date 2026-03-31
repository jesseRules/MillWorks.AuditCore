# Architecture

This document describes the internal design of MillWorks.AuditCore: project structure, dependency graph, data flow through the interceptor and compliance pipeline, tamper detection mechanics, the extension model for custom providers, and the security model.

## Project Dependency Graph

```
MillWorks.AuditCore.Abstractions
  |  Pure .NET library. No EF Core, no ASP.NET Core dependencies.
  |  Contains: Models (AuditEvent, AuditEnvironment, AuditTarget), DTOs,
  |  Interfaces (IAuditContext, IAuditEventFactory, IAuditLogger,
  |  IFieldEncryptionService, IEncryptionKeyProvider, IConsentVerificationService),
  |  Enums (AuditAction), Constants (FERPA/HIPAA event types),
  |  Requests, Responses, Exceptions, Canonicalization utilities.
  |
  v
MillWorks.AuditCore.EntityFramework
  |  EF Core data layer. References Abstractions only.
  |  Contains: AuditApplicationDbContext with schema configuration,
  |  Entity classes (AuditEventEntity, AuditLogEntity, AuditIntegrityEntity,
  |  AuditArchiveRecordEntity, AuditSecurityEventEntity),
  |  Primitive base classes (AuditAggregateRoot, AppendOnlyEntity, AuditEntity),
  |  AuditSaveChangesInterceptor, Repository<T> base class,
  |  Concrete repositories, EF Migrations,
  |  Value converters for field-level encryption, ModelBuilder extensions.
  |
  v
MillWorks.AuditCore.Providers
  |  Entity-specific audit enrichment. References Abstractions and EntityFramework.
  |  Contains: IAuditProvider interface, BaseAuditProvider abstract class,
  |  Concrete provider implementations (e.g., UserAuditProvider).
  |
  v
MillWorks.AuditCore.Services
  |  Business logic. References Abstractions, EntityFramework, and Providers.
  |  Contains: AuditService, AuditLogger (core logging pipeline),
  |  TamperDetectionService (hash chain, HMAC signing, chain verification),
  |  Compliance validators (GDPR, HIPAA, FERPA, SOC2, ISO 27001, PCI-DSS, STIG),
  |  AuditComplianceService (report generation, anonymization, data export),
  |  FieldEncryptionService + key providers (Azure Key Vault, file-based),
  |  Dead letter queue (InMemory, FileSystem, Redis) + background processor,
  |  Query/Search/Report services, Archival service,
  |  Distributed locking (Redis, InMemory, Null fallback),
  |  AuditContextMiddleware, Mapster mapping configuration, Options classes.
  |
  v
MillWorks.AuditCore.AspNetCore
     Application entry point. References all projects above.
     Contains: MillWorksAuditBuilder (fluent configuration API),
     ServiceCollectionExtensions (AddMillWorksAudit),
     EncryptionConfigurationExtensions (UseFieldEncryption),
     ProviderRegistry, AuditOptions, Middleware pipeline integration.
```

Each layer references only the layers below it. This prevents circular dependencies and allows lower layers to be used independently. For example, `Abstractions` can be referenced from a console application or Azure Function that only needs to construct `AuditEvent` objects for submission to an external audit service, without pulling in EF Core or ASP.NET Core.

## Design Principles

### Separation of Concerns

The project enforces strict layering:

- **Abstractions** defines contracts. It has no implementation dependencies beyond the .NET BCL.
- **EntityFramework** owns all database access. No other project writes SQL or calls `DbContext` directly.
- **Services** contains business rules. It depends on repository interfaces, not on `DbContext`.
- **AspNetCore** is the composition root. It is the only project that knows about all other projects simultaneously.

### Circular Dependency Prevention

The EF Core interceptor captures entity changes during `SaveChangesAsync`. If the interceptor wrote audit records through the normal `IAuditLogger` pipeline, which itself calls `SaveChangesAsync`, an infinite loop would result. This is resolved through two mechanisms:

1. **Type exclusion set**: The interceptor maintains a `HashSet<Type>` of audit-owned entity types. Any `EntityEntry` whose `ClrType` appears in this set is skipped during change detection.

2. **DbContext bypass for audit entities**: `AuditApplicationDbContext.SaveChanges()` / `SaveChangesAsync()` detect when audit entities are being saved and temporarily set the bypass flag so the interceptor does not recurse while persisting `AuditEventEntity`, `AuditIntegrityEntity`, `AuditLogEntity`, `AuditArchiveRecordEntity`, or `AuditSecurityEventEntity`.

`InternalAuditEventRepository` still exists as an internal helper around audit-event persistence, but the interceptor's `AuditLogEntity` path currently writes directly to `context.Set<AuditLogEntity>()` and relies on the exclusion set plus the DbContext bypass behavior to prevent recursion.

### Fail-Safe Audit Logging

Audit logging is designed to avoid breaking the application's primary operation when resilience is enabled. The `ResilientAuditLogger` decorator wraps `IAuditLogger`, retries transient failures, and on exhaustion routes failed events to the configured dead letter queue. A background processor can retry dead-lettered events on a configurable schedule.

Important caveats in the current implementation:

- This fail-safe behavior applies to the `IAuditLogger` pipeline, not to every audit-related write path in the system.
- The dead-letter path is operationally useful but not yet fully security-hardened. In the current code, DLQ implementations still persist the original failed event payload and exception stack trace without applying `IAuditFieldRedactor`.
- The emergency fallback file is redacted, but it writes to temp storage using platform-default permissions.

## Interceptor Flow

The `AuditSaveChangesInterceptor` extends EF Core's `SaveChangesInterceptor` and hooks into `SavingChangesAsync`:

```
Application calls DbContext.SaveChangesAsync()
    |
    v
1. Interceptor receives SavingChanges event
    |
    v
2. Materialize auditable EntityEntry objects from ChangeTracker
   - states: Added / Modified / Deleted
   - skip audit-owned entity types
   - skip [NoAudit] types
    |
    v
3. For each entry:
   a. Determine entity type, state, and primary key
   b. Check cached FERPA metadata and other property metadata
   c. For Modified: emit one AuditLogEntity per changed property
   d. For Added / Deleted: emit one AuditLogEntity with a property snapshot in AdditionalData
   e. For [SensitiveData] properties: mask or redact values before persistence
   f. Stamp CorrelationId / IpAddress / UserAgent from AuditApplicationDbContext request metadata
   g. For FERPA entities: include FERPA metadata in AdditionalData
    |
    v
4. Collect all AuditLogEntity objects into a batch
    |
    v
5. Add the AuditLogEntity objects directly to the same DbContext
   via context.Set<AuditLogEntity>().Add(...)
   (these writes are excluded from further interception)
    |
    v
6. Original SaveChangesAsync proceeds
```

Property-level metadata (sensitive data flags, FERPA attributes, no-audit markers) is cached in `ConcurrentDictionary` instances to avoid repeated reflection on hot paths.

### Compliance Enforcement at the Interceptor Level

When `UseCompliance()` is configured with an enforcement mode, the interceptor can block saves that violate compliance rules. For example, if FERPA enforcement is active and a FERPA-annotated entity is being modified without verified consent, the interceptor throws a `ComplianceViolationException` before `SaveChanges` reaches the database.

## Tamper Detection

### Hash Chain Construction

When tamper detection is enabled, `AuditLogger.LogAsync(...)` persists the `AuditEventEntity` and then creates a corresponding `AuditIntegrityEntity` in the same transaction.

Each integrity record receives a database-generated sequence number. For a new event, `TamperDetectionService` currently:

1. Retrieves the most recent `AuditIntegrityEntity` to obtain the previous hash.
2. Canonicalizes the event payload using `AuditCanonicalizer`.
3. Computes the event hash from the canonicalized event data.
4. Computes an HMAC value from immutable event identity fields using the configured HMAC key.
5. Computes a checksum from immutable event fields used for version-independent verification.
6. Optionally computes an RSA digital signature over the event hash when digital signatures are enabled.
7. Stores the `AuditIntegrityEntity` with:
   - `EventHash`
   - `PreviousEventHash`
   - `HmacSignature`
   - `Checksum`
   - `AlgorithmVersion`
   - optional `DigitalSignature`
   - database-generated `SequenceNumber`

The current implementation does not store a separate persisted `ChainHash` column. Chain continuity is represented by each row's `PreviousEventHash` pointer to the prior row's `EventHash`.

Performance note:

- Hashes are pre-computed outside the critical section.
- The critical section is still serialized around "read latest + insert integrity row", so this remains one of the main write-path bottlenecks under concurrency.

### Chain Verification

`VerifyChainIntegrityAsync` walks the integrity table in sequence order and verifies:

- sequence continuity
- `PreviousEventHash` linkage between consecutive rows
- per-event event hash validity
- HMAC validity
- checksum validity
- optional digital signature validity

Any mismatch indicates tampering or corruption. The result includes:

- total events verified
- whether the chain is broken
- list of tampered events
- verification timestamp

`VerifySequenceIntegrityAsync` checks for gaps or duplicates in the sequence number column.

`DetectTamperingAsync` combines both checks and returns a list of `TamperAlert` objects with severity and description.

### Distributed Locking

In multi-instance deployments, concurrent writes to the integrity chain can race on the "latest row" lookup and insert sequence. When distributed locking is enabled, `TamperDetectionService` acquires a Redis-backed lock before reading the previous hash and inserting the new integrity row. If distributed lock acquisition times out, the current implementation falls back to a process-local static lock.

This provides correctness for the common path, but the current fallback behavior prioritizes availability over strong cross-node coordination during partial Redis failure. Duplicate sequence conflicts are retried with exponential backoff and transaction rollback.

## Compliance Pipeline

### Validator Registration

`UseCompliance()` accepts a list of `ComplianceStandard` enum values. For each standard, the corresponding `IComplianceValidator` implementation is registered in DI:

| Standard | Validator Class |
|----------|----------------|
| GDPR | `GdprValidator` |
| HIPAA | `HipaaValidator` |
| FERPA | `FerpaValidator` |
| SOC 2 | `Soc2Validator` |
| ISO 27001 | `Iso27001Validator` |
| PCI-DSS | `PciDssValidator` |
| STIG | `StigValidator` |

### Report Generation

`IAuditComplianceService.GenerateComplianceReportAsync(standard, startDate, endDate)`:

1. Queries all audit events in the date range.
2. Resolves all registered `IComplianceValidator` implementations.
3. Filters to the validator matching the requested standard.
4. Invokes `ValidateAsync(events)` which returns a list of `AuditValidationResult`.
5. Each result contains: rule name, pass/fail, message, severity, compliance standard, category, regulation reference (e.g., "45 CFR SS 164.312(b)"), total count, failed count, and recommendations.
6. Assembles results into a `ComplianceReport` with overall pass/fail and per-rule detail.

### Compliance Attribute Scanner

The `ComplianceAttributeScanner` runs at startup (singleton) and scans configured assemblies for compliance-related attributes (`[FERPA]`, `[SensitiveData]`, etc.). Results are cached and used by validators and the interceptor to determine which entities and properties require special handling.

### Additional Compliance Operations

- **Anonymize User Data**: `AnonymizeUserDataAsync(userId)` -- replaces user-identifying fields in audit records with anonymized placeholders, supporting GDPR right to erasure.
- **Export User Audit Data**: `ExportUserAuditDataAsync(userId)` -- exports all audit records for a user as JSON, supporting GDPR data portability.
- **Validate Retention**: `ValidateRetentionComplianceAsync()` -- checks that audit records are being retained for the configured minimum period.

## Extension Model: Custom Providers

### IAuditProvider Interface

```csharp
public interface IAuditProvider
{
    string EntityType { get; }
    Task<AuditEvent> CreateAuditEventAsync(string action, object? entity, object? oldValues = null);
    Task<bool> ShouldAuditAsync(string action, object entity);
    Task EnrichAuditEventAsync(AuditEvent auditEvent, object? entity);
    Dictionary<string, object?> GetChanges(object? oldValues, object? newValues);
}
```

### BaseAuditProvider

`BaseAuditProvider` provides a default implementation that:

- Uses `IAuditEventFactory` to construct the `AuditEvent`.
- Populates IP address, user agent, and request ID from `IHttpContextAccessor`.
- Computes property-level diffs using cached reflection.
- Calls the virtual `EnrichAuditEventAsync` for subclass customization.

### Writing a Custom Provider

```csharp
public class PatientAuditProvider : BaseAuditProvider
{
    public override string EntityType => "Patient";

    public override Task<bool> ShouldAuditAsync(string action, object entity)
    {
        // Always audit patient records
        return Task.FromResult(true);
    }

    public override Task EnrichAuditEventAsync(AuditEvent auditEvent, object? entity)
    {
        if (entity is Patient patient)
        {
            auditEvent.CustomFields["MRN"] = patient.MedicalRecordNumber;
            auditEvent.CustomFields["Department"] = patient.Department;
            // Mask SSN in audit trail
            auditEvent.CustomFields["SSN"] = "***-**-" + patient.SSN?[^4..];
        }
        return Task.CompletedTask;
    }
}
```

### Registration

```csharp
audit.RegisterProviders(registry =>
{
    registry.AddProvider<PatientAuditProvider>("Patient");
});
```

The `AuditProviderDispatcher` resolves the correct provider for each entity type at runtime using the `AuditProviderTypeMap`.

## Security Model

### Redaction

`AuditLogger` supports field redaction through `IAuditFieldRedactor`:

- `RedactValue(...)` for mapped string columns
- `RedactFields(...)` for `CustomFields`
- `RedactTarget(...)` for the serialized target payload

Important current behavior:

- The default DI registration is `PassThroughAuditFieldRedactor`, which performs no redaction.
- Normal `AuditLogger` persistence uses the redactor.
- The emergency fallback file also uses the redactor.
- The current file and Redis dead-letter queue implementations do not yet apply the redactor before persisting failed events.

As a result, redaction is currently opt-in and failure-path storage is less secure than the normal success path.

### Field-Level Encryption

Properties marked with `[EncryptedField]` or `[SensitiveData(AutoEncrypt = true)]` are encrypted transparently through EF Core value converters registered by `ModelBuilderEncryptionExtensions`.

- **Algorithm**: AES-256-GCM (authenticated encryption with associated data).
- **Key derivation**: Per-field keys are derived from the master key using `FieldKeyDerivation`, ensuring that compromise of one field's ciphertext does not help decrypt another field.
- **Payload format**: `EncryptedFieldPayload` stores the ciphertext, IV, authentication tag, and key version together for self-describing decryption.

### Key Management

Two built-in key providers:

1. **`AzureKeyVaultProvider`**: Retrieves encryption keys from Azure Key Vault. Supports key rotation -- the key version is stored with each encrypted field, so old and new keys coexist.

2. **`FileBasedKeyProvider`**: Stores encryption keys on the local filesystem, encrypted with a master key. Designed for DMZ or air-gapped environments where cloud key management is not available.

Custom key providers can be supplied by implementing `IEncryptionKeyProvider` and passing it to `UseFieldEncryption()`.

### Integrity Signatures

Every integrity record includes an HMAC value when an HMAC key is configured. This allows verification that the integrity record was produced by a party possessing the key, not just that the chain is internally consistent.

When `EnableDigitalSignatures` is enabled, the system also computes an RSA digital signature over the event hash. These are separate mechanisms:

- HMAC: symmetric authenticity/integrity check
- digital signature: asymmetric signature over the event hash

In Production, `Audit:HmacKey` is required. Outside Production, the system can fall back to a process-scoped generated key, which is convenient for development but does not survive restarts.

### Consent Verification

The `IConsentVerificationService` (backed by `IMemoryCache` for synchronous reads in the interceptor path) checks whether a user has granted consent for processing their data. This is used by the FERPA validator and the interceptor's FERPA enforcement path. Consent records are cached to avoid database roundtrips on every `SaveChanges` call.

### Security Event Logging

The `IAuditSecurityEventService` records security-relevant events (tamper alerts, authentication failures, unauthorized access attempts) in the `AuditSecurityEvents` table. These events are separate from the main audit log and are not subject to the same hash chain, allowing security monitoring to operate independently.
