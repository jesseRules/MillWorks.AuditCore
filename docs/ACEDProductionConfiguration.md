# ACED Production Configuration Snippet

> **Note (2026-04-21):** This document is retained as an **ACED-style regulated deployment reference**. The canonical ACED application configuration is now owned by the ACEDS repository; this snippet remains useful as a worked example for any regulated AuditCore consumer (HMAC key sourcing, digital-signature key paths, fail-closed mode, Redis DLQ/locking, schema/migration posture).

This snippet captures a representative regulated production posture for AuditCore. It is a reference for the consuming application; values shown as environment variables or paths are deployment inputs, not secrets to commit.

## appsettings.Production.json

```json
{
  "ConnectionStrings": {
    "AuditSqlServer": "Server=tcp:<aced-sql-host>,1433;Database=ACED_Audit;Encrypt=True;TrustServerCertificate=False;"
  },
  "Audit": {
    "ApplicationName": "ACED",
    "Environment": "Production",
    "Enabled": true,

    "HmacKey": "",
    "EnableDigitalSignatures": true,
    "DigitalSignaturePrivateKeyPath": "/etc/aced/audit/signing/private-key.pem",
    "DigitalSignaturePublicKeyPath": "/etc/aced/audit/signing/public-key.pem",

    "FailureMode": "FailClosedForRegulated",
    "AllowPassThroughRedactor": false,

    "Schema": "audit",
    "MigrateOnStartup": true,
    "EnsureDatabaseCreated": false,
    "FailOnMigrationError": true,
    "MigrationTimeoutSeconds": 300,

    "UseRedisLocking": true,
    "RedisConnectionString": "",
    "EnableBatchedIntegrityWrites": false,

    "QueueCapacity": 1000,
    "EnqueueTimeout": "00:00:00.100",
    "DrainTimeout": "00:00:30",
    "OverflowPolicy": "RouteToDeadLetter",

    "EnableDeadLetterQueue": true,
    "DeadLetterProvider": "Redis",
    "EnableBackgroundProcessor": true,
    "MaxRetries": 3,
    "RetryDelaySeconds": 1,
    "IncludeStackTraces": false,
    "FailFastOnDlqUnavailable": true,
    "ReprocessIntervalMinutes": 5,
    "DeadLetterQueueMaxBatchSize": 100
  }
}
```

## Secret Sources

- `Audit:HmacKey` must come from a durable secret provider such as Azure Key Vault, Kubernetes Secret, or the deployment environment. Do not store it in JSON. The value must be stable across instances and restarts, and at least 32 characters.
- `Audit:RedisConnectionString` must come from the same secret source as the Redis credential. It is used for distributed integrity locking and the Redis dead-letter queue.
- The digital-signature private key path must point at a mounted secret readable only by the ACED process identity. The public key path can be mounted read-only alongside it for verification.

Environment variable example:

```bash
Audit__HmacKey=<key-from-secret-store>
Audit__RedisConnectionString=<redis-endpoint-and-secret>
```

## Program.cs Wiring

```csharp
builder.Services.AddMillWorksAudit(builder.Configuration, audit =>
{
    audit.Options.ApplicationName = "ACED";
    audit.Options.Environment = builder.Environment.EnvironmentName;
    audit.Options.HmacKey = builder.Configuration["Audit:HmacKey"];
    audit.Options.EnableDigitalSignatures = true;
    audit.Options.FailureMode = AuditFailureMode.FailClosedForRegulated;
    audit.Options.AllowPassThroughRedactor = false;

    audit.UseEntityFramework(ef =>
    {
        ef.ConnectionString = builder.Configuration.GetConnectionString("AuditSqlServer")!;
        ef.Schema = "audit";
        ef.MigrateOnStartup = true;
        ef.EnsureDatabaseCreated = false;
        ef.FailOnMigrationError = true;
    });

    audit.UseSecurity(security =>
    {
        security.UseRedisLocking = true;
        security.RedisConnectionString = builder.Configuration["Audit:RedisConnectionString"];
        security.DigitalSignaturePrivateKeyPath = builder.Configuration["Audit:DigitalSignaturePrivateKeyPath"];
        security.DigitalSignaturePublicKeyPath = builder.Configuration["Audit:DigitalSignaturePublicKeyPath"];
        security.EnableBatchedIntegrityWrites = false;
    });

    audit.UseMiddleware(middleware =>
    {
        middleware.OverflowPolicy = RequestAuditOverflowPolicy.RouteToDeadLetter;
        middleware.QueueCapacity = 1000;
        middleware.EnqueueTimeout = TimeSpan.FromMilliseconds(100);
        middleware.DrainTimeout = TimeSpan.FromSeconds(30);
    });

    audit.UseResilience(resilience =>
    {
        resilience.EnableDeadLetterQueue = true;
        resilience.DeadLetterProvider = DeadLetterProvider.Redis;
        resilience.EnableBackgroundProcessor = true;
        resilience.IncludeStackTraces = false;
        resilience.FailFastOnDlqUnavailable = true;
        resilience.ReprocessIntervalMinutes = 5;
    });
});
```

## Schema Decision

ACED production should use the default `audit` schema with `MigrateOnStartup = true`. Packaged migrations are intentionally anchored to `audit`, including `audit.__EFMigrationsHistory`.

Custom schemas are supported only for fresh databases created from the runtime model with `EnsureDatabaseCreated = true`; they are not a production migration path for ACED.

## Operational Assertions

- Regulated entity writes use `FailClosedForRegulated`, so an interceptor audit-build failure rolls back the business `SaveChanges`.
- Request-audit queue overflow uses `RouteToDeadLetter`, preserving overflow events in the DLQ when Redis is available.
- SQL Server provider retry strategy is not enabled around explicit transactions; `ResilientAuditLogger` owns retry and DLQ behavior for direct logger writes.
- `AllowPassThroughRedactor` remains false in production. ACED must register a redactor appropriate for PHI/PII before handling real data.
