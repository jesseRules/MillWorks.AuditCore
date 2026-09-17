# Database Provider Interoperability Plan

**Status:** Reviewed; implementation not started

**Originally proposed:** 2026-06-06

**Last code review:** 2026-08-06

**Goal:** make AuditCore correct on explicitly supported EF Core relational providers without regressing tamper evidence, transactional outbox atomicity, schema lifecycle, or operational safety.

## Review Outcome

The original conclusion still holds: the shipped runtime is SQL Server-first and PostgreSQL/MySQL are not supported storage providers. SQLite has meaningful test and direct-`DbContext` coverage, but it is not selectable through the public `UseEntityFramework()` builder and has no supported migration lane.

Work completed since the original plan improves the baseline but does not complete an interoperability phase:

- SQL Server now has a Testcontainers CI lane covering empty-database migration, schema override, outbox draining, optimistic concurrency, and cross-context integrity append concurrency.
- SQLite model branches now provide SQLite timestamp defaults, quoted filters/check constraints, SQL Server type remapping, and application-managed concurrency tokens.
- The transactional outbox writer now prefers a mapped `AuditOutboxEntity` change-tracker path. That path lets the consumer provider generate SQL; only the unmapped, explicit-transaction fallback remains SQL Server-specific raw SQL.
- The outbox drainer retains SQL Server's atomic `UPDATE TOP ... OUTPUT` claim and a provider-neutral EF fallback. The fallback is exercised by SQLite tests but is not proven for multi-process PostgreSQL/MySQL contention.
- Duplicate-key and deadlock helpers contain PostgreSQL-oriented detection without an Npgsql dependency. This is preparatory code only; there is no PostgreSQL provider package or integration lane proving it.

No PostgreSQL or MySQL runtime registration, model branch, migrations, advisory lock, maintenance strategy, package reference, Testcontainer, or CI lane exists.

## Verified Code Baseline

| Area | Verified state on 2026-08-06 | Consequence |
|------|-------------------------------|-------------|
| Public runtime registration | `MillWorksAuditBuilder.UseEntityFramework()` unconditionally calls `UseSqlServer(...)` | Consumers cannot select PostgreSQL, MySQL, or SQLite through the supported builder |
| Options | `EntityFrameworkOptions` has connection, schema, migration, seeding, and timeout settings; it has no provider or migrations assembly | Runtime and schema lifecycle cannot select a provider |
| Options validation | `EntityFrameworkOptionsValidator` reserves SQL Server schema names and applies one 128-character identifier rule | Validation is SQL Server-specific |
| Migration history schema | Runtime and design-time setup hard-code `__EFMigrationsHistory` to schema `audit`, not `EntityFrameworkOptions.Schema` | Existing custom-schema support does not include the migrations history table |
| Design-time factory | `DesignTimeDbContextFactory` always calls `UseSqlServer(...)`; only the connection string is configurable | Migration generation is SQL Server-only |
| Provider packages | The shipped EF project references only `Microsoft.EntityFrameworkCore.SqlServer`; SQLite is test-only | No first-class PostgreSQL/MySQL/SQLite runtime package strategy exists |
| Migrations | One SQL Server migration set through `20260719131302_AddAuditEventsUserCoveringIndex` | Migrations contain SQL Server types, defaults, schemas, filters, and index features |
| Model | Explicit branches exist for SQL Server behavior, SQLite, and InMemory; all other relational providers fall into SQL Server SQL/default branches | PostgreSQL/MySQL would receive invalid `GETUTCDATE()`, bracket quoting, and SQL Server-specific index configuration |
| Model cache | `AuditModelCacheKeyFactory` keys by context type, schema, and design-time flag, but not provider | Replacing EF's default key can allow a model built for one provider to be reused for another |
| Concurrency token | SQL Server uses database-generated rowversion; every other provider gets an application-managed byte-array token | Useful foundation, but untested on PostgreSQL/MySQL mappings |
| Integrity append lock | SQL Server uses transaction-owned `sp_getapplock`; all other providers return without a database lock and use process-local serialization upstream | Tamper-chain appends are unsafe across application instances outside SQL Server |
| Transactional outbox write | Mapped entity path is EF/provider-generated; unmapped explicit-transaction path emits bracketed SQL Server `VALUES`/`WHERE NOT EXISTS` SQL | Mapped mode has a portable foundation; raw-SQL mode is SQL Server-only |
| Outbox duplicate handling | SQL Server and SQLite are covered; helper has unproven PostgreSQL detection and no MySQL detection | Each new provider needs real exception tests, including raw-command exceptions |
| Outbox claim | SQL Server uses `UPDATE TOP ... OUTPUT`; others use query + conditional `ExecuteUpdateAsync` | Portable path is not a single atomic claim statement and needs multi-worker contention tests |
| Maintenance | Size query uses `sys.*`; optimization runs `UPDATE STATISTICS [audit].[AuditEvents]`; failures are caught | Non-SQL providers degrade by executing invalid SQL first; optimization also ignores configured schema |
| Command metrics | `AuditSqlCommandInterceptor` directly references `Microsoft.Data.SqlClient` and Azure SQL codes and is always registered | Non-SQL providers receive a SQL Server-specific interceptor |
| CI | General unit/SQLite suite plus a dedicated SQL Server Testcontainers workflow | No PostgreSQL or MySQL proof lane exists |

