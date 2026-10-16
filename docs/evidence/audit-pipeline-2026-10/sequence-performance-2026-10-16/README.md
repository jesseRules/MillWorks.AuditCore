# Sequence aggregate performance investigation — 2026-10-16

**Decision: retain the current protected production query and locking behavior.** The normal
unique-index/pooled fixture has a protected median of 33.3–34.0 ms for 217,719 rows. Direct
comparison with the old unprotected EF query measured 30.7–31.4 ms: about 2.7 ms / 9% overhead.
The earlier isolated 10–49% comparisons were separate single runs with nonunique fixture
indexes and pooling disabled; they do not establish production slowdown.

## Controlled results

Eight explicit profile cases passed, zero failed/skipped. Each case warms five variants twice,
then records nine samples per variant while rotating execution order. No shared cache was
cleared. Medians below include connection open, command execution, transaction completion and
cleanup as applicable. Each probe verifies the same seeded count and distinct count. RCSI,
index and pooling changes affect only a nonce-owned database on localhost:1435, removed by
fixture teardown. The SQL test instance is version 15.00.2000; plans retain actual operators,
I/O and CPU. The narrow fixture is not a full production-schema or load qualification.

| RCSI | Pooling | Sequence index | Old EF (ms) | Protected EF (ms) | Same SQL/transaction via raw client (ms) | TABLOCK raw (ms) | Grouped-count raw (ms) |
|------|---------|----------------|-------------|-------------------|------------------------------------------|------------------|------------------------|
| OFF | OFF | Nonunique | 76.21 | 80.08 | 78.28 | 79.56 | 62.37 |
| ON | OFF | Nonunique | 80.71 | 81.45 | 81.05 | 79.24 | 65.64 |
| OFF | ON | Nonunique | 70.38 | 74.31 | 70.72 | 70.06 | 55.09 |
| ON | ON | Nonunique | 69.68 | 71.09 | 70.90 | 71.19 | 54.94 |
| OFF | OFF | Unique | 42.45 | 43.94 | 43.56 | 43.74 | 60.48 |
| ON | OFF | Unique | 42.96 | 43.63 | 43.09 | 42.29 | 53.06 |
| OFF | ON | Unique | 31.38 | 34.04 | 33.16 | 33.13 | 44.38 |
| ON | ON | Unique | 30.66 | 33.32 | 32.49 | 31.30 | 41.03 |

### What the plans explain

With the unique SequenceNumber index, SQL Server simplifies distinct counting and uses one
stream aggregate above the ordered index scan. Without uniqueness it uses two aggregate
stages. The production query still contains COUNT_BIG DISTINCT, so it continues to detect
bad duplicate data when the constraint is absent; no integrity shortcut was introduced.

Pooling avoids about 10 ms of repeated physical-connection overhead in the unique-index
fixture. The comparison between protected EF and the same protected SQL through a raw client
shows roughly one millisecond of EF/context overhead, not the main cost. Do not replace the
repository/model/schema integration with hand-written SQL for that small local difference.

The alternative grouped-count SQL helps the nonunique fixture but is slower under the normal
unique index. The broader [TABLOCK hint](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table)
provides too little benefit to justify changing lock granularity. Neither candidate was added
to production. SERIALIZABLE protection, retries, cancellation, duplicate detection, and the
single aggregate SELECT remain intact. This benchmark adds no database-wide setting change.

The old unprotected EF path exists only in the explicit diagnostic test with no concurrent
writers. It must not be used as a production fast path. These sequential performance cases do
not replace the previous lock-overlap/concurrency correctness evidence.

## Read-only application inventory

[Application inventory](application-index-inventory.json) records an enabled, unfiltered,
unique IX_AuditIntegrity_SequenceNumber on the local MillWorks database. The inspected
configured connection omits Pooling, which uses SqlClient's true default. The database currently
has zero integrity rows, so this does not measure its full-schema scan time or confirm the
running API's loaded configuration/assemblies. No index was created/rebuilt on the application
database and no application data or settings were changed. The credential-free
[inventory probe](observe-application-indexes.py) depends on the sibling observer/config loader;
credentials were not retained in this bundle.

## Reproduce and next steps

[Manifest](manifest.json) contains exact command, all samples, medians/ranges, hashes, source
HEAD/dirty state, discovered test names and outcomes. [TRX](profile.trx),
[build log](build-and-profile.log), and [actual plans](plans/) retain raw evidence. The
ProfileSequenceSummary method is Explicit and categorized Performance; run it by name through
the shared gate after starting/checking isolated SQL. Ordinary regression filters should
exclude ProfileSequenceSummary when profiling is not intended. The profile restores the
fixture's nonunique index and closes its pools so wider selections retain corruption coverage.

Next, verify the populated deployment uses the enabled unique index, capture its actual plan
and reads, and measure with the real pooled connection/settings. Existing local configuration
already has those two prerequisites; there is no justified production configuration change
from this investigation. Further query/isolation changes need a demonstrated workload gain
and the same concurrency acceptance. No new API build/restart or package publication occurred.
