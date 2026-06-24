# MillWorks.AuditCore Sample Project

A working ASP.NET Core Web API that demonstrates the full MillWorks.AuditCore feature set: manual audit logging, automatic entity change tracking, tamper detection, compliance reporting, and archival.

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- **SQL Server** -- either run the bundled Docker container (recommended, see below) or point the connection string at any edition (LocalDB, Express, Developer, or a remote instance).
- **Docker** (recommended) -- to spin up SQL Server locally with the included `docker-compose.yml`.
- **Redis** (optional) -- only required if you enable `UseRedisLocking` in the security configuration. The sample defaults to `UseRedisLocking = false`.
- **Azure Storage** (optional) -- only required if you provide an `AzureStorage` connection string for archival.

## How to Run

### Option A -- Docker (recommended)

A `docker-compose.yml` next to this README brings up SQL Server 2022 locally. The
sample app itself still runs from your IDE or the CLI against that container.

1. Start the database and wait for it to pass its health check (run from this folder):

   ```shell
   docker compose up -d --wait
   ```

   `--wait` blocks until the container's health check reports healthy (~30s on a
   first boot under Rosetta), so the app doesn't race a still-starting SQL Server.

2. Run the sample. The bundled launch profile runs in the **Development**
   environment, and `appsettings.Development.json` already points `DefaultConnection`
   at the container, so no further configuration is needed:

   ```shell
   dotnet run
   ```

3. Open the Swagger UI at `https://localhost:7115/swagger` to explore and test the API endpoints.

4. When you're done, stop the database (add `-v` to also delete its data volume):

   ```shell
   docker compose down
   ```

**Apple Silicon:** the `mcr.microsoft.com/mssql/server` image is amd64-only and runs
under Rosetta emulation (the compose file sets `platform: linux/amd64`). SQL Server
2022 runs cleanly this way. The compose file also documents a SQL Server 2025 option
and an ARM-native (but retired) Azure SQL Edge option if you prefer those.

**Already using port 1433?** If another local SQL instance (e.g. a SQL Server or
Azure SQL Edge container) is already bound to `1433`, `docker compose up` fails with
`Bind for 0.0.0.0:1433 failed: port is already allocated`. Either stop that instance,
or remap the host port in `docker-compose.yml` (e.g. `"14330:1433"`) and update the
`Server=localhost,1433` portion of the connection string in `appsettings.Development.json`
to match.

### Option B -- Your own SQL Server

Set the `DefaultConnection` connection string in `appsettings.json` (or via
environment variable / user secrets), then run `dotnet run`:

```json
"ConnectionStrings": {
  "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=AuditCoreSample;Trusted_Connection=True;Encrypt=False"
}
```

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
| **Search** | `POST /search`, `GET /search/entity/{entityType}`, `GET /security`, `GET /distinct/users`, `GET /distinct/event-types` |
| **Reporting** | `GET /summary`, `GET /chart-data`, `GET /activity/summary`, `GET /distribution/event-types`, `GET /top-users` |
| **Tamper detection** | `GET /integrity/verify/{eventId}`, `POST /integrity/verify-chain`, `GET /integrity/verify-sequence`, `GET /integrity/detect-tampering`, `GET /integrity/export-proof/{eventId}`, `GET /chain/status` |
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
