# Database Provider Interoperability Plan

**Goal:** make AuditCore run correctly on multiple EF Core relational providers, not just SQL Server, without regressing tamper-evidence, batching, or operational safety.

**Recommended target:** treat this as a staged portability effort, not a lock-only refactor.

---

## Executive Summary

The original version of this plan was too narrow. The codebase is not only coupled to SQL Server through `sp_getapplock`; it also hardcodes SQL Server in DI registration, design-time migrations, model defaults, and maintenance SQL.

Today, the real state is:

| Area | Current State | Interop Impact |
|------|---------------|----------------|
| EF registration | `UseEntityFramework()` always calls `UseSqlServer(...)` | Non-SQL-Server runtime cannot be configured through the public builder |
| Design-time factory | `DesignTimeDbContextFactory` always uses SQL Server | Migrations are SQL Server-only today |
| Migrations | Snapshot and generated migrations are SQL Server flavored | No provider-specific migration story yet |
| Model defaults | `AuditDbContext` branches only for SQL Server / SQLite / InMemory | PostgreSQL and MySQL would currently inherit bad default SQL/filter syntax |
| Integrity append lock | `AuditIntegrityRepository` uses SQL Server `sp_getapplock`; others are no-op | Multi-instance integrity appends are unsafe outside SQL Server |
| Maintenance SQL | `GetAuditDatabaseSizeAsync` and `OptimizeAuditTablesAsync` use SQL Server SQL | Admin/maintenance APIs are provider-specific |

That means "provider interoperability" has to cover at least five concerns:

1. provider selection and bootstrapping
2. model compatibility
3. migration strategy
4. tamper-detection locking
5. provider-specific maintenance behavior

---

## Current Code Reality

### Confirmed SQL Server Couplings

- `MillWorksAuditBuilder.UseEntityFramework()` hardcodes `UseSqlServer(...)`.
- `DesignTimeDbContextFactory` hardcodes `UseSqlServer(...)`.
- `AuditIntegrityRepository.SupportsCrossProcessAppendLock` is `Context.Database.IsSqlServer()`.
- `AuditIntegrityRepository.AcquireAppendLockAsync()` calls `sp_getapplock`.
- `AuditDbContext` uses SQL Server defaults and filtered-index SQL for every provider except SQLite/InMemory.
- `AuditMaintenanceService.GetAuditDatabaseSizeAsync()` queries `sys.tables`.
- `AuditMaintenanceService.OptimizeAuditTablesAsync()` runs `UPDATE STATISTICS [audit].[AuditEvents]`.
- EntityFramework project/package graph currently references `Microsoft.EntityFrameworkCore.SqlServer`; there is no first-class Npgsql/MySQL package story.

### Important Constraint

The hash-chain append lock is not the same as the existing TTL-based distributed lock service. The integrity append path needs transaction-bound serialization. Reusing `IAuditDistributedLockService` would weaken semantics.

### Good News

Not everything is SQL Server-specific:

- most CRUD/query paths are plain EF LINQ
- `EF.Functions.Like(...)` usage is portable across major relational providers
- concurrency token handling already distinguishes SQL Server rowversion from application-managed tokens elsewhere
- SQLite fallback logic already proves the codebase can branch by provider when needed

---

## Scope and Non-Goals

### In Scope

- SQL Server remains supported and behaviorally unchanged
- PostgreSQL support for runtime writes, reads, outbox, and integrity chain
- MySQL support for runtime writes, reads, outbox, and integrity chain, with clearly documented lock semantics
- provider-aware maintenance behavior
- provider-aware migration story

### Out of Scope for the First Release

- Oracle / MariaDB / CockroachDB / custom EF providers
- zero-breaking-change public API guarantee for every extension point
- perfect feature parity for every admin optimization on day one

---

## Provider Tiers

Define support levels explicitly so the library does not overclaim:

| Tier | Meaning |
|------|---------|
| Tier 1 | Fully supported for normal runtime audit writes, reads, outbox, and tamper detection |
| Tier 2 | Supported for runtime behavior, but some admin/maintenance APIs are degraded or provider-specific |
| Unsupported | Library may compile, but correctness is not guaranteed for multi-instance integrity append |