## Non-Negotiable Correctness Constraints

- Provider support includes runtime registration, model metadata, schema creation/evolution, transactional outbox behavior, integrity locking, maintenance behavior, and repeatable integration tests. LINQ queries compiling is insufficient.
- The integrity append lock is a transaction/connection-level serialization primitive. The TTL-based `IAuditDistributedLockService` may elect an outbox or DLQ worker, but must not allocate tamper-chain sequence numbers.
- A provider without a proven cross-process integrity append lock cannot claim multi-instance tamper-evidence correctness.
- Transactional outbox writes must remain in the consumer's transaction. A portability fallback must never commit an audit row independently.
- SQL Server remains the default and its retry strategy remains disabled unless the explicit-transaction design changes.
- SQLite remains single-process development/test storage unless a separate production support decision is made.

## Support Tiers

| Tier | Required guarantee |
|------|--------------------|
| Tier 1 | Runtime reads/writes, provider migrations, both supported outbox modes, atomic multi-worker drain claims, cross-process integrity append serialization, explicit maintenance behavior, and CI coverage |
| Tier 2 | Runtime and schema lifecycle are supported, but named operational or multi-instance features are unavailable and fail explicitly rather than silently degrading |
| Experimental | Direct/local use is tested for selected paths; no production correctness claim |
| Unsupported | The provider may compile through EF, but AuditCore makes no compatibility or correctness claim |

Current and target position:

| Provider | Current | Target |
|----------|---------|--------|
| SQL Server | Tier 1 product path | Tier 1; preserve behavior |
| PostgreSQL | Unsupported | First additional Tier 1 candidate |
| MySQL | Unsupported | Defer until PostgreSQL establishes the abstractions; initially Tier 2 at most |
| SQLite | Experimental through direct/test setup | Experimental, single-process only |

Do not advertise a target tier as shipped support until its acceptance suite passes in CI.

## Architecture Decisions Required Before Coding

### 1. Provider package ownership

Choose one packaging model before adding provider APIs:

- provider-specific companion packages, such as `MillWorks.AuditCore.EntityFramework.PostgreSql`; or
- provider packages referenced by the main EntityFramework package; or
- a consumer-supplied `DbContextOptionsBuilder` configuration callback plus provider services supplied by the consumer.

**Recommendation:** use companion packages or a consumer-supplied provider configuration hook. Referencing every EF provider from the main package unnecessarily couples release cadence and dependency surface. Provider-specific migrations and lock/maintenance implementations should live with the corresponding provider package.

### 2. Outbox modes supported per provider

Decide whether Tier 1 requires both writer modes:

- mapped `AuditOutboxEntity` through the change tracker; and
- unmapped entity with an explicit transaction through a SQL dialect.

**Recommendation:** require both for Tier 1 because the public writer deliberately supports both atomicity contracts. If a provider supports mapped mode only, report that limitation as Tier 2 and fail raw mode before issuing SQL.

### 3. Schema semantics

