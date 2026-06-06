# Database Provider Interoperability Plan

**Status:** Proposed  
**Date:** 2026-06-06  
**Goal:** make AuditCore run correctly on multiple EF Core relational providers without regressing tamper evidence, transactional outbox behavior, or operational safety.

## Executive Summary

AuditCore is currently a SQL Server-first library with SQLite/InMemory branches for tests and local scenarios. It is not yet a provider-interoperable library.

Provider interoperability is broader than replacing `sp_getapplock`. Current SQL Server assumptions appear in public bootstrapping, design-time migrations, model defaults, outbox raw SQL, command metrics, maintenance SQL, and integrity append locking.

Treat this as a staged portability effort. Do not claim PostgreSQL or MySQL support until runtime registration, model metadata, schema lifecycle, outbox writes, integrity locking, and tests are all provider-correct.

## Current Code Reality

| Area | Current State | Interop Impact |
|------|---------------|----------------|
| Runtime EF registration | `MillWorksAuditBuilder.UseEntityFramework()` always calls `UseSqlServer(...)` | Public builder cannot configure PostgreSQL/MySQL/SQLite runtime storage |
| Options | `EntityFrameworkOptions` only has `ConnectionString`, schema, and migration flags | No provider selection or provider-specific migration assembly/history table |
| Options validation | Validator is embedded in `EntityFrameworkOptions.cs` and reserves SQL Server schemas | Validation is SQL Server-biased and may reject valid provider-specific usage |
| Design-time factory | `DesignTimeDbContextFactory` always calls `UseSqlServer(...)` | Generated migrations and design-time model are SQL Server-only |
| Packages | EntityFramework project references `Microsoft.EntityFrameworkCore.SqlServer` only | Npgsql/MySQL/SQLite package strategy is not first-class |
| Model defaults | `AuditDbContext` branches for SQL Server / SQLite / InMemory only | PostgreSQL/MySQL inherit SQL Server defaults such as `GETUTCDATE()` and bracketed predicates |
| Rowversion | SQL Server uses `IsRowVersion`; non-SQL Server uses application-managed concurrency tokens | Good foundation, but must be tested per provider |
| Integrity append lock | `AuditIntegrityRepository` uses SQL Server `sp_getapplock`; others are no-op | Multi-instance tamper-chain appends are unsafe outside SQL Server |
| Transactional outbox writer | `AuditOutboxWriter` emits SQL Server bracketed identifiers and SQL Server-oriented insert SQL | Consumer-context outbox writes are not provider-portable |
| Outbox drainer claim | SQL Server has `UPDATE TOP ... OUTPUT`; others use EF portable fallback | PostgreSQL/MySQL can work initially via fallback, but need concurrency tests |
| Maintenance SQL | `GetAuditDatabaseSizeAsync` queries `sys.tables`; `OptimizeAuditTablesAsync` runs `UPDATE STATISTICS [audit].[AuditEvents]` | Admin APIs execute SQL Server-only SQL or degrade through exceptions |
| Command metrics | `AuditSqlCommandInterceptor` references `Microsoft.Data.SqlClient` and Azure SQL error codes | Metrics interceptor is SQL Server-specific and should not be universal |

## Important Constraints

- The hash-chain append lock is transaction-bound serialization. It is not equivalent to the existing TTL-based `IAuditDistributedLockService`.
- `IAuditDistributedLockService` can still elect one outbox drainer leader, but it should not be reused for integrity sequence allocation.
- SQLite is useful for single-process development and tests, but it is not a multi-instance tamper-chain provider.
- Provider support includes schema lifecycle. Runtime LINQ portability alone is not enough.

## Support Tiers

Define support levels before implementation so docs do not overclaim.

| Tier | Meaning |
|------|---------|
| Tier 1 | Runtime writes/reads, outbox, migrations/schema lifecycle, tamper detection, and required tests are supported |
| Tier 2 | Runtime behavior is supported, but some admin/maintenance operations or multi-instance guarantees are degraded and documented |
| Experimental | Can be configured for local or limited use, but correctness is not guaranteed under production concurrency |
| Unsupported | Provider may compile through EF, but AuditCore makes no correctness claim |