**Recommended initial target:**

- SQL Server: Tier 1
- PostgreSQL: Tier 1
- MySQL: Tier 2 until lock semantics and migrations are proven under load
- SQLite: single-instance/dev-test only, not Tier 1

---

## Proposed Architecture

### 1. Split Provider Interoperability into Capability Areas

Do not model this as one binary "supports provider X" switch. Add an internal capability model:

```csharp
public sealed record AuditDatabaseCapabilities(
    string ProviderName,
    bool SupportsTransactionScopedAdvisoryLocks,
    bool SupportsProviderMigrations,
    bool SupportsDatabaseSizeEstimation,
    bool SupportsTableOptimization,
    bool SupportsFilteredIndexes,
    bool UsesDatabaseGeneratedRowVersion);
```

This keeps the plan honest. PostgreSQL may be Tier 1 for integrity locking while a maintenance optimization remains Tier 2.

### 2. Separate Runtime Provider Selection from Design-Time Migrations

The current builder and design-time factory both assume SQL Server. Those need to be solved independently:

- runtime: consumers must be able to select the relational provider
- design-time: migrations must be generated/applied with a clear provider strategy

### 3. Keep Integrity Locking at the Repository Layer

The current instinct in the original doc was correct here. `IAuditDistributedLockService` is still the wrong abstraction for hash-chain serialization.

### 4. Treat Migrations as a First-Class Problem

This is the biggest missing piece from the earlier plan. Even if runtime SQL becomes portable, the library is not top-tier if users cannot create/update the schema cleanly for each provider.

---

## Phase 0: Reframe the Public Contract

**Objective:** make the library honest about what "provider support" means before code changes begin.

### Deliverables

- add a provider support matrix to docs
- explicitly state current SQL Server default in README/docs
- define Tier 1 / Tier 2 / Unsupported terminology
- document that SQLite remains single-instance/test oriented

### Acceptance Criteria

- no doc claims PostgreSQL/MySQL support before runtime + migrations + locking are implemented

---

## Phase 1: Provider-Aware Bootstrapping

**Objective:** remove the hardcoded SQL Server registration path.

### Problems to Solve

- `UseEntityFramework()` always uses SQL Server
- design-time factory always uses SQL Server
- package references assume SQL Server only

### Design

Add a provider abstraction at the builder level instead of baking provider selection into `UseEntityFramework()`.

