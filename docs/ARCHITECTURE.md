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
  |  Contains: AuditDbContext with schema configuration,
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

2. **DbContext bypass for audit entities**: `AuditDbContext.SaveChanges()` / `SaveChangesAsync()` detect when audit entities are being saved and temporarily set the bypass flag so the interceptor does not recurse while persisting `AuditEventEntity`, `AuditIntegrityEntity`, `AuditLogEntity`, `AuditArchiveRecordEntity`, or `AuditSecurityEventEntity`.

`InternalAuditEventRepository` still exists as an internal helper around audit-event persistence, but the interceptor's `AuditLogEntity` path currently writes directly to `context.Set<AuditLogEntity>()` and relies on the exclusion set plus the DbContext bypass behavior to prevent recursion.

### Fail-Safe Audit Logging

Audit logging is designed to avoid breaking the application's primary operation when resilience is enabled. The `ResilientAuditLogger` decorator wraps `IAuditLogger`, retries transient failures, and on exhaustion routes failed events to the configured dead letter queue. A background processor can retry dead-lettered events on a configurable schedule.

Important caveats in the current implementation:

- This fail-safe behavior applies to the `IAuditLogger` pipeline, not to every audit-related write path in the system.
- The dead-letter path is operationally useful but not yet fully security-hardened. In the current code, DLQ implementations still persist the original failed event payload and exception stack trace without applying `IAuditFieldRedactor`.
- The emergency fallback file is redacted, but it writes to temp storage using platform-default permissions.

### Configurable Fail-Closed for Interceptor Audit Failures

The EF `AuditSaveChangesInterceptor` builds `AuditLogEntity` records inside `SavingChangesAsync`. Historically, any exception raised while building those records was caught, logged, and swallowed — the business `SaveChanges` completed with no audit record attached. That permissive default is correct for most applications but wrong for regulated ones (HIPAA, FERPA, GDPR, PCI-DSS), where a write without a matching audit record is a compliance gap. Phase 4 makes the response configurable via `AuditOptions.FailureMode`.

| Mode | Behavior on interceptor audit-build failure |
|------|---------------------------------------------|
| `AuditFailureMode.Permissive` (default) | Log at Error level; swallow. Business `SaveChanges` proceeds and commits without the audit record. Matches historical behavior. |
| `AuditFailureMode.FailClosedForRegulated` | If any modified entity in the failing save is regulated, rethrow `AuditIntegrityException`; EF aborts the save and the transaction rolls back. Non-regulated entities remain permissive under the same deployment. |
| `AuditFailureMode.FailClosedAlways` | Rethrow on every interceptor audit-build failure, regardless of entity regulation. For deployments where audit completeness is non-negotiable across all data. |

**Regulated-entity detection (default policy):** The shipped `RegulatedEntityFailurePolicy` considers an entity regulated when its class carries `[FERPA]` or `[PHI]`, or when any public-instance property carries `[SensitiveData(ApplicableStandards = ...)]` with at least one of `ComplianceStandard.HIPAA`, `FERPA`, `GDPR`, or `PCI_DSS`. Attribute lookup results are cached per type.

**Extension point.** `IAuditFailurePolicy` in `MillWorks.AuditCore.Abstractions.Interfaces` is a single-method contract (`bool ShouldFailClosed(AuditFailureContext context)`). Consumers can register a custom implementation via `Services.AddSingleton<IAuditFailurePolicy, MyCustomPolicy>()` **before** calling `AddMillWorksAudit` — the default registration uses `TryAddSingleton` and yields to a pre-registered policy. Custom policies can inspect the full `AuditFailureContext` (mode plus the list of `(EntityType, Action)` pairs captured from the failing save) and apply tenant-, operation-, or user-specific rules.