PostgreSQL and SQL Server support schemas; SQLite does not in the same sense, and MySQL treats database/schema concepts differently. Define whether `EntityFrameworkOptions.Schema` is required, ignored, translated, or rejected for each provider. The migrations history table must use the same resolved schema policy instead of the current hard-coded `audit` value.

## Phase 0: Honest Support Documentation And Capability Model

**Status:** Not started.

Create one internal provider descriptor/capability source. Avoid independent provider-name strings across the model, outbox, locks, maintenance, and registration.

```csharp
internal sealed record AuditDatabaseCapabilities(
    string ProviderName,
    bool SupportsSchemas,
    bool SupportsProviderMigrations,
    bool SupportsTransactionScopedAdvisoryLocks,
    bool SupportsMappedOutboxWrites,
    bool SupportsRawSqlOutboxWrites,
    bool SupportsAtomicOutboxClaim,
    bool SupportsDatabaseSizeEstimation,
    bool SupportsTableOptimization,
    bool UsesDatabaseGeneratedRowVersion);
```

Deliverables:

- Add a database-provider support matrix to the README and package documentation.
- State explicitly that the current product path is SQL Server and SQLite is test/development-only.
- Centralize provider names, detection, capabilities, and resolved schema behavior.
- Ensure unsupported critical capabilities fail during startup or first relevant operation with a clear provider-specific message.

Acceptance criteria:

- Documentation does not imply PostgreSQL/MySQL production support.
- Provider decisions are not scattered string comparisons.
- Unknown providers default to unsupported, not SQL Server behavior.

## Phase 1: Runtime Provider Selection And Package Boundary

**Status:** Not started.

Add intentional provider selection without forcing non-SQL providers through `UseSqlServer`. Keep SQL Server as the backwards-compatible default.

The exact option/API shape follows the package decision. If AuditCore owns selection directly, the minimum public option is:

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

Implementation requirements:

- Use one registration path for runtime and the provider-specific design-time factories.
- Resolve migration history schema from configured provider/schema rather than hard-coding `audit`.
- Make identifier and reserved-schema validation provider-aware.
- Register Azure SQL classification only for SQL Server, or split generic command timing from provider error classification.
- Add provider identity to `AuditModelCacheKeyFactory`; retain schema and design-time identity.
- Reject an unknown or unavailable provider at startup.

Acceptance criteria:

- The supported builder intentionally selects each installed provider.
- Default configuration produces the same SQL Server options and interceptors as today.
- A non-SQL provider receives no SQL Server-specific EF configuration or Azure SQL classifier.
- Model cache tests prove different providers cannot share a cached model.

## Phase 2: Provider-Correct Model Metadata

**Status:** Partially prepared for SQLite; not started for PostgreSQL/MySQL.

Replace the current `InMemory / SQLite / else-is-SQL-Server` structure with explicit provider strategies. Unknown relational providers must fail model construction.

Provider decisions include:

- UTC timestamp default SQL;
- filtered/partial index support and syntax;
- check-constraint quoting;
- included-column indexes;
- rowversion versus application-managed concurrency tokens;
- `DateTimeOffset`, `Guid`, JSON text, and byte-array mappings;
- schemas and table qualification.

Acceptance criteria:

- PostgreSQL/MySQL never receive `GETUTCDATE()`, bracketed identifiers, or SQL Server included-index metadata.
- SQL Server retains rowversion and the user-covering index.
- Application-managed concurrency tokens work on each non-SQL provider.
- Metadata/DDL tests cover every advertised provider and reject an unknown provider.

## Phase 3: Schema Lifecycle And Migrations

**Status:** SQL Server only.

The existing migration set is SQL Server-specific and should remain the SQL Server history. Add separate migrations and snapshots per supported provider, preferably in its companion package/assembly.

Requirements:

- Select provider and connection string explicitly at design time. Preserve SQL Server as the default for the existing workflow.
- Keep provider snapshots isolated; do not attempt to make the existing SQL Server migration files portable.
- Document exact generate, script, apply, and upgrade commands.
- Test both empty-database creation and at least one real upgrade transition.
- Define SQLite as `EnsureCreated`-only or give it a migration set; do not leave the lifecycle ambiguous.

