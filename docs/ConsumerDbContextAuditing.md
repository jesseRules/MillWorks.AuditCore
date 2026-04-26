# Plan — Consumer DbContext Auditing

Two upstream gaps surfaced while wiring `MillWorks.Compliance` (in
`/Users/jesse/RiderProjects/MillWorks/MillWorks.Compliance/`) onto AuditCore's EF
interceptor. Both affect every consumer library that references
`MillWorks.AuditCore.EntityFramework` and registers `AuditSaveChangesInterceptor` on
its own DbContext — today that's `Identity`, `DataProcessing`, `Notification`,
`SqlBuilder`, `Document`, `Media`, `Git`, `Ai`, `Compliance`. The interceptor
nominally captures their writes but in practice produces partial output (gap #1) or
no chain entries at all (gap #2).

This plan closes both gaps without breaking the existing
`AuditApplicationDbContext`-only happy path that ships in v1.6.x.

## Goals

1. Consumer DbContexts that opt in via a single line of model configuration get a
   complete audit pipeline: `AuditLog` rows + `AuditEvent` rows + hash-chained
   `AuditIntegrity` rows + HMAC signatures + `FailClosedForRegulated` enforcement.
2. The opt-in is discoverable, documented, and forces failure rather than silent
   no-op when misconfigured.
3. No behavior change for `AuditApplicationDbContext` saves.
4. Existing consumer apps that haven't opted in keep their current behavior (no
   audit), unchanged.

## Non-goals

- Auto-injecting audit entities into every DbContext via convention. Opt-in is a
  feature: silent inclusion would surprise consumers and complicate their
  migrations.
- A second `IAuditLogger` registration per consumer. The interceptor stays the
  single audit-write path; this plan extends what it writes, it doesn't add a new
  pipeline.
- Per-consumer schema customization. Audit tables stay under the configured
  `EntityFrameworkOptions.Schema` (default `audit`), shared across all consumers.

## Item 01 — Discoverable consumer opt-in for `AuditLogEntity`

### Problem

`AuditSaveChangesInterceptor.GetAuditableEntries`
(`src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs:419`)
silently returns null when the saving context's model lacks `AuditLogEntity`, then
the interceptor early-exits without recording anything. The downstream
`context.Set<AuditLogEntity>().Add(...)` call (line 456) requires the type to be
mapped on the saving DbContext's model.

Consumers can opt in by manually declaring `modelBuilder.Entity<AuditLogEntity>()`
plus `ToTable("AuditLogs", "audit", t => t.ExcludeFromMigrations())` plus
`HasKey(e => e.Id)` in their own `OnModelCreating`. That works but is undiscoverable
— there's no doc, no compiler hint, and no runtime warning when the consumer
forgets it.

### Deliverable

A first-party `ModelBuilder` extension that does the right thing in one call:

```csharp
// In MillWorks.AuditCore.EntityFramework/Extensions/ModelBuilderAuditExtensions.cs
public static class ModelBuilderAuditExtensions
{
    /// <summary>
    /// Registers AuditCore entity types as external (non-owned) on the consumer
    /// DbContext's model so AuditSaveChangesInterceptor can write through it. The
    /// physical tables are owned by AuditApplicationDbContext's migrations —
    /// ExcludeFromMigrations() prevents the consumer from emitting duplicate DDL.
    /// </summary>
    /// <param name="modelBuilder">The consumer DbContext model builder.</param>
    /// <param name="schema">
    /// Schema where the audit tables live; must match
    /// <see cref="EntityFrameworkOptions.Schema"/> on the singleton AuditCore
    /// configuration. Defaults to "audit".
    /// </param>
    public static ModelBuilder IncludeAuditCoreEntitiesAsExternal(
        this ModelBuilder modelBuilder,
        string schema = "audit")
    {
        // Required by AuditSaveChangesInterceptor.GetAuditableEntries +
        // ProcessAuditableEntries.
        modelBuilder.Entity<AuditLogEntity>(b =>
        {
            b.ToTable("AuditLogs", schema, static t => t.ExcludeFromMigrations());
            b.HasKey(static e => e.Id);
        });

        // Required by Item 02 — chained event + integrity rows for consumer writes.
        modelBuilder.Entity<AuditEventEntity>(b =>
        {
            b.ToTable("AuditEvents", schema, static t => t.ExcludeFromMigrations());
            b.HasKey(static e => e.Id);
        });
        modelBuilder.Entity<AuditIntegrityEntity>(b =>
        {
            b.ToTable("AuditIntegrity", schema, static t => t.ExcludeFromMigrations());
            b.HasKey(static e => e.Id);
        });
        modelBuilder.Entity<AuditSecurityEventEntity>(b =>
        {
            b.ToTable("SecurityEvents", schema, static t => t.ExcludeFromMigrations());
            b.HasKey(static e => e.Id);
        });
        // ArchiveRecord and IntegrityWorkItems are written only by
        // AuditApplicationDbContext background services; consumer contexts do not
        // need to map them.

        return modelBuilder;
    }
}
```

### Verification

- [ ] Unit test: `ModelBuilder.IncludeAuditCoreEntitiesAsExternal()` adds the
  expected entity types and marks them `ExcludeFromMigrations`.
- [ ] Integration test: a consumer-style `DbContext` (built inside the AuditCore
  test project) calls `IncludeAuditCoreEntitiesAsExternal()` in `OnModelCreating`,
  saves a non-audit entity, and `audit.AuditLogs` ends up with one row per change.
- [ ] Doc: README "Quick Start" gains a "Consumer DbContext (auto-audited)"
  section that shows the one-line opt-in.

### Detection / fail-loud

Add a startup-time check (`IHostedService` or
`AuditOptionsValidator`-adjacent) that scans every registered `DbContextOptions<T>`
for an attached `AuditSaveChangesInterceptor`. If the context's model lacks
`AuditLogEntity`, log a `Warning` with the entity type name and a remediation
hint:

```
The audit interceptor is attached to MillWorks.Project.Data.ProjectDbContext, but
its model does not include AuditLogEntity. Saves through this context will not be
audited. Call modelBuilder.IncludeAuditCoreEntitiesAsExternal("audit") in
ProjectDbContext.OnModelCreating, or remove the interceptor.
```

Not a hard throw — a consumer that intentionally registers the interceptor for a
non-audited context should not fail boot. Warning + actionable message is enough.

### Scope of changes

- `src/MillWorks.AuditCore.EntityFramework/Extensions/ModelBuilderAuditExtensions.cs` (new)
- `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs`
  (register the startup-time check hosted service)
- `tests/MillWorks.AuditCore.Tests/EntityFramework/ConsumerDbContextAuditingTests.cs` (new)
- `README.md` (Quick Start section)

## Item 02 — Hash-chain + event rows for consumer DbContext saves

### Problem

`CaptureForProviderDispatch`
(`src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs:673`)
and `DispatchProvidersAsync` (line 711) both early-return unless
`context is AuditApplicationDbContext`. As a result, consumer DbContext saves
produce only `AuditLogEntity` rows — no `AuditEventEntity`, no
`AuditIntegrityEntity`, no HMAC signature, no input to
`ITamperDetectionService.VerifyChainIntegrityAsync`, and no condition for
`AuditFailureMode.FailClosedForRegulated` to actually fire on (since fail-closed
triggers on audit-build failures, and the build path is short-circuited before any
event is built).

This is the half of the regulated-app posture that does not currently work for
consumer libraries. `MillWorks.Compliance`'s `[PHI]` markers are correct; the
posture wired in `MillWorks.Api/Program.cs` is correct; but no audit failure for a
Compliance entity can ever trigger fail-closed because Compliance saves never
attempt to build event / integrity rows.

### Deliverable

In `ProcessAuditableEntries` (or a sibling method invoked alongside it), build
`AuditEventEntity` records for each entity write and chain them through
`ITamperDetectionService.CreateIntegrityRecordBatchAsync` — for both
`AuditApplicationDbContext` and consumer DbContexts that have
`AuditEventEntity` + `AuditIntegrityEntity` in their model (Item 01 ensures
they do).

Roughly:

```csharp
private async Task ChainEventsForConsumerContextAsync(
    DbContext context,
    List<EntityEntry> auditableEntries,
    string? correlationId,
    CancellationToken ct)
{
    if (context.Model.FindEntityType(typeof(AuditEventEntity)) is null)
        return; // Item 01 wasn't applied — interceptor is in degraded mode.

    var sp = ResolveScopedServiceProvider(context);
    if (sp is null) return;

    var tamper = sp.GetService<ITamperDetectionService>();
    if (tamper is null) return;

    var events = new List<AuditEventEntity>(auditableEntries.Count);
    foreach (var entry in auditableEntries)
    {
        events.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            EventId = Guid.NewGuid(),
            EventType = $"{entry.Entity.GetType().Name}.{MapAction(entry.State)}",
            User = ResolveUserId(sp),
            InsertedDate = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
            // ... entity / action fields ...
        });
    }
    context.Set<AuditEventEntity>().AddRange(events);

    // Build integrity rows from the events. Use the batch API so the chain stays
    // intact under one lock acquisition.
    var integrity = await tamper.CreateIntegrityRecordBatchAsync(
        events.Select(MapToIntegrityDto).ToList(), ct);

    context.Set<AuditIntegrityEntity>().AddRange(integrity.Select(MapToEntity));
}
```

Two design choices to validate:

1. **Where the chaining runs.**
   - `ChainEventsForConsumerContextAsync` runs inside `ProcessAuditableEntries`'s
     `try` block so a failure trips the same fail-closed path
     `AuditFailureMode.FailClosedForRegulated` already covers.
   - `await` inside `SavingChangesAsync` keeps everything in the consumer's
     `SaveChangesAsync` transaction. Integrity-record construction must not open
     its own transaction.
2. **Lock semantics.**
   - The integrity batch writer takes the SQL `sp_getapplock` documented in the
     README (and the in-process semaphore for non-SQL providers). Calling it from
     consumer transactions must not deadlock against
     `AuditApplicationDbContext`'s own integrity writes — both paths take the same
     lock, so verify the lock is reentrant or the path is serialized.

### Verification

- [ ] Unit test: chain-write logic invoked against a mock
  `ITamperDetectionService` produces the expected event/integrity DTOs.
- [ ] Integration test (in AuditCore.Tests, via the consumer-style test
  DbContext from Item 01): create / update / soft-delete an entity through the
  consumer context; verify `audit.AuditEvents` has 3 rows and
  `ITamperDetectionService.VerifyChainIntegrityAsync()` returns
  `IsValid = true, ChainBroken = false, TotalEvents >= 3`.
- [ ] Integration test: consumer-context write that triggers fail-closed (e.g.
  injecting an `IFieldEncryptionService` that throws for a `[PHI]` entity) rolls
  back the entire `SaveChangesAsync` and surfaces `AuditIntegrityException`.
- [ ] Concurrency test: parallel writes from `AuditApplicationDbContext` and a
  consumer context against the same database don't deadlock or break the chain
  ordering.

### Scope of changes

- `src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs`
- `tests/MillWorks.AuditCore.Tests/EntityFramework/ConsumerDbContextChainTests.cs`
  (new)
- `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/ConsumerContextChainConcurrencyTests.cs`
  (new — uses Testcontainers SQL Server)
- README.md "Tamper Detection" section: clarify that hash-chain coverage applies to
  consumer contexts that opted in via Item 01.

## Item 03 — `MillWorks.AuditCore` Meter instruments (telemetry parity)

### Problem

AuditCore exposes runtime counters via the `IAuditDiagnostics` interface but does
not currently publish `System.Diagnostics.Metrics.Meter` instruments under the
`MillWorks.AuditCore` namespace. Downstream consumers that wire OpenTelemetry /
Prometheus / Application Insights against `Meter` cannot observe AuditCore activity
without scraping the `IAuditDiagnostics` properties manually.

`MillWorks.Compliance.Tests/IntegrationTests/ComplianceAuditDiagnosticsTests`
already wires a `MeterListener` as a regression guard — when AuditCore starts
publishing meters, that test fails loudly so consumers update their assertions.

### Deliverable

Add a static `Meter` named `"MillWorks.AuditCore"` and publish counters/histograms
that mirror `IAuditDiagnostics`:

| Property | Instrument | Type |
|---|---|---|
| `InterceptorAuditFailureCount` | `millworks.audit.interceptor.failures` | Counter |
| `SnapshotSerializationFallbackCount` | `millworks.audit.snapshot.fallback` | Counter |
| `SnapshotSerializationTotalFailureCount` | `millworks.audit.snapshot.total_failure` | Counter |
| `DlqStoreOperationCount` / `DlqStoreFailureCount` | `millworks.audit.dlq.store` (with `result` tag) | Counter |
| `IntegrityBatchFlushCount` / `IntegrityBatchFlushFailureCount` | `millworks.audit.integrity.batch_flush` (with `result` tag) | Counter |
| (new) chain build duration | `millworks.audit.integrity.build_duration` | Histogram |
| (new) interceptor invocation duration | `millworks.audit.interceptor.duration` | Histogram |

`IAuditDiagnostics` keeps its existing surface — both surfaces tick on the same
event so consumers that prefer one over the other stay supported.

### Verification

- [ ] Unit test (`MeterListener`-based): triggering each diagnostic counter also
  records a measurement on the corresponding instrument.
- [ ] Update `MillWorks.Compliance.Tests/IntegrationTests/ComplianceAuditDiagnosticsTests`
  to assert specific instruments (rather than its current "no instruments
  published" guard).

### Scope of changes

- `src/MillWorks.AuditCore.Services/Diagnostics/AuditMeter.cs` (new)
- `src/MillWorks.AuditCore.Services/Diagnostics/AuditDiagnostics.cs` (publish on
  same code paths as the counter increments)
- `tests/MillWorks.AuditCore.Tests/Diagnostics/AuditMeterTests.cs` (new)
- README.md "Production Readiness" table: add a row for Meter instruments.

## Order of operations

Items must land in order — Item 02 depends on Item 01's entity registration, and
Item 03 is independent but easier to verify after Item 02 has populated the chain.

1. Item 01 — opt-in extension method + startup warning (low risk, contained)
2. Item 02 — chain-write for consumer contexts (the architectural change)
3. Item 03 — Meter instruments (telemetry parity)

## Validation against MillWorks.Compliance

After each item ships in a new AuditCore NuGet release, bump
`MillWorks.Compliance.csproj` and `MillWorks.Compliance.Tests.csproj` to that
version and rerun the integration suite at
`/Users/jesse/RiderProjects/MillWorks/MillWorks.Compliance.Tests/IntegrationTests/`:

- After Item 01: no behavior change (Compliance already opts in inline; just
  swap the inline `modelBuilder.Entity<AuditLogEntity>()` block in
  `ComplianceDbContext.OnModelCreating` for the new
  `IncludeAuditCoreEntitiesAsExternal("audit")` call).
- After Item 02: pivot `ComplianceAuditChainTests` back to its original shape —
  `ITamperDetectionService.VerifyChainIntegrityAsync` should now return
  `IsValid = true, TotalEvents >= 3` for a Compliance create/update/delete cycle.
  Add the long-deferred `ComplianceFailClosedTests` (failing
  `IFieldEncryptionService` for a `[PHI]` entity → `AuditIntegrityException` +
  rollback).
- After Item 03: update `ComplianceAuditDiagnosticsTests`'s `MeterListener` guard
  to assert specific instrument names (`millworks.audit.interceptor.duration`
  fires once per Compliance save, etc).

## Open questions

- **Do we want consumer-context writes to share the same hash chain as
  `AuditApplicationDbContext` writes, or maintain a separate per-context chain?**
  The default in this plan is "single global chain" — every event in the audit DB
  links to the previous event regardless of which DbContext produced it. That
  matches forensic expectations (one chronological history) but adds contention
  on the chain lock. Per-consumer chains are forensically weaker and complicate
  cross-context correlation.
- **HMAC key scope.** Same key for all consumers, or per-consumer subkeys derived
  from the master? Per-consumer subkeys would let a forensics team verify a
  Compliance-only chain without trusting the general HMAC, but the builder API
  doesn't currently support subkey derivation.
- **Backward compatibility for in-flight consumers.** Identity, DataProcessing,
  Notification, etc. currently silently miss audit records. Once Items 01–02
  ship, do we add a one-time migration or just document the opt-in path and let
  each library decide when to adopt?