**Exception shape.** `AuditIntegrityException` in `MillWorks.AuditCore.Abstractions.Exceptions` carries `EntityName`, `Action`, and `FailureReason`, with the original exception wrapped as `InnerException`. Under `FailClosedForRegulated`, the named entity is the first regulated entity in the failing save (chosen by re-running the policy per entity) — useful diagnostics when a batch contains both regulated and non-regulated rows.

**Observability.** `AuditDiagnosticCounter.InterceptorAuditFailure` (and the paired `IAuditDiagnostics.InterceptorAuditFailureCount` property) increments **unconditionally** whenever the interceptor's audit-build catch fires — on both the swallow and rethrow paths. A sustained non-zero rate indicates an audit-pipeline regression regardless of the configured failure mode.

**Scope boundary — what Phase 4 does NOT change.** This fail-closed behavior is strictly scoped to the EF interceptor's audit-log-build path inside `ProcessAuditableEntries`. It does not apply to:

- `ResilientAuditLogger` (the `IAuditLogger` decorator) — remains fail-open by architecture: retries, DLQ, emergency fallback file. Designed for applications calling `IAuditLogger.LogAsync` directly.
- `AuditContextMiddleware` deferred request-audit dispatch — still catches and logs; overflow preservation is governed by `AuditMiddlewareOptions.OverflowPolicy`.
- Background hosted services (`DatabaseInitializationService`, `IntegrityWriteBatcher`, `IntegrityReconciliationService`, `DeadLetterQueueProcessor`, `ArchiveCreationBackgroundService`, `ArchiveVerificationBackgroundService`) — each has its own per-operation `catch` and log behavior; not governed by `AuditFailureMode`.

A regulated consumer that needs end-to-end audit-completeness across all paths should treat `FailClosedForRegulated` as one layer among several rather than the single fail-closed switch.

### Request-Audit Overflow Semantics

Phase 5 governs what happens when the deferred HTTP request-audit pipeline overflows — when `InProcessRequestAuditDispatcher` cannot accept a new event because its bounded channel is full or closed. This is distinct from Phase 4's interceptor fail-closed behavior.

**Decision: request-audit overflow does not fail the HTTP response.** The business request continues regardless of how overflow is handled. `AuditContextMiddleware` catches every exception from `IRequestAuditDispatcher.DispatchAsync` and swallows it after logging. A failed request-audit dispatch never propagates to the HTTP status line. The request-audit record is a supplementary diagnostic, not a transactional record, and audit-side saturation does not translate into 5xx responses.

`AuditMiddlewareOptions.OverflowPolicy` selects one of three `RequestAuditOverflowPolicy` values. All three are implemented at the dispatcher layer; middleware branches only in its structured log detail (`{PolicyDetail}`), not in HTTP-facing behavior.

| Policy | Dispatcher behavior on overflow | Event fate |
|--------|---------------------------------|------------|
| `Throw` (default) | Throws `TimeoutException` (zero-timeout Path A), propagates `OperationCanceledException` (timeout Path B), or propagates `ChannelClosedException`. Caller-initiated cancellation is not treated as overflow — it propagates normally. Middleware catches and logs. | Lost. |
| `DropAndLog` | Catches overflow at the dispatcher, logs at `LogWarning`, returns without throwing. Middleware's catch blocks do not fire on overflow under this policy. | Lost (single `LogWarning` per drop). |
| `RouteToDeadLetter` | Catches overflow and attempts `IAuditDeadLetterQueue.StoreFailedEventAsync`. On success logs at `LogWarning` (recovery, not failure). If no DLQ is registered, logs at `LogWarning` and drops. If the DLQ `Store` call itself throws, logs at `LogError` and drops. | Preserved in DLQ on success; dropped on DLQ unavailability or DLQ failure. |

**Tradeoffs.**