Acceptance criteria:

- Every Tier 1 provider creates an empty database and upgrades an older schema in CI.
- Migration history uses the resolved provider/schema configuration.
- SQL Server's existing migration IDs and upgrade behavior remain unchanged.

## Phase 4: Transactional Outbox Portability

**Status:** Mapped mode has a portable foundation; raw mode and non-SQL contention are incomplete.

Preserve the existing hybrid writer contract:

1. When the consumer maps `AuditOutboxEntity`, stage rows through the change tracker so the consumer provider generates SQL.
2. When the entity is unmapped but an explicit transaction exists, use a provider dialect on that connection/transaction.
3. Otherwise fail closed with `AuditOutboxAtomicityException`.

For raw mode, introduce a dialect with identifier delimiting, parameter limits, duplicate-safe insertion, and table qualification. Do not assume SQL Server's `VALUES` alias form is portable.

Also make duplicate detection explicit per provider. The current PostgreSQL type-name/`Data["SqlState"]` path must be verified against real Npgsql exceptions, and MySQL codes must be implemented and tested.

For draining, treat the existing EF claim path as a correctness candidate, not a proven atomic algorithm. Its conditional status update prevents the straightforward double-claim race, but the multi-statement candidate/update/reload sequence has only SQLite coverage. Prove its behavior under real client/server isolation and multi-worker contention, or replace it with provider-specific atomic claim SQL (`FOR UPDATE SKIP LOCKED`, provider equivalent, or another transactionally sound strategy).

Acceptance criteria:

- Mapped and raw writer modes commit/rollback atomically on every Tier 1 provider.
- Duplicate idempotency keys are success in batch, individual fallback, and races.
- Two or more drainers never process the same lease concurrently.
- SQL Server retains its atomic claim fast path.
- Unsupported outbox modes fail before executing provider-invalid SQL.

## Phase 5: Integrity Advisory Locks

**Status:** SQL Server only.

Keep lock acquisition next to `AuditIntegrityRepository`, but extract provider strategies.

| Provider | Candidate strategy | Support consequence |
|----------|--------------------|---------------------|
| SQL Server | `sp_getapplock`, `LockOwner = Transaction` | Existing behavior |
| PostgreSQL | transaction-scoped advisory lock, with bounded/cancellable acquisition | Strong Tier 1 candidate |
| MySQL | `GET_LOCK` / `RELEASE_LOCK` | Session-scoped; requires explicit exception-safe release and connection-pool tests |
| SQLite | process-local serializer plus native single-writer behavior | Experimental single-process only |

Requirements:

- Require an active transaction wherever lock ownership is transaction-scoped.
- Use a stable, collision-safe advisory lock key derived from the audit database/schema/chain identity.
- Test separate contexts and connections, timeout/cancellation, rollback release, and connection reuse.
- Update SQL Server-specific comments in `TamperDetectionService`, repository interfaces, options, and README after the abstraction lands.

Acceptance criteria:

- Concurrent append tests validate a gap-free chain across independent connections.
- Lock failure never falls back silently to an unsafe append.
- SQL Server behavior and its existing concurrency suite remain unchanged.

## Phase 6: Provider-Aware Maintenance

**Status:** Not started.

Replace exception-driven degradation with a provider strategy. Separate portable cleanup/statistics queries from provider-specific database-size and optimization operations.

Requirements:

- SQL Server retains its size query and `UPDATE STATISTICS`, using the configured schema.
- PostgreSQL/MySQL use provider-correct operations only when the behavior is well-defined.
- SQLite/unsupported operations return an explicit unsupported/degraded result and log once at the appropriate level.
- Consider evolving the maintenance contract so callers can distinguish an exact size, an estimate, and an unsupported operation; a `long` or `bool` alone cannot express that distinction.

Acceptance criteria:

- No provider executes another provider's system-catalog or maintenance SQL.
- Configured schema is honored.
- Tests assert both supported execution and explicit degradation.

## Phase 7: Integration Matrix And CI Gates

**Status:** SQL Server and SQLite baseline exists; PostgreSQL/MySQL lanes do not.

