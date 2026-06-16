# EF Interceptor Coverage Gaps

**Status:** Implemented (2026-06-09)
**Date:** 2026-06-09 (code review)
**Scope:** `AuditSaveChangesInterceptor`, `AuditDbContext`, EF repositories, value conversion

## Problem

Review of the EF layer found several paths where entity changes are persisted with no audit trail and no diagnostic signal, one path where the interceptor can crash the consumer's save in a mode whose contract is "operation allowed," and a retention-cleanup query that cannot work on the production provider.

## Findings

### 1. Mixed batches silently skip auditing of regular entities (High)

`Data/AuditDbContext.cs:165-217`

`SaveChangesAsync`/`SaveChanges` set `BypassAuditInterceptor = true` for the *entire* save if **any** audit entity is tracked. A consumer context derived from `AuditDbContext` (the documented inheritance pattern) that saves a business entity in the same batch as, e.g., an `AuditSecurityEventEntity` gets the whole save bypassed — the business change is never audited. The interceptor itself documents this as wrong (`ShouldBypass`: "Do NOT bypass here — that would skip auditing for regular entities in mixed batches"), but the context-level flag does exactly that before the interceptor sees the batch.

**Fix:** Drop the blanket bypass; the interceptor already excludes audit entity types per-entry. Bypass should only be set by internal writers that know the batch is audit-only.

### 2. Disconnected updates emit zero audit records (High)

`Interceptors/AuditSaveChangesInterceptor.cs:571-593`

For an entity attached via `DbSet.Update()` (the standard disconnected web pattern), EF seeds `OriginalValue` from `CurrentValue`, so every modified property fails the `AreValuesEqual` filter, `changes.Count == 0`, and `BuildEnvelope` returns null. The update persists with **no** audit trail and no warning. This also affects `Repository<T>.DeleteAsync` soft-deletes when the entity wasn't loaded tracked.

**Fix:** When an entry has no per-property diffs but is genuinely `Modified`, fall back to a snapshot envelope (like the Added/Deleted path), or at minimum count and log the dropped entry via `IAuditDiagnostics`.

### 3. FERPA AuditOnly mode crashes the consumer's save on bare consumer contexts (High)

`Interceptors/AuditSaveChangesInterceptor.cs:388-423` (call sites 320–331, 362–369; invoked outside the try/catch at line 188)

`AddComplianceSecurityEvent` does `context.Set<AuditSecurityEventEntity>().Add(...)` on the *consumer's* DbContext. Bare consumer contexts (supported configuration, see `BareConsumerDbContextTests`) do not have that entity in their model, so `Set<T>().Add` throws `InvalidOperationException`. Because `EnforceConsentRequirements` runs before the try/catch (by design, so `ComplianceViolationException` propagates), the exception escapes and fails the application's SaveChanges — in **AuditOnly** mode, whose documented contract is "operation allowed." Existing tests only cover this path with an `AuditDbContext`-derived context.

**Fix:** Check `context.Model.FindEntityType(typeof(AuditSecurityEventEntity)) is not null` and fall back to the scoped audit services/sink (or logging) when absent; ensure the add can never fail the consumer save in AuditOnly mode.

### 4. Outbox/audit publish is not atomic with the business save (High)

`Interceptors/AuditSaveChangesInterceptor.cs:482-498` with `Services/Sinks/AuditOutboxWriter.cs:90-135`

The sink publish runs inside `SavingChangesAsync`, i.e. before EF begins the save's transaction. `AuditOutboxWriter.WriteChunkAsync` runs `ExecuteSqlRawAsync` on the consumer's connection, which autocommits unless the consumer happens to have an explicit transaction open. The documented guarantee — "inserted into the consumer's transaction so it commits atomically with the business write" — does not hold in the default case: if `base.SaveChangesAsync` then fails, the outbox row (or in Immediate mode the fully persisted audit rows) survives, recording changes that never happened. On application-level retry, `BuildEnvelope` mints fresh `EnvelopeId`s — violating `AuditEnvelope`'s own contract ("Producers must preserve EnvelopeId across retries") — so the idempotency key changes and duplicates insert.

**Fix:** Ensure an ambient transaction exists before the outbox write (begin one in the interceptor when none exists; commit in `SavedChangesAsync`, roll back in `SaveChangesFailedAsync`), or derive `EnvelopeId` deterministically from entry identity + change content so retries dedupe.

### 5. `CleanupOldArchiveRecordsAsync` raw SQL targets the wrong schema on SQL Server (High)

`Repositories/ArchiveRecordRepository.cs:251-265`

The table is mapped to `[audit].[ArchiveRecord]`, but the delete is `DELETE FROM "ArchiveRecord"` with no schema qualifier — on SQL Server this resolves against the user's default schema and throws "Invalid object name." Retention cleanup is broken on the production provider; it only works on SQLite (the test provider). Secondary: on SQLite the `{cutoffDate}` parameter binds in a text format that doesn't match the stored `"O"` format, making the comparison only date-granular.