- `Throw` gives operators the loudest per-overflow signal via middleware's `LogWarning` / `LogError`. Under sustained saturation this can become noisy without preserving any of the lost events. Right for consumers who want overflow to be visible but do not operate a DLQ.
- `DropAndLog` is the quietest policy under sustained saturation: one dispatcher-side `LogWarning` per drop, no middleware exception path. Right for consumers who explicitly prefer silent event loss over added request latency and do not operate a DLQ.
- `RouteToDeadLetter` is the only policy that preserves saturated events for out-of-band processing. Requires `UseResilience(...)` on the builder so an `IAuditDeadLetterQueue` implementation is registered; otherwise it falls back to `DropAndLog`-style behavior with an additional `LogWarning` noting the missing DLQ. Right for regulated deployments.

**Distinction from Phase 4.** `AuditOptions.FailureMode` (Phase 4) governs the EF `AuditSaveChangesInterceptor` only; it decides whether a failing audit-log-build inside `SavingChangesAsync` rolls back the business transaction for regulated entities. That decision sits on the `DbContext.SaveChanges` write path. `AuditMiddlewareOptions.OverflowPolicy` (Phase 5, this section) sits on the HTTP middleware's deferred dispatch path and does not fail the HTTP response regardless of policy. The two settings are independent — a consumer can run Phase 4 `FailClosedForRegulated` alongside any Phase 5 policy. Phase 5 deliberately does not add a request-audit failure policy: a regulated deployment that needs audit completeness on the HTTP path should wire `RouteToDeadLetter` plus a durable DLQ (for example `Redis` or `FileSystem`), not expect request-audit failures to 5xx the request.

## Runtime Options Flow

All configurable runtime behavior flows through the standard .NET options pipeline. Runtime services receive their configuration through `IOptions<T>` / `IOptionsMonitor<T>` constructor injection; none of them read `IConfiguration` directly. The hidden configuration path that formerly existed in `TamperDetectionService` (direct reads of `Audit:HmacKey` and `Audit:EnableDigitalSignatures`) has been removed.

Seven options types participate. All are registered with the same shape — `AddOptions<T>().BindConfiguration("Audit").Configure(consumerOverlay).ValidateOnStart()` — and each has a corresponding `IValidateOptions<T>` registered via `TryAddEnumerable` so misconfiguration fails at host start rather than at first use.

```
appsettings / environment / secrets
    |
    v
IConfiguration section "Audit"
    |
    v
AddOptions<T>().BindConfiguration("Audit")
    |
    v
fluent builder overlay
    - builder.Options.* for AuditOptions
    - UseEntityFramework(o => ...)
    - UseSecurity(o => ...)
    - UseCompliance(o => ...)
    - UseArchival(o => ...)
    - UseResilience(o => ...)
    |
    v
IValidateOptions<T> + ValidateOnStart()
    |
    v
typed consumers
    - IOptions<T> / IOptionsMonitor<T> in runtime services
    - AuditDbContext schema options
    - TamperDetectionService HMAC/signature options
    - AuditSaveChangesInterceptor failure policy
    - InProcessRequestAuditDispatcher overflow policy
```

| Options type              | Registered in                               | Binds flat from `Audit` | `ValidateOnStart()` |
|---------------------------|---------------------------------------------|-------------------------|---------------------|
| `AuditOptions`            | `AddMillWorksAudit` (top-level)             | ✓                       | ✓                   |
| `AuditMiddlewareOptions`  | `AddMillWorksAudit` (top-level)             | ✓                       | ✓                   |
| `EntityFrameworkOptions`  | `MillWorksAuditBuilder.UseEntityFramework`  | ✓                       | ✓                   |
| `SecurityOptions`         | `MillWorksAuditBuilder.UseSecurity`         | ✓                       | ✓                   |
| `ComplianceOptions`       | `MillWorksAuditBuilder.UseCompliance`       | ✓                       | ✓                   |
| `ArchivalOptions`         | `MillWorksAuditBuilder.UseArchival`         | ✓                       | ✓                   |
| `ResilienceOptions`       | `MillWorksAuditBuilder.UseResilience`       | ✓                       | ✓                   |

### Binding precedence

