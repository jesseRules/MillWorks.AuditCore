# MillWorks.AuditCore

*Tamper-evident audit logging and compliance platform for .NET*

[![Build Status](https://img.shields.io/github/actions/workflow/status/jesserules/millworks.auditcore/ci.yml?branch=main)](https://github.com/jesserules/millworks.auditcore/actions)
[![NuGet](https://img.shields.io/nuget/v/MillWorks.AuditCore.AspNetCore)](https://www.nuget.org/packages/MillWorks.AuditCore.AspNetCore)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)

MillWorks.AuditCore is a comprehensive audit logging framework for .NET applications that enforces data integrity at the storage layer through cryptographic hash chains and HMAC signatures. Built for organizations operating under HIPAA, FERPA, SOC 2, GDPR, and IRB requirements, it provides tamper-evident logging, field-level encryption, and automated compliance validation -- capabilities that are typically spread across multiple commercial products. The library integrates with Entity Framework Core as a SaveChanges interceptor, capturing every entity change with zero modifications to existing application code.

## Features

### Automatic Entity Auditing
EF Core `SaveChangesInterceptor` automatically captures create, update, and delete operations across all tracked entities. Entities are diffed at the property level, recording old values, new values, and changed property lists. No attribute decoration or manual logging calls required -- opt out with `[NoAudit]` when needed.

### Tamper Detection
Every audit event is linked into a cryptographic hash chain. Each record's SHA-256 hash incorporates the previous record's hash, forming an append-only ledger that detects insertion, deletion, or modification of any record in the sequence. Chain integrity can be verified on demand or on a schedule. Tamper alerts are recorded as security events.

### Field-Level Encryption
AES-256-GCM encryption for sensitive audit fields, applied transparently through EF Core value converters. Mark properties with `[EncryptedField]` or `[SensitiveData(AutoEncrypt = true)]`. Key management supports Azure Key Vault for cloud deployments or file-based key storage for DMZ and air-gapped environments. Per-field key derivation ensures compromise of one field does not expose others.

### Compliance Validation
Built-in validators for seven regulatory standards: GDPR (Articles 17, 25, 30, 32), HIPAA (45 CFR Part 164), FERPA (34 CFR Part 99), SOC 2 (Trust Services Criteria), ISO 27001 (Annex A), PCI-DSS, and STIG. Validators inspect the audit log for required controls and produce structured compliance reports with pass/fail per rule, severity, regulation references, and remediation recommendations. Enforcement mode can block non-compliant operations at the interceptor level.

### Dead Letter Queue
Audit events that fail to persist (database timeout, transient fault) are captured in a dead letter queue rather than lost. Supports in-memory, file-system, and Redis-backed providers. A background processor automatically retries failed events with configurable retry policies.

### Distributed Coordination
Redis-based distributed locking ensures hash chain consistency across multiple application instances writing audit events concurrently. Falls back to in-memory locking for single-instance deployments.

### Archival
Completed audit records can be archived to Azure Blob Storage with integrity verification. Archives are checksummed and can be restored on demand. Background archival runs on a configurable schedule with retention policies.

### Custom Providers
Per-entity audit enrichment through the `IAuditProvider` interface. Register providers for specific entity types to control which actions are audited, add domain-specific metadata, and mask sensitive properties before they reach the audit log.

### Query and Reporting
Full-text search, date-range filtering, entity trail reconstruction, user activity timelines, event type distribution, and top-user reports. Compliance reports can be generated per standard for any date range. All query services use `AsNoTracking()` for read performance.

## Quick Start

Install the ASP.NET Core integration package (pulls in all dependencies):

```shell
dotnet add package MillWorks.AuditCore.AspNetCore
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

### Automatic Entity Change Tracking

Any entity saved through a DbContext that has the audit interceptor registered will be captured automatically:

```csharp
dbContext.Products.Add(new Product { Name = "Widget", Price = 9.99m });
await dbContext.SaveChangesAsync();
// AuditEvent is created with Action = Created, entity name, key values,
// and a full snapshot of the new property values -- no code changes needed.
```

## Architecture

```
Abstractions          (pure .NET -- no EF, no ASP.NET dependencies)
  Models, DTOs, Interfaces, Enums, Constants, Requests, Responses
    |
EntityFramework       (EF Core data layer)
  DbContext, Entities, Interceptor, Repositories, Migrations, Value Converters
    |
Providers             (entity-specific audit enrichment)
  IAuditProvider, BaseAuditProvider, per-entity implementations
    |
Services              (business logic)
  Compliance validators, Tamper detection, Encryption, Dead letter queue,
  Query/Search/Report, Archival, Maintenance, Mapping
    |
AspNetCore            (application entry point)
  MillWorksAuditBuilder, ServiceCollectionExtensions, Middleware, Options
```

**Abstractions** is a pure .NET library with no framework dependencies. It can be referenced from console applications, background workers, Azure Functions, or any .NET host without pulling in ASP.NET Core or Entity Framework.

Each layer depends only on the layers below it. The `AspNetCore` package is the top-level integration point that wires everything together through dependency injection.

## Configuration

The full builder API:

```csharp
builder.Services.AddMillWorksAudit(audit =>
{
    // Application identity
    audit.Options.ApplicationName = "MyApp";
    audit.Options.Environment = "Production";
    audit.Options.EnableDigitalSignatures = true;
    audit.Options.HmacKey = "<base64-hmac-key>";

    // Entity Framework storage (required)
    audit.UseEntityFramework(ef =>
    {
        ef.ConnectionString = "Server=...";
        ef.Schema = "audit";              // SQL Server schema
        ef.MigrateOnStartup = true;       // Apply EF migrations on startup
        ef.EnsureDatabaseCreated = false;  // Or use EnsureCreated for dev
        ef.MigrationTimeoutSeconds = 120;
    });

    // Security and tamper detection
    audit.UseSecurity(security =>
    {
        security.EnableTamperDetection = true;
        security.UseRedisLocking = true;
        security.RedisConnectionString = "localhost:6379";
    });

    // Compliance validation
    audit.UseCompliance(compliance =>
    {
        compliance.Standards.Add(ComplianceStandard.HIPAA);
        compliance.Standards.Add(ComplianceStandard.FERPA);
        compliance.Standards.Add(ComplianceStandard.SOC2);
        compliance.EnableAutomaticValidation = true;
        compliance.DataRetentionDays = 2555; // 7 years for HIPAA
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
        resilience.EnableDeadLetterQueue = true;
        resilience.DeadLetterProvider = DeadLetterProvider.FileSystem;
        resilience.EnableBackgroundProcessor = true;
    });

    // Field-level encryption (Azure Key Vault)
    audit.UseFieldEncryption("https://my-vault.vault.azure.net/");

    // Or file-based encryption for air-gapped environments
    // audit.UseFieldEncryptionWithFileStorage("/secure/keys", "<master-key>");

    // Custom per-entity audit providers
    audit.RegisterProviders(registry =>
    {
        registry.AddProvider<PatientAuditProvider>("Patient");
        registry.AddProvider<FinancialRecordAuditProvider>("FinancialRecord");
    });
});
```

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

## Database Schema

All tables are created under a configurable SQL Server schema (default: `audit`).

| Table | Purpose | Key Columns |
|-------|---------|-------------|
| `AuditEvents` | Primary audit event store | `Id`, `EventType`, `EntityName`, `Action`, `UserId`, `JsonData`, `StartDate`, `CorrelationId` |
| `AuditLogs` | Entity change log with old/new values | `Id`, `EntityName`, `EntityId`, `Action`, `OldValues`, `NewValues`, `ChangedProperties`, `UserId` |
| `AuditIntegrity` | Hash chain records for tamper detection | `Id`, `AuditEventId`, `EventHash`, `PreviousHash`, `SequenceNumber`, `HmacSignature` |
| `AuditArchiveRecords` | Metadata for archived audit batches | `Id`, `ArchiveId`, `BlobPath`, `EventCount`, `Checksum`, `ArchivedAt`, `RestoredAt` |
| `AuditSecurityEvents` | Security-relevant events and tamper alerts | `Id`, `EventType`, `Severity`, `Description`, `SourceIp`, `DetectedAt` |

Append-only entities (`AuditEvents`, `AuditIntegrity`, `AuditSecurityEvents`) do not carry update/delete audit columns to avoid unnecessary storage overhead.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for development setup, coding standards, and pull request guidelines.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## Citation

If you use MillWorks.AuditCore in academic work or grant-funded research, please cite it:

> Carlberg, J. (2025). MillWorks.AuditCore: Tamper-evident audit logging and compliance platform for .NET. https://github.com/jesserules/millworks.auditcore