| Capability | SQL Server | PostgreSQL | MySQL | SQLite |
|------------|------------|------------|-------|--------|
| provider selection/startup validation | required | required | required if shipped | required |
| model/DDL validation | required | required | required if shipped | required |
| empty schema + upgrade migrations | existing/extend | required | required if shipped | define lifecycle |
| normal read/write + concurrency token | existing | required | required if shipped | existing/extend |
| mapped and raw transactional outbox | existing/extend | required | required if Tier 1 | mapped required; raw per tier |
| multi-worker outbox claim | existing/extend | required | required if shipped | local contention only |
| cross-process integrity append | existing | required | required for multi-instance claim | not claimed |
| lock timeout and release | existing/extend | required | required if shipped | n/a |
| maintenance behavior | required | required | required if shipped | degraded behavior required |

CI rules:

- Keep the existing SQL Server Testcontainers workflow as a required gate.
- Add PostgreSQL as the first new provider gate.
- Add MySQL only when its product tier and provider package are committed.
- Keep fast SQLite tests in the general suite, but do not treat them as proof of client/server concurrency semantics.

## Revised Delivery Slices

| Slice | Scope | Depends on | Estimate |
|-------|-------|------------|----------|
| A | Package/API decision, support matrix, capability registry | none | 1 day |
| B | Provider-aware runtime registration, validation, migration-history schema, model cache key | A | 1.5-2 days |
| C | Explicit model strategies and provider metadata tests | B | 2 days |
| D | PostgreSQL package, design-time factory, migrations, empty/upgrade lane | B, C | 3-4 days |
| E | Outbox writer dialects, real duplicate detection, atomic PostgreSQL claim | B, D | 2-3 days |
| F | Lock strategy extraction, SQL Server parity, PostgreSQL advisory lock | B, D | 2 days |
| G | Maintenance strategies and result semantics | B, D | 1-2 days |
| H | PostgreSQL CI matrix, docs, failure-path and concurrency hardening | E, F, G | 2-3 days |
| I | MySQL product decision and implementation | proven PostgreSQL abstractions | separately estimate |

Estimated PostgreSQL Tier 1 effort: **14-18 engineering days**, excluding production soak and package-release work. MySQL is intentionally not included until its tier and session-lock risk are accepted.

## Risks And Rollback

- **Migration maintenance:** every Tier 1 provider adds a long-lived snapshot and upgrade lane. If that cost is not acceptable, keep the provider unsupported.
- **Integrity correctness:** an advisory-lock implementation error can create an apparently valid but forked/gapped chain. Provider support remains blocked until cross-connection tests pass.
- **Outbox claim races:** the generic EF fallback is multi-statement and cannot be assumed atomic under client/server concurrency.
- **Model cache contamination:** the custom cache key must include provider before a process can construct the same context type/schema for different providers.
- **Package drift:** provider packages must remain compatible with the repository's EF Core version.
- **MySQL lock lifetime:** session-owned locks and pooled connections make exception/release behavior higher risk than PostgreSQL transaction-owned locks.

Each phase is independently reversible by removing the new provider registration/package and retaining SQL Server as the only Tier 1 provider. Never roll back by silently routing an unknown provider through SQL Server SQL or by weakening transactional/tamper guarantees.

## Definition Of Done For A New Provider

A provider is supported only when all of the following are true:

- it is intentionally selectable through the public configuration surface;
- options and schema semantics are provider-correct;
- model metadata and types are provider-correct;
- empty creation and upgrades use maintained provider migrations;
- mapped and advertised raw outbox modes are atomic;
- multi-worker claim behavior is proven;
- duplicate/deadlock detection uses real provider exceptions;
- integrity appends serialize across connections/instances, or the limitation is explicit in a lower tier;
- maintenance operations execute provider-correct SQL or explicitly report unsupported behavior;
- the provider's integration matrix is a required CI gate; and
- README/package support claims match the achieved tier.

## Recommendation

Implement PostgreSQL first and use it to establish the provider boundary. Keep SQL Server as the unchanged default, SQLite as the local/test lane, and MySQL out of the committed scope until PostgreSQL proves the migrations, outbox, locking, and maintenance abstractions. This produces a smaller credible support claim than attempting four providers at once and makes every advertised guarantee testable.