Two input sources feed the resolved options instance: `IConfiguration` via `BindConfiguration("Audit")`, and the fluent builder overlay via `builder.Options.X = Y` (for `AuditOptions`) or `Use*(o => o.X = Y)` (for the per-subsystem types). Registration order is bind first, fluent overlay second, so fluent values win where the consumer explicitly set them. A consumer can leave a property untouched in the fluent builder and still have its `Audit:...` configuration value take effect.

### Baseline-diff replay for `AuditOptions`

`AuditOptions` is the only type the consumer mutates on a live `builder.Options` instance (exposed as a property on `MillWorksAuditBuilder`) rather than inside a `Use*(o => ...)` delegate. To replay those mutations onto the pipeline-resolved options instance without blanking the configuration-bound values the consumer did not touch, `AddMillWorksAudit` snapshots a baseline `new AuditOptions()` alongside the consumer's mutated copy and, in its `Configure` delegate, assigns each property only when the consumer-mutated value differs from the baseline. Known limitation: if a consumer fluent-sets a property to the same value as its default, the overlay cannot distinguish that from "never touched," and `IConfiguration` binding wins for that property. This is accepted for Phase 1; no currently targeted use case is blocked by it.

### Hosted-service registration

Six background services participate in the audit pipeline: `DatabaseInitializationService`, `IntegrityWriteBatcher`, `IntegrityReconciliationService`, `DeadLetterQueueProcessor`, `ArchiveCreationBackgroundService`, and `ArchiveVerificationBackgroundService`. Each is registered unconditionally by its owning `Use*` method (or by `AddMillWorksAudit` in the case of `InProcessRequestAuditDispatcher`). Each self-gates inside its entry method (`ExecuteAsync` or `StartAsync`) by reading `IOptions<T>.Value` and returning early if its enablement flag is false. Registration is therefore always predictable; runtime behavior is driven entirely by the typed option.

### Schema configuration and migration anchoring

`EntityFrameworkOptions.Schema` (default: `"audit"`) is applied to the runtime model via `AuditDbContext.OnModelCreating`, which calls `modelBuilder.HasDefaultSchema(_schema)` as its first operation. Every audit entity's table mapping inherits this schema — there are no per-entity schema overrides on the entity attributes. `AuditModelCacheKeyFactory` (wired via `options.ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()` inside `UseEntityFramework`) includes the configured schema in the compiled-model cache key alongside the context type and design-time flag, so two `AuditDbContext` instances in the same process with different schemas get independently compiled models.

**Migration anchoring — the single backward-compat constraint that survives the greenfield policy.** The built-in EF migrations in `src/MillWorks.AuditCore.EntityFramework/Migrations/` are generated against the default `"audit"` schema. Every `CreateTable`, `AddForeignKey`, `EnsureSchema`, and `__EFMigrationsHistory` reference is literal `"audit"`. The `MigrationsHistoryTable("__EFMigrationsHistory", "audit")` call in `MillWorksAuditBuilder.UseEntityFramework` is also fixed to `"audit"`. Consequences:

- **Default-schema deployments (`Schema = "audit"`):** `MigrateOnStartup = true` applies the packaged migrations cleanly.
- **Custom-schema deployments (`Schema = "<other>"`):** fresh-database-only. Set `EnsureDatabaseCreated = true`; the runtime model creates tables under the configured schema on first use. Do not set `MigrateOnStartup = true` under a custom schema — the packaged migrations target `"audit"` regardless of the option value and will not produce the expected tables.
- **Schema identifier validation:** `EntityFrameworkOptionsValidator` enforces `^[A-Za-z_][A-Za-z0-9_]{0,127}$` on the schema value and rejects the reserved SQL Server schemas (`dbo`, `sys`, `guest`, `INFORMATION_SCHEMA`, case-insensitive) at host start via `ValidateOnStart()`.