Recommended initial target:

- SQL Server: Tier 1, unchanged behavior
- PostgreSQL: Tier 1 only after advisory locks and provider migrations are proven
- MySQL: Tier 2 until lock semantics and concurrency tests are proven under load
- SQLite: Experimental, single-instance/dev-test only

## Phase 0: Honest Documentation And Capability Model

**Objective:** make support claims accurate before code changes.

Add an internal capability model instead of a single provider-supported boolean:

```csharp
internal sealed record AuditDatabaseCapabilities(
    string ProviderName,
    bool SupportsTransactionScopedAdvisoryLocks,
    bool SupportsProviderMigrations,
    bool SupportsDatabaseSizeEstimation,
    bool SupportsTableOptimization,
    bool SupportsFilteredIndexes,
    bool UsesDatabaseGeneratedRowVersion,
    bool SupportsAtomicOutboxClaimSql);
```

Deliverables:

- Add provider support matrix to README/docs.
- State that current released behavior is SQL Server-first.
- Mark SQLite as single-instance/test oriented.
- Add provider capability constants or strategies in one internal location.

Acceptance criteria:

- No doc claims PostgreSQL/MySQL production support before runtime, migrations, outbox, locking, and tests exist.
- Provider capability decisions are centralized, not scattered across string checks.

## Phase 1: Runtime Provider Selection

**Objective:** remove hidden SQL Server-only runtime registration.

Recommended option shape:

```csharp
public enum AuditDatabaseProvider
{
    SqlServer,
    PostgreSql,
    MySql,
    Sqlite
}

public sealed class EntityFrameworkOptions
{
    public AuditDatabaseProvider Provider { get; set; } = AuditDatabaseProvider.SqlServer;
    public string ConnectionString { get; set; } = string.Empty;
    public string Schema { get; set; } = "audit";
    public string? MigrationsAssembly { get; set; }
}
```

Implementation notes:

- Keep SQL Server as the default provider unless intentionally making a breaking change.
- Register the provider inside one helper used by runtime and design-time paths.
- Keep SQL Server retry disabled in the current path unless transaction semantics are reworked.
- Register `AuditSqlCommandInterceptor` only for SQL Server, or rename/generalize it into a provider-aware command metrics interceptor.
- Revisit `EntityFrameworkOptionsValidator`: reserved schema names and identifier rules should be provider-aware.

Files:

- `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs`
- `src/MillWorks.AuditCore.EntityFramework/Options/EntityFrameworkOptions.cs`
- `src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSqlCommandInterceptor.cs`
- project package references

Acceptance criteria:

- Runtime configuration can intentionally select SQL Server, PostgreSQL, MySQL, or SQLite.
- SQL Server behavior remains unchanged by default.
- Non-SQL Server providers do not receive SQL Server-specific interceptors or options.
- Provider-specific packages are referenced intentionally and documented.

## Phase 2: Design-Time And Migration Strategy

**Objective:** make schema creation and schema evolution provider-specific and repeatable.

The current migrations are SQL Server-flavored (`datetimeoffset`, `rowversion`, `GETUTCDATE()`, bracketed check constraints, SQL Server filtered indexes). They should not be treated as portable migrations.

Recommended strategy: separate migration sets per provider.

Example layout:

- `Migrations/SqlServer`
- `Migrations/PostgreSql`
- `Migrations/MySql`
- `Migrations/Sqlite` only if SQLite schema evolution is supported beyond tests

Design-time factory requirements:

- Accept provider from args or environment, for example `AUDIT_MIGRATION_PROVIDER`.
- Accept connection string from `AUDIT_MIGRATION_CONNECTION_STRING`.
- Select provider-specific migrations assembly or migrations namespace.
- Preserve SQL Server as the default design-time provider for existing workflows.

Files:

- `src/MillWorks.AuditCore.EntityFramework/Data/DesignTimeDbContextFactory.cs`
- `src/MillWorks.AuditCore.EntityFramework/Migrations/*`
- project/build scripts for provider-specific migration generation

Acceptance criteria:

- Each Tier 1 provider can create an empty schema from migrations.
- Each Tier 1 provider can apply the next migration cleanly.
- Migration docs show exactly how to generate and apply migrations per provider.
- SQL Server migration history table behavior remains unchanged.

## Phase 3: Model Configuration Portability

**Objective:** make `AuditDbContext` generate valid metadata for each provider.

Current gaps:

- PostgreSQL/MySQL fall into SQL Server defaults.
- `GETUTCDATE()` is applied to non-SQL Server providers.
- Filter/check constraint SQL uses SQL Server brackets outside SQLite.
- SQLite column type overrides exist, but equivalent provider-specific type decisions are not centralized.

Centralize provider names and SQL snippets:

```csharp
internal static class AuditProviderNames
{
    public const string SqlServer = "Microsoft.EntityFrameworkCore.SqlServer";
    public const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";
    public const string PostgreSql = "Npgsql.EntityFrameworkCore.PostgreSQL";
    public const string MySqlPomelo = "Pomelo.EntityFrameworkCore.MySql";
    public const string MySqlOracle = "MySql.EntityFrameworkCore";
}
```

Provider-specific concerns:

- timestamp default SQL
- filtered-index predicate syntax
- check-constraint identifier quoting
- rowversion vs application-managed concurrency tokens
- schema support and migration history table naming
- provider-specific type mappings for `DateTimeOffset`, `Guid`, JSON strings, and byte arrays where needed

Files:

- `src/MillWorks.AuditCore.EntityFramework/Data/AuditDbContext.cs`
- `src/MillWorks.AuditCore.EntityFramework/Data/AuditModelCacheKeyFactory.cs`

Acceptance criteria:

- PostgreSQL does not receive `GETUTCDATE()` or bracketed identifiers.
- MySQL does not receive SQL Server filtered-index/check-constraint syntax.
- SQL Server rowversion behavior remains unchanged.
- Model cache keys include provider and schema when needed.
- Model tests cover each provider's generated metadata.

## Phase 4: Provider-Portable Transactional Outbox SQL

**Objective:** keep transactional outbox behavior correct on consumer DbContexts.

`AuditOutboxWriter` currently writes raw SQL like:

- `INSERT INTO [schema].[AuditOutbox]`
- SQL Server bracketed column identifiers
- `VALUES` table constructor aliasing
- `WHERE NOT EXISTS` duplicate handling

That SQL runs against the consumer's DbContext and transaction. It must become provider-aware before the outbox can be claimed as portable.

Recommended abstraction:

```csharp
internal interface IAuditOutboxSqlDialect
{
    int MaxParametersPerCommand { get; }
    string DelimitIdentifier(string identifier);
    string QualifyTable(string schema, string table);
    string BuildInsertIfNotExistsSql(
        string schema,
        int rowCount,
        IReadOnlyList<string> columnNames);
}
```

Implementation notes:

- Keep idempotent duplicate handling.
- Keep writes inside the consumer transaction.
- Avoid provider packages leaking into the abstractions project.
- Continue to use EF parameters; do not concatenate values.
- The drainer's existing portable EF claim path may be acceptable initially for PostgreSQL/MySQL, but needs concurrency tests. Provider-specific atomic claim SQL can be a later optimization.

Files:

- `src/MillWorks.AuditCore.Services/Sinks/AuditOutboxWriter.cs`
- `src/MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`
- new internal SQL dialect/strategy files

Acceptance criteria:

- Outbox writes succeed on each supported provider.
- Duplicate idempotency keys are still treated as success.
- SQL Server outbox write behavior remains unchanged.
- PostgreSQL/MySQL do not execute SQL Server bracketed identifier SQL.

## Phase 5: Advisory Lock Portability

**Objective:** preserve tamper-chain append correctness across providers.

Keep integrity locking near `AuditIntegrityRepository`. Do not reuse `IAuditDistributedLockService` for sequence allocation.

Recommended abstraction:

```csharp
internal interface IDatabaseAdvisoryLock
{
    bool IsSupported { get; }
    string ProviderName { get; }

    Task AcquireAsync(
        DbContext context,
        string resourceName,
        int timeoutMs,
        CancellationToken cancellationToken);
}
```

Provider strategy:

| Provider | Strategy | Tier Notes |
|----------|----------|------------|
| SQL Server | `sp_getapplock` with `LockOwner = Transaction` | Existing Tier 1 behavior |
| PostgreSQL | `pg_advisory_xact_lock` or `pg_try_advisory_xact_lock` loop | Good transaction-scoped match |
| MySQL | `GET_LOCK` / `RELEASE_LOCK` | Session-scoped; Tier 2 until proven safe |
| SQLite | no-op plus process-local serializer | Experimental single-instance only |

Implementation notes:

- `AcquireAppendLockAsync` must still require an active transaction for providers with transaction-scoped locks.
- MySQL lock release must be explicit and exception-safe because the lock is session-scoped.
- `TamperDetectionService` comments and docs currently mention SQL Server `sp_getapplock`; update them to describe provider strategy.
- Existing local serializer fallback remains useful for providers without cross-process lock support.

Files:

- `src/MillWorks.AuditCore.EntityFramework/Repositories/AuditIntegrityRepository.cs`
- new `Locking` strategy files under EntityFramework
- `src/MillWorks.AuditCore.Services/TamperDetectionService.cs` docs/comments

Acceptance criteria:

- SQL Server behavior is unchanged.
- PostgreSQL concurrent integrity appends serialize across separate contexts/processes.
- Lock timeout behavior is tested.
- Rollback releases transaction-scoped locks.
- MySQL behavior is tested and documented as session-scoped.

## Phase 6: Provider-Aware Maintenance Services

**Objective:** stop admin APIs from assuming SQL Server internals.

Current SQL Server-only operations:

- `GetAuditDatabaseSizeAsync` queries `sys.tables`.
- `OptimizeAuditTablesAsync` runs `UPDATE STATISTICS [audit].[AuditEvents]`.

Recommended abstraction:

```csharp
internal interface IAuditDatabaseMaintenanceStrategy
{
    bool SupportsDatabaseSizeEstimation { get; }
    bool SupportsOptimization { get; }

    Task<long> GetDatabaseSizeAsync(
        AuditDbContext context,
        CancellationToken cancellationToken);

    Task<bool> OptimizeAsync(
        AuditDbContext context,
        CancellationToken cancellationToken);
}
```

Behavior:

- SQL Server retains current behavior.
- PostgreSQL/MySQL implement provider-correct SQL only where safe.
- Unsupported operations return a degraded result and log clearly.
- Do not rely on catching provider SQL failures as the normal degradation path.

Files:

- `src/MillWorks.AuditCore.Services/AuditMaintenanceService.cs`
- new maintenance strategy files

Acceptance criteria:

- PostgreSQL/MySQL do not execute `sys.tables` or `UPDATE STATISTICS [audit]...`.
- Unsupported maintenance operations degrade explicitly.
- `GetAuditStatisticsAsync` reports whether database size is estimated/degraded if possible.

## Phase 7: Test Matrix And CI

**Objective:** prove correctness, not just compile success.

Required integration coverage:

| Area | SQL Server | PostgreSQL | MySQL | SQLite |
|------|------------|------------|-------|--------|
| schema creation from migrations | yes | yes | yes | optional/test |
| normal write/read path | yes | yes | yes | yes |
| outbox write in consumer transaction | yes | yes | yes | yes |
| outbox drain/claim | yes | yes | yes | yes |
| tamper append serialization | yes | yes | yes/documented | local only |
| lock timeout/release | yes | yes | yes | n/a |
| maintenance APIs | yes | yes/degraded | yes/degraded | degraded |
| model metadata validation | yes | yes | yes | yes |

Must-have tests:

- provider selection in `UseEntityFramework`
- design-time factory provider selection
- migrations create provider schema cleanly
- model uses provider-correct default SQL and constraints
- transactional outbox duplicate handling per provider
- concurrent integrity appends across separate DbContexts/connections
- rollback releases advisory lock
- command metrics interceptor does not require `SqlException` on non-SQL Server providers
- maintenance methods do not issue invalid SQL for the active provider

Infrastructure:

- existing SQL Server Testcontainers lane remains required
- add PostgreSQL Testcontainer lane
- add MySQL Testcontainer lane
- keep SQLite local integration tests