**Fix:** Replace with `DbSet.Where(...).ExecuteDeleteAsync` (the enum/int conversion translates in current EF), or qualify with the configured schema.

### 6. `SaveChanges(bool)` overloads bypass concurrency-token prep and bypass detection (Medium)

`Data/AuditDbContext.cs:165-217`

Only the parameterless/CT overloads are overridden; in EF Core those delegate *to* `SaveChanges(bool)` / `SaveChangesAsync(bool, ct)`, not the reverse. Callers using the `acceptAllChangesOnSuccess` overloads skip `PrepareConcurrencyTokens` (so on non-SQL-Server providers `RowVersion` never rotates — optimistic concurrency silently inert) and skip audit-entity bypass detection. Compounding: `AuditOutboxEntity` is not in the interceptor's `_auditEntityTypes` and not `[NoAudit]`, so through this path outbox rows would themselves be audited.

**Fix:** Override the `bool` overloads and funnel the simple overloads into them; add `AuditOutboxEntity`/`AuditIntegrityWorkItemEntity` to `_auditEntityTypes` or mark them `[NoAudit]`.

### 7. Sync `SaveChanges` on intercepted contexts performs no auditing, silently (Medium)

`Interceptors/AuditSaveChangesInterceptor.cs:164-166`

The sync `SavingChanges` override is deliberately omitted; the comment claims "letting it fall through to base makes the gap obvious," but nothing surfaces it. A consumer calling sync `SaveChanges()` persists all changes with zero audit records — `FailClosedAlways` does not fail closed for sync saves.

**Fix:** Override sync `SavingChanges` to throw `NotSupportedException` (fail loud), or at minimum increment a diagnostics counter and log an error.

### 8. Provider-dispatch re-entrancy guard leaks stale `PendingProviderDispatches` (Medium)

`Interceptors/AuditSaveChangesInterceptor.cs:771-801`

If a provider triggers a nested save on the same context, the nested `SavedChangesAsync` hits the `IsDispatchingProviders` guard and returns before clearing `PendingProviderDispatches`. The nested batch's dispatches stay parked on the context and fire on the next unrelated save — stale, late provider dispatch.

**Fix:** Clear (or queue-and-drain) pending dispatches even when the re-entrancy guard trips.

### 9. `EncryptedValueConverter` equality queries silently return nothing (Medium)

`Conversion/EncryptedValueConverter.cs:18-47`, `Extensions/ModelBuilderEncryptionExtensions.cs`

AES-GCM with a random nonce is non-deterministic, and EF applies value converters to query parameters: `Where(e => e.Ssn == value)` compares against a freshly encrypted ciphertext that can never equal the stored one — zero rows, no error.

**Fix:** Document/enforce that encrypted properties are not queryable (e.g., a model validation pass that flags predicates on them), or add a deterministic HMAC shadow column for equality lookups.

### 10. Low-severity items

- Interceptor `Truncate` slices on raw char index (`AuditSaveChangesInterceptor.cs:869-873`), splitting surrogate pairs and producing invalid JSON in `DetailsJson`/`AdditionalData`; reuse `TruncateSafe`.
- `GetPrimaryKeyValue` drops non-Guid keys (`AuditSaveChangesInterceptor.cs:817-828`): entities with int/long/composite keys get `EntityId = null`, so `Modified` audit rows can't be tied back to a record. Serialize key value(s) as string on the envelope.
- `Repository.GetPagedAsync` permits Skip/Take with no ordering (`Repositories/Repository.cs:329-366`) — nondeterministic pages; require orderBy or apply a PK tiebreak.
- SQLite `DateTimeOffset`-as-`"O"`-TEXT ordering assumes uniform offsets (`Data/AuditDbContext.cs:536-552`); consumer-supplied non-UTC offsets break ORDER BY. Also pass `CultureInfo.InvariantCulture` to the parse.
- `AuditSqlCommandInterceptor.CommandFailed` labels all failures `operation=reader` (`Interceptors/AuditSqlCommandInterceptor.cs:151-166`); use `eventData.ExecuteMethod`.

## Implementation Outline

1. Fix the three silent-drop paths (#1, #2, #7) together; add tests covering mixed batches, `Update()` on detached entities, and sync saves on consumer contexts.
2. Fix #3 with a bare-consumer-context FERPA AuditOnly test.
3. Decide the atomicity strategy for #4 (interceptor-owned transaction vs deterministic `EnvelopeId`) — deterministic IDs also help the entity-batch idempotency issue in `AuditWritePipelineDurability.md`.
4. Replace the raw SQL in #5 with `ExecuteDeleteAsync` and run the repository tests against SQL Server, not just SQLite.
5. Apply #6–#10.

## Non-Goals

- Supporting lazy-loading proxies.
- Auditing raw-SQL mutations (`ExecuteUpdate`/`ExecuteDelete` by consumers) — out of scope for the interceptor model.
