# MillWorks.AuditCore Sample Project

A working ASP.NET Core Web API that demonstrates the full MillWorks.AuditCore feature set: manual audit logging, automatic entity change tracking, tamper detection, compliance reporting, and archival.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- **SQL Server** -- any edition (LocalDB, Express, Developer, or a remote instance). Set the connection string in `appsettings.json`.
- **Redis** (optional) -- only required if you enable `UseRedisLocking` in the security configuration. The sample defaults to `UseRedisLocking = false`.
- **Azure Storage** (optional) -- only required if you provide an `AzureStorage` connection string for archival.

## How to Run

1. Set the `DefaultConnection` connection string in `appsettings.json` (or via environment variable / user secrets):

   ```json
   "ConnectionStrings": {
     "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=AuditCoreSample;Trusted_Connection=True;"
   }
   ```

2. Run the project:

   ```shell
   dotnet run --project samples/MillWorks.AuditCore.SampleProject
   ```

3. Open the Swagger UI at `https://localhost:<port>/swagger` to explore and test the API endpoints.

The sample is configured with `EnsureDatabaseCreated = true`, so the audit schema tables are created automatically on first run. No manual migrations are needed.

## What's Included

### ProductsController (`/api/products`)

A standard CRUD controller that demonstrates manual audit logging in a typical business scenario:

| Endpoint | Method | Audit Behavior |
|----------|--------|----------------|
| `/api/products` | GET | Logs `Product.List.Viewed` |
| `/api/products/{id}` | GET | Logs `Product.Viewed` or `Product.NotFound` |
| `/api/products` | POST | Logs `Product.Created` with full entity snapshot |
| `/api/products/{id}` | PUT | Uses `CreateScope` to capture old and new values |
| `/api/products/{id}` | DELETE | Logs `Product.Deleted` |
| `/api/products/bulk` | POST | Uses `BeginOperationAsync` / `EndOperationAsync` for batch tracking |
| `/api/products/statistics` | GET | Logs `Product.Statistics.Generated` |

### AuditController (`/api/audit`)

Exposes the MillWorks.AuditCore query, compliance, tamper detection, and archival APIs:

| Category | Endpoints |
|----------|-----------|
| **Basic operations** | `POST /test-event`, `POST /test-event-scope`, `POST /test-operation` |
| **Query** | `GET /events`, `GET /events/{id}`, `GET /events/by-date`, `GET /events/by-user/{username}` |
| **Entity trails** | `GET /trail/{entityName}/{entityId}` |
| **Activity** | `GET /activity/user/{userId}`, `GET /activity/recent` |
| **Search** | `POST /search`, `GET /search/entity/{entityType}`, `GET /distinct/users`, `GET /distinct/event-types` |
| **Reporting** | `GET /summary`, `GET /chart-data`, `GET /activity/summary`, `GET /distribution/event-types`, `GET /top-users` |
| **Tamper detection** | `GET /integrity/verify/{eventId}`, `POST /integrity/verify-chain`, `GET /integrity/verify-sequence`, `GET /integrity/detect-tampering`, `GET /integrity/export-proof/{eventId}` |
| **Compliance** | `POST /compliance/report`, `POST /compliance/anonymize/{userId}`, `GET /compliance/export/{userId}`, `GET /compliance/validate-retention` |
| **Archival** | `POST /archive`, `POST /archive/restore/{archiveId}`, `GET /archives`, `GET /archive/validate/{archiveId}` |

## Configuration

The `appsettings.json` file contains the full configuration surface. Key sections:

### Connection Strings

```json
"ConnectionStrings": {
  "DefaultConnection": "",
  "Redis": "",
  "AzureStorage": ""
}
```

### Encryption

```json
"KeyVault": {
  "Url": "https://your-keyvault.vault.azure.net/"
},
"Encryption": {
  "UseFileStorage": false,
  "KeyStorePath": "/secure/encryption-keys",
  "MasterKey": ""
}
```

Field-level encryption is enabled when either `KeyVault:Url` or `Encryption:UseFileStorage` with a `MasterKey` is provided. If neither is set, encryption is skipped.

### Audit Options

```json
"Audit": {
  "ApplicationName": "MyApp",
  "EnableDigitalSignatures": false,
  "HmacKey": "",
  "RetentionDays": 365,
  "Security": {
    "EnableTamperDetection": true,
    "UseRedisLocking": false
  },
  "Resilience": {
    "EnableDeadLetterQueue": true,
    "DeadLetterProvider": "FileSystem",
    "EnableBackgroundProcessor": true
  }
}
```

Note: The sample's `Program.cs` configures audit services programmatically using the builder API, not from `appsettings.json` sections. The JSON values above are shown for reference; the sample reads only `ConnectionStrings`, `KeyVault`, and `Encryption` from configuration.