Recommended shape:

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
}
```

Then switch provider registration in one place.

### Files

- `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs`
- `src/MillWorks.AuditCore.EntityFramework/Data/DesignTimeDbContextFactory.cs`
- `src/MillWorks.AuditCore.EntityFramework/*.csproj`
- `src/MillWorks.AuditCore.Services/*.csproj` if provider packages leak there

### Acceptance Criteria

- runtime configuration can choose SQL Server / PostgreSQL / MySQL / SQLite
- design-time creation can target the same provider intentionally
- no provider is selected by hidden SQL Server default outside explicit configuration

---

## Phase 2: Normalize Model Configuration

**Objective:** make `AuditDbContext` generate valid model metadata for each supported provider.

### Current Gaps

- provider branching only distinguishes SQL Server vs SQLite/InMemory
- non-SQLite, non-SQLServer providers currently fall into SQL Server defaults such as `GETUTCDATE()` and bracketed filter SQL

### Design

Replace the current ad hoc branching with provider helpers:

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

Then centralize provider-specific SQL snippets:

- inserted/created timestamp default SQL
- filtered-index predicate syntax
- rowversion vs application-managed tokens
- provider-specific column type remapping only where truly necessary

### Files

- `src/MillWorks.AuditCore.EntityFramework/Data/AuditDbContext.cs`

### Acceptance Criteria

- PostgreSQL does not receive `GETUTCDATE()`
- MySQL does not receive SQL Server bracketed filter SQL
- model snapshot generation is deterministic per provider strategy

---

## Phase 3: Advisory Lock Portability

**Objective:** preserve tamper-detection correctness across providers.

### Recommended Abstraction

Keep the repository method:

- `IAuditIntegrityRepository.AcquireAppendLockAsync()`

Introduce an internal provider strategy behind it:

```csharp
public interface IDatabaseAdvisoryLock
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

### Provider Strategy

| Provider | Strategy | Notes |
|----------|----------|-------|
| SQL Server | `sp_getapplock` | Existing behavior, transaction-scoped |
| PostgreSQL | `pg_advisory_xact_lock` or `pg_try_advisory_xact_lock` loop | Good semantic match |
| MySQL | `GET_LOCK` / `RELEASE_LOCK` | Session-scoped, not a perfect semantic match |
| SQLite | no-op + process-local serializer | single-instance/test only |

### Important Design Decision

MySQL should not be described as equivalent to SQL Server/PostgreSQL. Its lock lifecycle is weaker because `GET_LOCK` is session-scoped. That is acceptable only if documented and tested as a lower-confidence support tier until proven otherwise.

### Files

- `src/MillWorks.AuditCore.EntityFramework/Locking/IDatabaseAdvisoryLock.cs`
- `src/MillWorks.AuditCore.EntityFramework/Locking/SqlServerAdvisoryLock.cs`
- `src/MillWorks.AuditCore.EntityFramework/Locking/PostgreSqlAdvisoryLock.cs`
- `src/MillWorks.AuditCore.EntityFramework/Locking/MySqlAdvisoryLock.cs`
- `src/MillWorks.AuditCore.EntityFramework/Locking/NullAdvisoryLock.cs`
- `src/MillWorks.AuditCore.EntityFramework/Repositories/AuditIntegrityRepository.cs`

### Acceptance Criteria

- SQL Server behavior is unchanged
- PostgreSQL integrity appends are serialized across processes
- MySQL behavior is tested and explicitly documented as session-scoped
- `TamperDetectionService` does not need provider-specific branching beyond the existing local fallback behavior

---

## Phase 4: Migration Strategy

**Objective:** define how schema creation/evolution works per provider.

This is the hardest part and must be settled early.

### Recommended Options

#### Option A: Separate migration sets per provider

Example:

- `Migrations/SqlServer`
- `Migrations/PostgreSql`
- `Migrations/MySql`

**Pros**

- explicit and predictable
- best match for provider-specific DDL differences

**Cons**

- more maintenance

#### Option B: SQL Server migrations only, provider-specific schema bootstrap later

**Pros**

- fastest path

**Cons**

- not credible as top-tier provider support

**Recommendation:** use separate migration sets per provider if PostgreSQL/MySQL are being positioned as real support, not experimental support.

### Files

- `src/MillWorks.AuditCore.EntityFramework/Migrations/*`
- `src/MillWorks.AuditCore.EntityFramework/Data/DesignTimeDbContextFactory.cs`
- build/test tooling for provider-specific migration generation

### Acceptance Criteria

- each supported provider can create a fresh schema from zero
- each supported provider can apply the next migration cleanly
- migration docs explain how to target each provider

---

## Phase 5: Provider-Aware Maintenance Services

**Objective:** stop admin APIs from assuming SQL Server internals.

### Current Gaps

- database size estimation queries `sys.tables`
- optimization runs `UPDATE STATISTICS [audit].[AuditEvents]`

### Design

Move provider-specific maintenance operations behind internal strategies:

```csharp
public interface IAuditDatabaseMaintenanceStrategy
{
    bool SupportsDatabaseSizeEstimation { get; }
    bool SupportsOptimization { get; }
    Task<long> GetDatabaseSizeAsync(AuditDbContext context, CancellationToken cancellationToken);
    Task<bool> OptimizeAsync(AuditDbContext context, CancellationToken cancellationToken);
}
```

If a provider cannot support a given operation safely:

- return a degraded result
- log clearly
- do not fake SQL Server behavior

### Acceptance Criteria

- SQL Server retains current admin functionality
- PostgreSQL/MySQL do not execute SQL Server-only statements
- unsupported maintenance operations degrade explicitly, not accidentally

---

## Phase 6: Test Matrix

**Objective:** prove correctness, not just compile success.

### Required Integration Coverage

| Area | SQL Server | PostgreSQL | MySQL | SQLite |
|------|------------|------------|-------|--------|
| schema creation | yes | yes | yes | yes |
| normal write/read path | yes | yes | yes | yes |
| outbox path | yes | yes | yes | yes |
| tamper append serialization | yes | yes | yes | local only |
| maintenance APIs | yes | yes/degraded | yes/degraded | n/a |

### Must-Have Tests

- concurrent integrity appends across separate contexts/processes
- lock timeout behavior
- rollback releases advisory lock
- model creation on each provider
- migrations create schema cleanly
- maintenance methods do not issue invalid SQL for the active provider

### Infrastructure

- SQL Server container
- PostgreSQL container
- MySQL container
- SQLite test harness remains local

---

## Ticketable Rollout

| Slice | Scope | Depends On | Est. Effort |
|-------|-------|------------|-------------|
| A | Provider support docs + capability model | None | 0.5 day |
| B | Runtime provider selection in builder/options | A | 1 day |
| C | Design-time factory + package/provider wiring | B | 1 day |
| D | Model normalization in `AuditDbContext` | B, C | 1.5 days |
| E | Advisory lock strategy extraction + SQL Server extraction | B | 0.5 day |
| F | PostgreSQL lock support | E | 1 day |
| G | MySQL lock support | E | 1.5 days |
| H | Migration set strategy | C, D | 2-3 days |
| I | Maintenance strategy abstraction | B | 1 day |
| J | Integration test matrix + CI | D, F, G, H, I | 2-3 days |

**Realistic total:** 11-14 engineering days, not counting production soak time.

---

## Risk Analysis

### Biggest Risk: Underestimating Migrations

The earlier plan understated this. Runtime portability without a clean schema lifecycle is incomplete support.

### MySQL Semantic Risk

`GET_LOCK` is session-scoped. That is not equivalent to transaction-scoped advisory locks. If MySQL remains in scope, this should be documented as lower-confidence support until concurrency tests prove it acceptable.

### Provider Drift Risk

Sprinkling provider-name string checks throughout the codebase will create long-term entropy. Centralize provider detection and SQL snippets early.

### Maintenance Feature Creep

Do not block provider support on achieving identical admin optimizations everywhere. It is acceptable for some maintenance operations to degrade by provider, as long as behavior is explicit and safe.

---

## Rollback Plan

- Phase 1-2 rollback: restore SQL Server-only registration/model branching
- Phase 3 rollback: keep SQL Server advisory locking only; others remain unsupported for multi-instance integrity append
- Phase 4 rollback: leave non-SQL-Server providers as experimental until migrations are solved
- Phase 5 rollback: keep SQL Server maintenance strategy as the only concrete implementation

Each phase should be independently revertible.

---

## Recommended Implementation Order

1. Phase 0
2. Phase 1
3. Phase 2
4. Phase 4
5. Phase 3
6. Phase 5
7. Phase 6

This order is intentional:

- provider selection and model correctness come before claiming provider support
- migration strategy should be decided before deep implementation spreads
- advisory locks are critical, but they are not the only blocker

---

## Success Metrics

| Metric | Before | After |
|--------|--------|-------|
| Public runtime provider choice | SQL Server only | SQL Server, PostgreSQL, MySQL, SQLite |
| Multi-instance integrity correctness outside SQL Server | No | Yes for PostgreSQL; documented/tested for MySQL |
| Provider-aware schema lifecycle | No | Yes |
| SQL Server-only maintenance SQL on non-SQL providers | Possible | Eliminated |
| Provider support claims in docs | Overstated risk | Explicit by tier |

---

## Final Recommendation

Do this work only if the library genuinely intends to support PostgreSQL/MySQL as first-class options. If that is the product direction, the right standard is:

- provider-aware runtime registration
- provider-correct model metadata
- real migration story
- tested integrity locking semantics
- explicit support tiers

Anything less is still "SQL Server library with partial portability experiments," which is fine, but it should be labeled that way.