## Ticketable Rollout

| Slice | Scope | Depends On | Est. Effort |
|-------|-------|------------|-------------|
| A | Support docs + capability model | None | 0.5 day |
| B | Runtime provider option + provider registration helper | A | 1 day |
| C | Provider-aware options validation and SQL command metrics registration | B | 0.5 day |
| D | Design-time factory + package/provider wiring | B | 1 day |
| E | Migration set strategy and first non-SQL Server migration lane | D | 2-3 days |
| F | Model configuration normalization | B, D | 1.5 days |
| G | Outbox SQL dialect strategy | B, F | 1.5 days |
| H | Advisory lock strategy extraction + SQL Server parity | B | 0.5 day |
| I | PostgreSQL advisory lock support | H | 1 day |
| J | MySQL advisory lock support and documentation | H | 1.5 days |
| K | Maintenance strategy abstraction | B | 1 day |
| L | Integration test matrix + CI | E, F, G, I, J, K | 2-4 days |

Realistic total: 14-18 engineering days, not counting production soak time.

## Risk Analysis

### Biggest Risk: Migration Maintenance

Provider-specific migrations add maintenance cost. If the library does not intend to maintain those lanes, do not claim Tier 1 support.

### Highest Correctness Risk: Integrity Sequence Locking

The tamper chain depends on serialized sequence allocation. PostgreSQL has a good transaction-scoped lock primitive. MySQL does not match as cleanly because `GET_LOCK` is session-scoped.

### Outbox Atomicity Risk

`AuditOutboxWriter` writes into the consumer transaction. Provider-specific SQL must preserve duplicate handling and transaction enrollment.

### Provider Drift Risk

Provider-name checks scattered across services will rot. Centralize detection, capabilities, SQL snippets, and dialects.

### Package Footprint Risk

Referencing every EF provider from the main package increases dependency surface. Decide whether AuditCore ships all providers, provider-specific companion packages, or documented consumer-owned provider packages.

## Rollback Plan

- Phase 1 rollback: keep SQL Server default registration only.
- Phase 2 rollback: keep SQL Server migrations as the only supported migration set.
- Phase 3 rollback: keep PostgreSQL/MySQL marked unsupported until model metadata is provider-correct.
- Phase 4 rollback: disable transactional outbox support on non-SQL Server providers.
- Phase 5 rollback: keep SQL Server advisory locking only; other providers remain unsupported for multi-instance integrity.
- Phase 6 rollback: keep SQL Server maintenance strategy as the only concrete implementation.

Each phase should be independently revertible and should not weaken SQL Server behavior.

## Recommended Implementation Order

1. Phase 0: docs and capabilities
2. Phase 1: runtime provider selection
3. Phase 2: design-time/migration strategy
4. Phase 3: model portability
5. Phase 4: outbox SQL portability
6. Phase 5: advisory locks
7. Phase 6: maintenance strategies
8. Phase 7: full provider CI matrix

This order keeps support claims behind the real blockers: schema lifecycle, outbox atomicity, and integrity locking.

## Success Metrics

| Metric | Before | After |
|--------|--------|-------|
| Public runtime provider choice | SQL Server only | SQL Server, PostgreSQL, MySQL, SQLite |
| Provider-aware schema lifecycle | No | Yes for Tier 1 providers |
| Outbox raw SQL portability | SQL Server only | Provider dialects or provider-safe EF paths |
| Multi-instance integrity correctness outside SQL Server | No | Yes for PostgreSQL; documented/tested for MySQL |
| SQL Server-only maintenance SQL on non-SQL providers | Possible | Eliminated |
| Provider support claims | Risk of overclaiming | Explicit by tier |

## Final Recommendation

Do this work only if AuditCore genuinely intends to support PostgreSQL/MySQL as maintained product paths. The minimum credible standard is:

- provider-aware runtime registration
- provider-correct model metadata
- provider-specific migration lifecycle
- provider-portable transactional outbox writes
- tested integrity locking semantics
- explicit support tiers and degradation behavior

Anything less should be labeled "SQL Server first with experimental provider work," which is a valid product stance as long as it is explicit.
