# Sequence aggregate: isolated SQL Server verification — 2026-10-16

**Result: 22 passed, 0 failed, 0 skipped.** The actual production
`AuditIntegrityRepository.IsSequenceContiguousAsync` was executed against the isolated SQL
instance at localhost:1435 (server version 15.00.2000), in a nonce-owned database removed by
fixture teardown. No application rows or application SQL settings were changed.

## Coverage and measurements

Ten correctness cases run with READ_COMMITTED_SNAPSHOT both OFF and ON: empty, singleton,
non-1-based contiguous, negative contiguous, interior gap, duplicate, duplicate masking a
gap, Int64 minimum/maximum adjacency, and full-range overflow protection. Two volume cases
seed 217,719 records, verify validity, remove an interior record, and verify invalidity.
Every repository invocation issued one aggregate SELECT with COUNT_BIG, COUNT_BIG DISTINCT,
MIN and MAX, and left the change tracker empty. The SQL groups by a constant: zero or one
summary row is returned, rather than the sequence list.

| RCSI | 217,719 contiguous rows | After interior deletion | Logical reads on identical-SQL instrumentation replay |
|------|-------------------------|-------------------------|------------------------------------------------------|
| OFF | 112.8 ms | 67.8 ms | 597 for each probe |
| ON | 119.6 ms | 85.1 ms | 980 for each probe |

Elapsed values cover the repository call. Logical reads and returned-row counts were
collected by a separate replay of its exact generated SQL, consuming all result sets with
STATISTICS IO enabled; they are not mislabeled as measurements from the original EF reader.
Each replay returned one summary row for these nonempty datasets. The repository command
count excludes fixture setup and the instrumentation replay.

The fixture intentionally contains only SequenceNumber plus a nonunique index, permitting
invalid duplicate sequences. It proves provider translation and correctness, not full-schema
performance or consistent reads during concurrent append/retention. RCSI ON/OFF tests are
sequential tests, not concurrency acceptance. Existing nullable warnings appeared during
compilation; no compilation errors occurred. Two earlier development runs also passed 22
cases; the retained TRX is the final run with read-statistics instrumentation.

## Reproduce and inspect

[manifest.json](manifest.json) retains the exact command, SDK, source HEAD/dirty state,
relevant source and assembly SHA-256 values, discovered test names and outcomes.
[sequence-sqlserver.trx](sequence-sqlserver.trx) retains generated SQL, durations and reads.
The test is opt-in through MILLWORKS_SQL_TESTS_ENABLED=1; the shared wrapper's --sql supplies
that variable. Start/check the isolated service through MillWorks/scripts/sql-test-instance.py
under its shared development gate first. This fixture never selects application port 1433.
The compiled code comes from a dirty source checkout; this is not immutable package acceptance.

## Read-only application observation

[application-cache-observation.json](application-cache-observation.json) records a read-only
metadata/plan-cache check at 2026-10-16T19:47:31Z. The local MillWorks database reported zero
integrity rows, RCSI enabled, and no matching cached aggregate statement. Missing cache
entries do not prove that the query never executed. The API's on-disk dependency manifest
still named AuditCore EntityFramework/Services 1.15.4-dev.3; this was not a loaded-DLL check.
[observe-application-cache.py](observe-application-cache.py) retains the credential-free probe
source; configuration credentials were read locally and not written into evidence.

No host scan was triggered, no package was deployed, no API was rebuilt/restarted, and no
application data was seeded to recreate the historical workload. Consequently the original
Rider warning and three scheduled-scan acceptance remain unverified. The planned concurrent
append/retention consistent-view tests and source/package/runtime reconciliation also remain
open in the durability plan.
