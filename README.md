# MillWorks.AuditCore

*Tamper-evident audit logging and compliance platform for .NET*

[![Build Status](https://img.shields.io/github/actions/workflow/status/jesserules/millworks.auditcore/ci.yml?branch=main)](https://github.com/jesserules/millworks.auditcore/actions)
[![NuGet](https://img.shields.io/nuget/v/MillWorks.AuditCore)](https://www.nuget.org/packages/MillWorks.AuditCore)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![Tests](https://img.shields.io/badge/tests-1%2C850%2B_passing-brightgreen)](tests/)

MillWorks.AuditCore is a comprehensive audit logging framework for .NET applications that enforces data integrity at the storage layer through cryptographic hash chains and HMAC signatures. Built for organizations operating under HIPAA, FERPA, SOC 2, GDPR, and NIST requirements, it provides tamper-evident logging, field-level encryption, and automated compliance validation -- capabilities that are typically spread across multiple commercial products. The library integrates with Entity Framework Core as a SaveChanges interceptor, capturing entity changes from DbContexts where the audit interceptor is registered without requiring per-save manual logging calls.

## Compatibility

| Component | Requirement |
|---|---|
| **.NET** | .NET 10.0+ |
| **Entity Framework Core** | 10.0+ |
| **SQL Server** | 2016+ (or Azure SQL) |
| **Redis** (optional) | 6.0+ — required only for distributed locking and Redis dead letter queue |
| **Azure Blob Storage** (optional) | Required only for archival |

**Versioning policy:** This project follows [Semantic Versioning 2.0](https://semver.org/). The public API surface consists of the builder API (`AddMillWorksAudit`), the `IAuditProvider` contract, and all types in the `MillWorks.AuditCore.Abstractions` package.

## Packages

| Package | Purpose |
|---|---|
| **`MillWorks.AuditCore`** | Primary install — batteries included, pulls in EF Core + ASP.NET Core wiring |
| `MillWorks.AuditCore.Abstractions` | Pure .NET — models, DTOs, interfaces. No EF or ASP.NET dependencies. Reference this from shared libraries or non-web hosts. |
| `MillWorks.AuditCore.EntityFramework` | EF Core data layer without ASP.NET host dependencies |
| `MillWorks.AuditCore.Services` | Business logic — compliance, encryption, dead letter queue, query/reporting |
| `MillWorks.AuditCore.Providers` | Entity-specific audit enrichment via `IAuditProvider` |

Most consumers install only `MillWorks.AuditCore`. The other packages are available for advanced scenarios (e.g., referencing `Abstractions` from a shared domain library that should not depend on ASP.NET).

## Features

### Automatic Entity Auditing
Any DbContext that has `AuditSaveChangesInterceptor` registered will produce audit envelopes for tracked entity changes. The interceptor is a producer; the audit sink owns persistence. Consumer DbContexts do not need to map AuditCore entities — they may optionally implement `IAuditContextSource` to flow user/correlation context into envelopes, and `IAuditProviderDispatchSource` to participate in `IAuditProvider` dispatch. Entities are diffed at the property level, recording old values, new values, and changed property lists. No attribute decoration or manual logging calls required — opt out with `[NoAudit]` when needed. The synchronous `SaveChanges()` path does not produce audit records because it cannot support the full provider-dispatch pipeline.

### Tamper Detection
Every audit event is linked into a cryptographic hash chain. Each record's SHA-256 hash incorporates the previous record's hash, forming an append-only ledger that detects insertion, deletion, or modification of any record in the sequence. Chain integrity can be verified on demand or on a schedule. Tamper alerts are recorded as security events.

Integrity modes and sink modes:

| Mode | Behavior |
|---|---|
| **Strict (Immediate sink)** | Audit envelope is published, persisted, and chain-extended on the audit-owned DbContext. Decoupled from consumer transaction. |
| **Strict (TransactionalOutbox sink)** | Audit envelope is staged in the saving consumer's transaction via outbox row; chain-extension happens after commit via background drainer. |
| **Batched (legacy IntegrityWriteBatcher)** | Existing high-throughput batched-integrity path. Independent of sink mode. |

### Field-Level Encryption
AES-256-GCM encryption for sensitive entity fields, applied transparently through EF Core value converters. Mark properties with `[EncryptedField]` or `[SensitiveData(AutoEncrypt = true)]`. Encrypted fields are redacted in audit snapshots (recorded as `[ENCRYPTED]` or masked) to prevent sensitive values from appearing in the audit log. Key management supports Azure Key Vault for cloud deployments or file-based key storage for DMZ and air-gapped environments. Per-field key derivation ensures compromise of one field does not expose others.

### Compliance Validation
Built-in validators for seven regulatory standards (plus enum-level NIST support): GDPR (Articles 17, 25, 30, 32), HIPAA (45 CFR Part 164), FERPA (34 CFR Part 99), SOC 2 (Trust Services Criteria), ISO 27001 (Annex A), PCI-DSS, and STIG. Validators inspect the audit log for required controls and produce structured compliance reports with pass/fail per rule, severity, regulation references, and remediation recommendations.

**Enforcement modes** control what happens when a non-compliant operation is intercepted:

| Mode | Behavior |
|---|---|
| `Advisory` (default) | Log a warning. The operation proceeds normally. |
| `AuditOnly` | Log a warning and create an `AuditSecurityEvent` record. The operation proceeds. |
| `Enforce` | Block the operation by throwing a `ComplianceViolationException`. The exception includes the standard name, entity type, user ID, and regulation reference (e.g., `"34 CFR §99.30"`). Callers should catch this exception and return an appropriate error response. |

### Dead Letter Queue
Audit events that fail to persist (database timeout, transient fault) are captured in a dead letter queue rather than lost. A background processor automatically retries failed events with configurable retry policies.

All three providers ship in v1.0:

| Provider | Backing Store | Best For |
|---|---|---|
| `InMemory` | `ConcurrentDictionary` | Development and testing |
| `FileSystem` | JSON files with semaphore locking | Single-instance production without Redis |
| `Redis` | Sorted sets with 30-day expiry | Multi-instance production deployments |

### Deferred HTTP Request Auditing
HTTP request-level audit events are dispatched off the request thread instead of being persisted inline during middleware teardown. By default, AuditCore uses an in-process bounded queue plus hosted worker. The worker creates a fresh DI scope for each deferred request audit, resolves scoped services such as `IAuditLogger`, and persists the event there.

This design keeps request latency bounded while preserving scoped lifetime correctness. Consumers that already have a job system can replace the default dispatcher with their own implementation of `IRequestAuditDispatcher`.

### Distributed Coordination
Hash-chain consistency across concurrent writers — in one process or across many API replicas against the same database — is guarded by a SQL Server `sp_getapplock` taken inside the integrity-write transaction. The lock is bound to the transaction, so it auto-releases on commit, rollback, or connection drop; there is no lease TTL to expire mid-critical-section. An in-process `SemaphoreSlim` covers non-SQL-Server providers (e.g. the SQLite test harness). The general-purpose `IAuditDistributedLockService` (Redis or in-memory) remains for other coordination primitives such as the dead-letter-queue leader lock.

### Archival
Completed audit records can be archived to Azure Blob Storage with integrity verification. Archives are checksummed and can be restored on demand with full integrity-chain metadata preserved. Background archive creation and verification both run on configurable schedules driven by retention policies. Archive creation and restore stream records through compression/decompression with bounded buffering so large archives do not require full in-memory materialization.

### Custom Providers
Per-entity audit enrichment through the `IAuditProvider` interface. Register providers for specific entity types to control which actions are audited, add domain-specific metadata, and mask sensitive properties before they reach the audit log.

### Query and Reporting
Multi-field text search, date-range filtering, entity trail reconstruction, user activity timelines, event type distribution, and top-user reports -- provided across `AuditQueryService`, `AuditSearchService`, and `AuditReportService`. Compliance reports can be generated per standard for any date range. Audit event data can be exported in JSON or CSV format. All query services use `AsNoTracking()` for read performance.

### Observability
OpenTelemetry-compatible metrics for SQL operations: command duration histograms, error counts by category (throttling, deadlock, connection pool, transient), slow query counters, and retry tracking. Azure SQL error codes are classified automatically for DTU throttling detection. See the [Observability](#observability) section for meter names and integration examples.

## Quick Start

Install the primary package (pulls in all dependencies):

```shell
dotnet add package MillWorks.AuditCore
```

### Minimal Setup

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMillWorksAudit(audit =>
{
    audit.Options.ApplicationName = "MyApp";

    audit.UseEntityFramework(ef =>
    {
        ef.ConnectionString = builder.Configuration
            .GetConnectionString("DefaultConnection")!;
        ef.EnsureDatabaseCreated = true;
        ef.Schema = "audit";
    });

    audit.UseSecurity(security =>
    {
        security.EnableTamperDetection = true;
        security.AuditSinkMode = AuditSinkMode.TransactionalOutbox;  // for FailClosedForRegulated
    });

    audit.UseMiddleware(middleware =>
    {
        middleware.ExcludedReadPaths.Add("/api/v1/feedback/dashboard");
    });
});

var app = builder.Build();
app.UseMillWorksAudit();
app.Run();
```

### Manual Logging

Inject `IAuditLogger` anywhere in your application:

```csharp
public class OrderService(IAuditLogger auditLogger)
{
    public async Task PlaceOrderAsync(Order order)
    {
        // ... business logic ...

        await auditLogger.LogAsync("Order.Placed", new
        {
            OrderId = order.Id,
            Total = order.Total,
            ItemCount = order.Items.Count
        });
    }
}
```

### Scoped Operations

For multi-step operations, use audit scopes to accumulate context:

```csharp
await using var scope = auditLogger.CreateScope("Order.Fulfillment", order);
scope.SetCustomField("WarehouseId", warehouse.Id);

// ... pick, pack, ship ...

scope.SetCustomField("TrackingNumber", trackingNumber);
scope.SetCustomField("ShippedAt", DateTimeOffset.UtcNow);
// Event is persisted with all fields when the scope disposes
```

`CreateScope()` remains appropriate for application-level business operations like workflows and long-running units of work. HTTP request auditing is handled separately by the middleware dispatcher/worker pipeline.

### Automatic Entity Change Tracking

Any entity saved through a DbContext that has the audit interceptor registered will be captured automatically. Consumer DbContexts do not need to map AuditCore entities — they may optionally implement `IAuditContextSource` to flow user/correlation context:

```csharp
public class ProjectDbContext(DbContextOptions<ProjectDbContext> options)
    : DbContext(options), IAuditContextSource
{
    public string? CurrentUserId { get; set; }
    public string? CurrentCorrelationId { get; set; }
    public string? CurrentIpAddress { get; set; }
    public string? CurrentUserAgent { get; set; }

    // No AuditCore entity mapping required — the sink owns persistence.
}

// In the host (e.g., MillWorks.Api/Program.cs):
services.AddDbContext<ProjectDbContext>((sp, options) =>
{
    options.UseSqlServer(connectionString);
    options.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
});
```

## The `IAuditProvider` Contract

`IAuditProvider` is the primary extensibility point for per-entity audit behavior. Implement this interface to control what gets audited and how audit events are enriched for a given entity type.

```csharp
public interface IAuditProvider
{
    /// The entity type name this provider handles (e.g., "Patient", "FinancialRecord")
    string EntityType { get; }

    /// Create an audit event for the given action and entity state
    Task<AuditEvent> CreateAuditEventAsync(string action, object? entity, object? oldValues = null);

    /// Return false to suppress auditing for a specific action/entity combination
    Task<bool> ShouldAuditAsync(string action, object entity);

    /// Add domain-specific metadata to an audit event after creation
    Task EnrichAuditEventAsync(AuditEvent auditEvent, object? entity);

    /// Compute a property-level diff between old and new entity state
    Dictionary<string, object?> GetChanges(object? oldValues, object? newValues);
}
```

A `BaseAuditProvider` abstract class provides default implementations for change tracking, HTTP context integration (IP address, user agent, request ID), and reflection-based property comparison. Most providers only need to override `EntityType` and optionally `ShouldAuditAsync`:

```csharp
public class PatientAuditProvider : BaseAuditProvider
{
    public PatientAuditProvider(IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor) { }

    public override string EntityType => "Patient";

    public override Task<bool> ShouldAuditAsync(string action, object entity)
    {
        // Audit all actions on Patient entities
        return Task.FromResult(true);
    }
}
```

Register providers in the builder:

```csharp
audit.RegisterProviders(registry =>
{
    registry.AddProvider<PatientAuditProvider>("Patient");
    registry.AddProvider<FinancialRecordAuditProvider>("FinancialRecord");
});
```

## Opting Out with `[NoAudit]`

The `[NoAudit]` attribute can be applied at two levels:

- **Entity level** (`[NoAudit] class TempCache { ... }`) — the entire entity type is excluded from automatic audit capture.
- **Property level** (`[NoAudit] public string InternalNotes { get; set; }`) — the property is excluded from change tracking and will not appear in old/new value diffs.

`[NoAudit]` applies only to the decorated target. It does not cascade to navigation properties or owned entities. If you need to exclude a related entity, apply `[NoAudit]` to that entity's class directly.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│ Consumer library (Compliance, Identity, ...)                        │
│   - References MillWorks.AuditCore.Abstractions (and EF only when   │
│     it uses [EncryptedField]/UseFieldEncryption — Compliance only)  │
│   - Defines local abstraction I{Library}AuditPublisher              │
│   - Service layer calls I{Library}AuditPublisher.PublishAsync       │
│     OR DbContext has interceptor registered (by Api, not library)   │
└─────────────────────┬───────────────────────────────────────────────┘
                      │
                      │ AuditEnvelope
                      ▼
┌─────────────────────────────────────────────────────────────────────┐
│ MillWorks.Api / AuditBridge   (scoped lifetime)                     │
│   - Implements every I{Library}AuditPublisher                       │
│   - Forwards to IAuditSink (resolved per request scope)             │
│   - Registers AuditSaveChangesInterceptor on consumer DbContexts    │
└─────────────────────┬───────────────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────────────┐
│ MillWorks.AuditCore.AspNetCore / Services                           │
│   IAuditSink (one of:)                                              │
│   ├── ImmediateSink (default)                                       │
│   │     - Builds AuditLogEntity / AuditEventEntity / AuditIntegrity │
│   │     - Writes directly to AuditDbContext (its own connection)    │
│   │     - Chain construction lives here                             │
│   └── TransactionalOutboxSink (opt-in)                              │
│         - Writes AuditOutboxEntity in saving DbContext's txn        │
│         - Background drainer commits to AuditDbContext              │
└─────────────────────┬───────────────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────────────────────┐
│ MillWorks.AuditCore.EntityFramework / AuditDbContext                │
│   - Owns audit schema (tables: AuditLogs, AuditEvents,              │
│     AuditIntegrity, SecurityEvents, ArchiveRecords, IntegrityWork,  │
│     AuditOutbox)                                                    │
│   - Owns its own connection (may differ from consumer connection)   │
│   - Owns its migrations                                             │
└─────────────────────────────────────────────────────────────────────┘
```

**Abstractions** (`MillWorks.AuditCore.Abstractions`) is a pure .NET library with no framework dependencies. Most consumer libraries depend only on Abstractions — they receive audit capabilities through the interceptor + sink pipeline without mapping AuditCore entities.

## Configuration

The full builder API:

```csharp
builder.Services.AddMillWorksAudit(audit =>
{
    // Application identity
    audit.Options.ApplicationName = "MyApp";
    audit.Options.Environment = "Production";     // Default: "Production"
    audit.Options.EnableDigitalSignatures = true;
    audit.Options.HmacKey = builder.Configuration["Audit:HmacKey"]!;
    audit.Options.FailureMode = AuditFailureMode.FailClosedForRegulated; // Regulated-app posture; see "Fail-Closed Audit Failures" below

    // Entity Framework storage (required)
    audit.UseEntityFramework(ef =>
    {
        ef.ConnectionString = "Server=...";
        ef.Schema = "audit";                // Default schema; built-in migrations are anchored to "audit"
        ef.MigrateOnStartup = true;         // Apply EF migrations on startup (default: false)
        ef.EnsureDatabaseCreated = false;    // Use EnsureCreated for dev (default: true)
        ef.MigrationTimeoutSeconds = 120;    // Default: 300
    });

    // Security and tamper detection.
    //
    // When `UseRedisLocking = true`, the consuming application is responsible for
    // registering `IConnectionMultiplexer` in the service collection before
    // `AddMillWorksAudit(...)` runs — AuditCore does not own that registration because
    // most apps already register it for other components (token caches, rate limiters,
    // SSO invalidation, etc.). Example:
    //
    //     builder.Services.AddSingleton<IConnectionMultiplexer>(
    //         _ => ConnectionMultiplexer.Connect(
    //             builder.Configuration.GetConnectionString("Redis")!));
    audit.UseSecurity(security =>
    {
        security.EnableTamperDetection = true;              // Default: true
        security.EnableBatchedIntegrityWrites = false;      // Default: false (strict mode)
        security.UseRedisLocking = true;                    // Default: false
    });

    // Compliance validation
    audit.UseCompliance(compliance =>
    {
        compliance.Standards.Add(ComplianceStandard.HIPAA);
        compliance.Standards.Add(ComplianceStandard.FERPA);
        compliance.Standards.Add(ComplianceStandard.SOC2);
        compliance.EnableAutomaticValidation = true;  // Default: true
        compliance.DataRetentionDays = 2555;          // 7 years for HIPAA (default: 365)
        compliance.EnforcementMode = ComplianceEnforcementMode.Enforce;  // Default: Advisory
    });

    // Archival to Azure Blob Storage
    audit.UseArchival(archival =>
    {
        archival.Provider = ArchivalProvider.AzureBlob;
        archival.ConnectionString = "<azure-storage-connection>";
        archival.ContainerName = "audit-archives";
        archival.EnableBackgroundArchival = true;
        archival.RetentionDays = 365;
        archival.ArchivalIntervalHours = 24;
    });

    // Resilience and dead letter queue
    audit.UseResilience(resilience =>
    {
        resilience.EnableDeadLetterQueue = true;                        // Default: true
        resilience.DeadLetterProvider = DeadLetterProvider.FileSystem;   // Default: InMemory
        resilience.EnableBackgroundProcessor = true;                     // Default: true
        resilience.MaxRetries = 3;                                      // Default: 3
    });

    // HTTP request audit middleware behavior
    audit.UseMiddleware(middleware =>
    {
        middleware.AuditWritesOnly = false;                             // Default: false
        middleware.ExcludedReadPaths.Add("/api/v1/feedback/dashboard");
        middleware.QueueCapacity = 1000;                                // Default: 1000
        middleware.EnqueueTimeout = TimeSpan.FromMilliseconds(100);     // Default: 100ms
        middleware.DrainTimeout = TimeSpan.FromSeconds(30);             // Default: 30s
    });

    // Field-level encryption (Azure Key Vault)
    audit.UseFieldEncryption("https://my-vault.vault.azure.net/");

    // Or file-based encryption for air-gapped environments (production default: fail-safe)
    // audit.UseFieldEncryptionWithFileStorage("/secure/keys", "<master-key>");
    //
    // For dev/bootstrap, enable auto-generation of initial keys:
    // audit.UseFieldEncryptionWithFileStorage("/secure/keys", "<master-key>", allowAutoKeyGeneration: true);

    // Custom per-entity audit providers
    audit.RegisterProviders(registry =>
    {
        registry.AddProvider<PatientAuditProvider>("Patient");
        registry.AddProvider<FinancialRecordAuditProvider>("FinancialRecord");
    });

    // Optional: replace the default in-process request-audit dispatcher
    // with a consumer-owned job bridge (for example, MillWorks.BackgroundJobs).
    // audit.UseRequestAuditDispatcher<MyBackgroundJobsRequestAuditDispatcher>();
});
```

> **Security note:** The HMAC key is a cryptographic secret. Do not hard-code it in source or check it into version control. Use [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets) for local development, and a secrets manager (Azure Key Vault, AWS Secrets Manager, environment variables) in deployed environments.

### Database Initialization Defaults

| Option | Default | Behavior |
|---|---|---|
| `EnsureDatabaseCreated` | `true` | Calls `EnsureCreated()` on startup — creates the schema if the database does not exist. Suitable for development. |
| `MigrateOnStartup` | `false` | Applies EF Core migrations on startup. Use this in production for schema evolution. |

If both are set to `false`, the application assumes the audit schema already exists. The first audit write will throw a `DbUpdateException` if the tables are missing. For production deployments, either enable `MigrateOnStartup` or apply migrations as part of your deployment pipeline (`dotnet ef database update`).

### Custom SQL Server Schemas

`EntityFrameworkOptions.Schema` (default: `"audit"`) is applied to the runtime model at startup. It is validated against a SQL identifier pattern, and reserved names (`dbo`, `sys`, `guest`, `INFORMATION_SCHEMA`) are rejected at host boot.

The built-in EF migrations shipped with this package are **anchored to the `"audit"` schema** — every `CreateTable`, `EnsureSchema`, and the `__EFMigrationsHistory` table reference `"audit"` literally. This means:

- **Default schema (`Schema = "audit"`):** `MigrateOnStartup = true` works normally; packaged migrations apply cleanly.
- **Custom schema (`Schema = "<other>"`):** **fresh-database-only**. Set `EnsureDatabaseCreated = true`; the runtime model creates tables under your configured schema. Do **not** set `MigrateOnStartup = true` under a custom schema — the packaged migrations target `"audit"` regardless of the option value and will not produce your tables.

Parameterized migration regeneration (producing a migration set against a non-`"audit"` schema) is out of scope for the shipped package. If you need a custom schema with migrations, fork the migration set and regenerate it against your chosen schema as a dedicated migration path.

### Fail-Closed Audit Failures

`AuditOptions.FailureMode` controls what happens when the EF audit interceptor fails to build audit log records during `SaveChangesAsync`:

| Mode | Behavior |
|------|----------|
| `AuditFailureMode.Permissive` (default) | Audit failure is logged and swallowed. Business `SaveChanges` commits without the audit record. Matches historical behavior. |
| `AuditFailureMode.FailClosedForRegulated` | When any modified entity is regulated, the interceptor rethrows `AuditIntegrityException` and the business transaction rolls back. Non-regulated entities remain permissive. |
| `AuditFailureMode.FailClosedAlways` | Rethrows on every audit build failure. |

**Regulated-entity detection (default).** An entity is considered regulated when its class carries `[FERPA]` or `[PHI]`, or when any property carries `[SensitiveData(ApplicableStandards = ...)]` including `HIPAA`, `FERPA`, `GDPR`, or `PCI_DSS`. Register a custom `IAuditFailurePolicy` before `AddMillWorksAudit` if you need tenant-, operation-, or user-specific rules.

**Regulated-application snippet.** For HIPAA / FERPA / GDPR / PCI-DSS deployments, set `FailureMode` to `FailClosedForRegulated`:

```csharp
audit.Options.FailureMode = AuditFailureMode.FailClosedForRegulated;
```

When the EF interceptor fails to build audit log records for a regulated entity, `SaveChangesAsync` will throw `AuditIntegrityException` and roll back the business transaction — preventing writes that lack a matching audit record. Non-regulated entities retain the default permissive behavior in the same deployment.

**Sink mode behavior.** `FailClosedForRegulated` works under both sink modes:

- Under `AuditSinkMode.Immediate` (default), audit-build or audit-publish failures are visible inside the interceptor's `try` block, so they rethrow `AuditIntegrityException` and the consumer's `SaveChangesAsync` rolls back the business write. The audit-side write happens on a separate connection — it never lands.
- Under `AuditSinkMode.TransactionalOutbox`, the same rethrow happens on outbox-write failure (which propagates through the interceptor's `try`). The added value of outbox is durability across audit-subsystem crashes — once the outbox row commits with the consumer transaction, a later drainer crash does not lose the envelope. Use this mode when zero-loss durability is required (HIPAA / FERPA / PCI-DSS or any posture where audit-subsystem failures must not lose envelopes).

**Scope.** `FailureMode` governs the **EF interceptor audit-build path only**. The following audit paths are **not** affected by `AuditFailureMode` and retain their own failure semantics:

- `ResilientAuditLogger` — remains fail-open by architecture (retry → dead letter queue → emergency fallback file). Designed for direct `IAuditLogger.LogAsync` callers.
- `AuditContextMiddleware` deferred request-audit dispatch — currently catches and logs; DLQ routing is a separate concern.
- Background hosted services (integrity batcher/reconciler, DLQ processor, archive services) — each service decides its own failure behavior.

A regulated deployment that wants end-to-end audit-completeness across every path should treat `FailClosedForRegulated` as one layer, not the single switch.

### Deferred Request Audit Middleware

`UseMillWorksAudit()` captures request context and emits a request-level `AuditEvent` for audited HTTP requests. That event is dispatched through `IRequestAuditDispatcher`.

By default:

- Request audits are buffered in a bounded in-memory queue.
- A hosted worker dequeues them in the background.
- Each item is processed inside a fresh DI scope, so scoped services like `IAuditLogger` and `AuditApplicationDbContext` are resolved safely outside the original HTTP request.

This default is suitable for most applications and requires no extra infrastructure.

For applications that already use an external background job system, replace the default dispatcher and keep AuditCore's processor contract:

```csharp
public sealed class BackgroundJobsRequestAuditDispatcher(
    IBackgroundJobClient jobs) : IRequestAuditDispatcher
{
    public ValueTask DispatchAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        jobs.Enqueue<DeferredRequestAuditJob>(job => job.RunAsync(auditEvent, CancellationToken.None));
        return ValueTask.CompletedTask;
    }
}

public sealed class DeferredRequestAuditJob(
    IRequestAuditProcessor processor)
{
    public Task RunAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        => processor.ProcessAsync(auditEvent, cancellationToken);
}
```

Register the custom dispatcher in the consuming app:

```csharp
builder.Services.AddMillWorksAudit(audit =>
{
    audit.UseRequestAuditDispatcher<BackgroundJobsRequestAuditDispatcher>();
});
```

This keeps AuditCore independent of any specific job framework while allowing consumers to route deferred request audits through systems such as `MillWorks.BackgroundJobs`.

## Compliance Standards

| Standard | Scope | What the Validator Checks |
|----------|-------|--------------------------|
| **GDPR** | EU data protection | Records of processing (Art. 30), right to erasure support (Art. 17), data protection by design (Art. 25), security of processing (Art. 32), user identification in audit trails |
| **HIPAA** | US health information | Audit controls (SS 164.312(b)), access controls, integrity controls, transmission security, person/entity authentication, emergency access procedures, automatic logoff tracking |
| **FERPA** | US education records | Access logging for education records, consent verification, directory information handling, legitimate educational interest tracking, disclosure logging |
| **SOC 2** | Trust Services Criteria | Logical access controls, system operations monitoring, change management tracking, risk mitigation evidence, availability and processing integrity |
| **ISO 27001** | Information security | Annex A control coverage: access control, cryptography, operations security, communications security, incident management, business continuity, compliance evidence |
| **PCI-DSS** | Payment card data | Cardholder data access tracking, authentication monitoring, network access logging, system component change tracking, security event alerting |
| **STIG** | DoD security baselines | Security-relevant event logging, access control enforcement, audit record content requirements, timestamp accuracy, audit storage capacity monitoring |
| **NIST** | NIST SP 800-53 | Enum-level support for tagging audit events against NIST controls; no standalone validator (covered by overlapping STIG and ISO 27001 rules) |

## Database Schema

All tables are created under a configurable SQL Server schema (default: `audit`).

| Table | Purpose | Key Columns |
|-------|---------|-------------|
| `AuditEvents` | Primary audit event store | `Id`, `EventType`, `EntityName`, `Action`, `UserId`, `JsonData`, `StartDate`, `CorrelationId`, `IntegrityStatus` |
| `AuditLogs` | Entity change log with old/new values | `Id`, `EntityName`, `EntityId`, `Action`, `OldValues`, `NewValues`, `ChangedProperties`, `UserId` |
| `AuditIntegrity` | Hash chain records for tamper detection | `Id`, `AuditEventId`, `EventHash`, `PreviousHash`, `SequenceNumber`, `HmacSignature` |
| `AuditIntegrityWorkItems` | Durable outbox for pending integrity writes (batched mode) | `Id`, `EventId`, `Status`, `AttemptCount`, `CreatedAt`, `LastError`, `CompletedAt` |
| `ArchiveRecord` | Metadata for archived audit batches | `Id`, `ArchiveId`, `BlobPath`, `EventCount`, `Checksum`, `ArchivedAt`, `RestoredAt` |
| `SecurityEvents` | Security-relevant events and tamper/compliance alerts | `Id`, `EventType`, `Severity`, `Message`, `DetailsJson`, `IpAddress`, `DetectedAt`, `Status` |
| `AuditOutbox` | Durable handoff for `TransactionalOutbox` sink | `Id`, `EnvelopeJson`, `Status`, `CreatedAt`, `CompletedAt`, `AttemptCount`, `LastError` |

Append-only entities (`AuditIntegrity`, `SecurityEvents`) do not carry update/delete audit columns to avoid unnecessary storage overhead. `AuditEvents.IntegrityStatus` is the one field updated after initial insert — it transitions from `Pending` to `Completed` (or `Failed`/`Reconciled`) when the integrity record is created in batched mode.

## Observability

AuditCore emits OpenTelemetry-compatible metrics for SQL operations and audit pipeline health. These metrics are exposed via `System.Diagnostics.Metrics` and can be consumed by any OpenTelemetry-compatible collector.

### Meters

| Meter Name | Purpose |
|------------|---------|
| `MillWorks.AuditCore.Sql` | SQL command duration, errors, retries, slow queries |
| `MillWorks.AuditCore.OutboxDrainer` | Outbox processing failures (transactional outbox mode) |

### SQL Metrics

The `AuditSqlCommandInterceptor` (registered automatically when using `UseEntityFramework`) emits:

| Metric | Type | Description |
|--------|------|-------------|
| `sql_command_duration_seconds` | Histogram | Per-command latency with `operation` and `outcome` tags |
| `sql_errors_total` | Counter | Error counts by `category` (throttling, connection_pool, deadlock, transient, other) |
| `sql_slow_commands_total` | Counter | Commands exceeding 1 second threshold |
| `sql_retries_total` | Counter | Retry attempts from `ResilientAuditLogger` with `operation` tag |

**Azure SQL error classification.** The interceptor classifies SQL Server errors into categories useful for Azure SQL diagnostics:

| Category | Error Codes | Meaning |
|----------|-------------|---------|
| `throttling` | 10928, 10929, 40501, 40544, 40549-40553, 49918-49920 | DTU/resource throttling |
| `connection_pool` | -2, 233, 10053, 10054, 10060, 40143, 40197, 40613 | Connection pool exhaustion or network issues |
| `deadlock` | 1205 | SQL Server deadlock victim |
| `transient` | -1, 2, 53, 121, 1232, 4060, 4221, 18456 | Transient network/auth errors |
| `other` | * | Unclassified errors |

### Subscribing to Metrics

**OpenTelemetry SDK (recommended):**

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("MillWorks.AuditCore.Sql");
        metrics.AddMeter("MillWorks.AuditCore.OutboxDrainer");
        
        // Add exporters
        metrics.AddOtlpExporter();           // For Jaeger, Grafana, etc.
        // metrics.AddConsoleExporter();     // For local dev
    });
```

**OTLP Collector (Jaeger, Grafana, etc.):**

```json
{
  "OpenTelemetry": {
    "OtlpEndpoint": "http://localhost:4317"
  }
}
```

Run a local collector:

```bash
docker run -d --name jaeger -p 4317:4317 -p 16686:16686 jaegertracing/all-in-one:latest
```

Then open http://localhost:16686 for traces.

**Console exporter (local dev):**

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddMeter("MillWorks.AuditCore.Sql");
        metrics.AddConsoleExporter();
    });
```

### Key Metrics to Watch

| Metric | What to Alert On |
|--------|------------------|
| `sql_errors_total{category="throttling"}` | Azure SQL DTU limits hit — scale up or optimize queries |
| `sql_errors_total{category="deadlock"}` | Deadlocks occurring — review transaction isolation and lock ordering |
| `sql_errors_total{category="connection_pool"}` | Pool exhaustion — increase pool size or reduce connection hold time |
| `sql_slow_commands_total` | Slow queries — add indexes or optimize queries |
| `sql_retries_total` | Non-zero in steady state indicates recurring transient failures |

### Programmatic Error Classification

The interceptor exposes static helpers for use in custom retry logic:

```csharp
// Classify an exception for logging/metrics
string category = AuditSqlCommandInterceptor.ClassifyError(exception);
// Returns: "throttling", "connection_pool", "deadlock", "transient", or "other"

// Check if an exception is likely transient (safe to retry)
bool shouldRetry = AuditSqlCommandInterceptor.IsTransient(exception);
// Returns true for: transient, throttling, deadlock, connection_pool, timeout
```

## Testing

Run the full test suite:

```bash
dotnet test
```

The SQL Server integration lane lives under `MillWorks.AuditCore.Tests.Integration.SqlServer` and uses Testcontainers to spin up a real SQL Server 2022 container. Run the lane in isolation:

```bash
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --filter "FullyQualifiedName~Integration.SqlServer"
```

Docker is required for a real SQL Server run. When Docker is unavailable, every test in the SQL Server lane reports `Inconclusive` rather than `Failed`, so full-suite runs stay green on Docker-less developer machines without masking regressions when the gate runs in CI.

GitHub Actions runs the lane on every PR and push to `main` via the [`SQL Integration`](.github/workflows/sql-integration.yml) workflow.

### Endurance / soak lane

The Phase 6.5 endurance soak lives under `MillWorks.AuditCore.Tests.Integration.Endurance` — deliberately **outside** the `Integration.SqlServer.*` namespace so it does not run on the default SQL lane or in CI. It exercises the integrity chain writer/verifier at 100k events with four concurrent writers and asserts the full chain is valid, the DLQ is empty, and managed memory stays within a 750 MB hard cap. The test class owns its own Testcontainers SQL Server 2022 lifecycle (started in `[OneTimeSetUp]`, disposed in `[OneTimeTearDown]`) so it is fully self-contained and never touches the `Integration.SqlServer` fixture.

Recommended invocation — pass the opt-in variable via `dotnet test -e` so it reaches the NUnit worker process:

```bash
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~Integration.Endurance" \
    -e AUDITCORE_RUN_ENDURANCE=1
```

The bare prefix form (`AUDITCORE_RUN_ENDURANCE=1 dotnet test …`) depends on environment inheritance into the test host and has been observed not to propagate in some `dotnet test` / NUnit worker configurations. Prefer the `-e` form unless you have confirmed inheritance works locally.

Without the opt-in variable the single test reports `Inconclusive` and does no work. Docker must be healthy — the run provisions its own database (`MillWorksAuditCoreSoakTests`) inside a dedicated SQL Server 2022 container, which is dropped on teardown. The test has a 15-minute cancellation budget; a typical run completes well inside that. Each invocation writes `notes.md` and `samples.csv` to `artifacts/phase6.5-soak/<UTC-timestamp>/`; the `artifacts/` directory is gitignored and is intended for local operator review.

## Production Readiness

The current hardening cycle closes the six production gaps tracked in `docs/ProductionHardeningPlan.md`:

| Area | Status |
|------|--------|
| Runtime options binding | `TamperDetectionService` and runtime services consume typed `IOptions<T>` values; production HMAC misconfiguration fails through options validation. |
| SQL Server schema configuration | `EntityFrameworkOptions.Schema` drives the runtime EF model, with schema-aware model caching. Built-in migrations remain anchored to the default `audit` schema; custom schemas are fresh-database-only. |
| Error-message redaction | `AuditEvent.ErrorMessage` is redacted before it is persisted into the serialized audit payload. |
| Fail-closed entity writes | `AuditOptions.FailureMode` supports `Permissive`, `FailClosedForRegulated`, and `FailClosedAlways`; regulated saves can roll back when the interceptor cannot build audit records. |
| Request-audit overflow | `AuditMiddlewareOptions.OverflowPolicy` supports explicit overflow behavior, including `RouteToDeadLetter` for DLQ preservation. |
| SQL Server verification | A Testcontainers-backed SQL Server lane covers migrations, custom schema creation, rowversion behavior, transaction rollback, tamper-chain verification, and retry/DLQ behavior. |
| Sink-based audit pipeline | `IAuditSink` + `ImmediateSink` / `TransactionalOutboxSink` decouple persistence from interceptor; consumer DbContexts no longer require AuditCore entity mapping. Documented in `docs/RedesignPlan.md`. |

For an ACED-style regulated deployment, start from [`docs/ACEDProductionConfiguration.md`](docs/ACEDProductionConfiguration.md): durable HMAC key, digital-signature key paths, `FailClosedForRegulated`, Redis-backed DLQ/locking, `RouteToDeadLetter`, and the default `audit` schema with migrations enabled.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, coding standards, and pull request guidelines.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Citation

If you use MillWorks.AuditCore in academic work or grant-funded research, please cite it:

> Carlberg, J. (2025). MillWorks.AuditCore: Tamper-evident audit logging and compliance platform for .NET. https://github.com/jesserules/millworks.auditcore