Parameterized migration regeneration against a non-`"audit"` schema is out of scope for the shipped package. A deployment that requires a custom schema with migration support must fork the migration set and regenerate it against the chosen schema as a dedicated migration path — this is explicitly not a config toggle.

### Remaining resolve-time forwarders

Two options types retain a transitional resolve-time singleton forwarder of the form `Services.AddSingleton<T>(sp => sp.GetRequiredService<IOptions<T>>().Value)`:

- **`SecurityOptions`** — consumed as a bare value by `FerpaValidator`, an `IComplianceValidator` implementation.
- **`ComplianceOptions`** — consumed as a bare value inside the `AuditSaveChangesInterceptor` factory via `sp.GetService<ComplianceOptions>()`.

Each forwarder is slated for removal once its last bare consumer flips its constructor to `IOptions<T>`. No other options type retains a forwarder.

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
   f. Stamp CorrelationId / IpAddress / UserAgent from AuditDbContext request metadata
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

When `UseCompliance()` is configured with an enforcement mode, the interceptor can block or flag saves that violate compliance rules. For example, if FERPA enforcement is active and a FERPA-annotated entity is being modified without verified consent, `Enforce` throws a `ComplianceViolationException` before `SaveChanges` reaches the database. `AuditOnly` allows the save but adds a `ComplianceViolation` row to `SecurityEvents` in the same `SaveChangesAsync` operation.

## Tamper Detection

### Hash Chain Construction

When tamper detection is enabled, `AuditLogger.LogAsync(...)` persists the `AuditEventEntity` and then creates a corresponding `AuditIntegrityEntity` in the same transaction.

Each integrity record receives an application-assigned sequence number under the integrity distributed lock. The SQL Server implementation does not rely on `IDENTITY` ordering for batched inserts because SQL Server's `MERGE ... OUTPUT` path can assign identity values in an order that differs from the input entity order. For a new event, `TamperDetectionService` currently:

1. Retrieves the most recent `AuditIntegrityEntity` to obtain the previous hash and highest sequence number.
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
   - application-assigned `SequenceNumber`

The current implementation does not store a separate persisted `ChainHash` column. Chain continuity is represented by each row's `PreviousEventHash` pointer to the prior row's `EventHash`.

Performance note:

- Hashes are pre-computed outside the critical section.
- The critical section is still serialized around "read latest + assign sequence + insert integrity row", so this remains one of the main write-path bottlenecks under concurrency.

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

### Append Serialization

In multi-instance deployments, concurrent writes to the integrity chain race on the "latest row" lookup and the insert of the next `SequenceNumber`. `TamperDetectionService` serializes the read-modify-write by opening a DB transaction and taking a SQL Server `sp_getapplock` named `audit:integrity:append` with `@LockOwner = 'Transaction'`. The lock is bound to the transaction, so it releases automatically on commit, rollback, or connection drop — there is no lease TTL that can expire mid-critical-section, and no dependency on Redis availability. Every API replica talking to the same database is serialized at the DB layer.

Non-SQL-Server providers (the SQLite test harness) get a process-local `SemaphoreSlim`; the service checks `IAuditIntegrityRepository.SupportsCrossProcessAppendLock` to decide which path to take. A retry loop with exponential backoff remains as defense-in-depth: after the applock is in place it should essentially never fire, and a non-zero retry count in production is a signal that the applock was removed or weakened.

The general-purpose `IAuditDistributedLockService` (Redis or in-memory) is no longer on the integrity-write path; it remains for coordination primitives that genuinely need cross-process mutual exclusion without a transaction, such as the dead-letter-queue leader election.

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

The `IAuditSecurityEventService` records security-relevant events (tamper alerts, authentication failures, unauthorized access attempts) in the `SecurityEvents` table. These events are separate from the main audit log and are not subject to the same hash chain, allowing security monitoring to operate independently. Interceptor-level compliance violations that proceed under `AuditOnly` also write directly to `SecurityEvents` so the finding commits atomically with the allowed entity save.
