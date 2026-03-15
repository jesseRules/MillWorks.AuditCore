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

1. **`InternalAuditEventRepository`**: A dedicated repository that writes audit entities directly to the `AuditApplicationDbContext`, bypassing the interceptor pipeline. The interceptor recognizes its own entity types (`AuditEventEntity`, `AuditIntegrityEntity`, etc.) and excludes them from auditing.

2. **Type exclusion set**: The interceptor maintains a `HashSet<Type>` of all audit entity types. Any `EntityEntry` whose `ClrType` appears in this set is skipped during change detection.

### Fail-Safe Audit Logging

Audit logging must never cause the application's primary operation to fail. The `ResilientAuditLogger` decorator wraps `IAuditLogger` and catches all exceptions. Failed events are routed to the dead letter queue rather than propagating to the caller. A background processor retries dead-lettered events on a configurable schedule.

## Interceptor Flow

The `AuditSaveChangesInterceptor` extends EF Core's `SaveChangesInterceptor` and hooks into `SavingChangesAsync`:

```
Application calls DbContext.SaveChangesAsync()
    |
    v
1. Interceptor receives SavingChanges event
    |
    v
2. Iterate all EntityEntry objects in ChangeTracker
    |
    v
3. For each entry:
   a. Skip if entity type is in the audit entity exclusion set
   b. Skip if entity type has [NoAudit] attribute (cached)
   c. Check entity state: Added, Modified, or Deleted
   d. For Modified: diff property-by-property, record old/new values
   e. For FERPA-annotated entities: check consent via IConsentVerificationService
   f. For [SensitiveData] properties: mask values in the audit snapshot
   g. Build AuditLogEntity with entity name, action, key values,
      old values JSON, new values JSON, changed property list
    |
    v
4. Collect all AuditLogEntity objects into a batch
    |
    v
5. Write batch to AuditApplicationDbContext via InternalAuditEventRepository
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

Each audit event is assigned a monotonically increasing sequence number. When a new event is persisted, the `TamperDetectionService`:

1. Retrieves the most recent `AuditIntegrityEntity` to obtain the previous hash.
2. Canonicalizes the audit event using `AuditCanonicalizer` (deterministic JSON serialization -- sorted keys, consistent whitespace, UTC timestamps).
3. Computes `SHA-256(canonical_json)` to produce the event hash.
4. Computes `SHA-256(event_hash + previous_hash)` to produce the chain hash.
5. Optionally computes `HMAC-SHA256(chain_hash, hmac_key)` for digital signature.
6. Stores the `AuditIntegrityEntity` with `EventHash`, `PreviousHash`, `ChainHash`, `HmacSignature`, and `SequenceNumber`.

### Chain Verification

`VerifyChainIntegrityAsync` walks the integrity table in sequence order and recomputes each chain hash from its event hash and the previous record's chain hash. Any mismatch indicates tampering. The result includes:

- Total events verified
- First broken link (if any)
- List of tampered event IDs
- Verification timestamp

`VerifySequenceIntegrityAsync` checks for gaps or duplicates in the sequence number column.

`DetectTamperingAsync` combines both checks and returns a list of `TamperAlert` objects with severity and description.

### Distributed Locking

In multi-instance deployments, concurrent writes to the hash chain could produce conflicting sequence numbers. When `UseRedisLocking` is enabled, `TamperDetectionService` acquires a distributed lock via Redis before reading the previous hash and writing the new integrity record. The lock has a configurable timeout and is released immediately after the write.

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

### HMAC Signing

When `EnableDigitalSignatures` is set and an `HmacKey` is provided, every integrity record is signed with HMAC-SHA256. This allows verification that integrity records were produced by a party possessing the key, not just that the hash chain is internally consistent.

### Consent Verification

The `IConsentVerificationService` (backed by `IMemoryCache` for synchronous reads in the interceptor path) checks whether a user has granted consent for processing their data. This is used by the FERPA validator and the interceptor's FERPA enforcement path. Consent records are cached to avoid database roundtrips on every `SaveChanges` call.

### Security Event Logging

The `IAuditSecurityEventService` records security-relevant events (tamper alerts, authentication failures, unauthorized access attempts) in the `AuditSecurityEvents` table. These events are separate from the main audit log and are not subject to the same hash chain, allowing security monitoring to operate independently.
