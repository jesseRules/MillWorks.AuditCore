# Sequence consistent-read protection — 2026-10-16

**Final verification: 102 passed, zero failed/skipped.** [TRX](sequence-consistency.trx),
[build/test log](build-and-test.log), and [manifest](manifest.json) retain the exact command,
SDK, source/assembly hashes, dirty source HEAD, test names, outcomes, and timing.
The suite consists of 47 SQL Server, 25 repository, four SQLite, and 26 service tests.
This run replaces no historical evidence and is not an immutable package qualification.

## Implemented behavior

SQL Server validation clones a dedicated SqlConnection and creates a fresh AuditDbContext
per attempt, preserving the configured model/schema and authentication state. It suppresses
ambient transactions, disables EF's nested retry strategy, and reads the aggregate in a
short SERIALIZABLE transaction. It does not save caller changes or widen the transaction
around the hash/chain scan. Non-SQL Server aggregate behavior is unchanged.

Each attempt issues one aggregate SELECT plus SET LOCK_TIMEOUT and transaction controls.
Query command timeout is two seconds; lock timeout is 1.5 seconds. SQL error 1205, 1222, or
-2 permits at most two retries with fresh contexts/connections and 100ms backoff. A successful
invalid summary returns false immediately. Incomplete reads throw; service tests verify that
timeout/cancellation does not produce a sequence-corruption finding.

The overall seven-second budget cancels work at 6.75 seconds, reserving 250ms for cleanup.
The final deadline test completed cleanup in **6,761 ms**. This is cooperative cancellation,
not a hard real-time guarantee during arbitrary process/network stalls. Caller cancellation
is propagated without retry, and tested connections close on cancellation/exhaustion.

## SQL concurrency proof

Tests use only localhost:1435 and a nonce-owned fixture database removed by teardown. Server
version is 15.00.2000. The fixture contains SequenceNumber and a nonunique index, deliberately
allowing duplicate corruption cases. It does not certify the production schema or host load.
READ_COMMITTED_SNAPSHOT is varied only on that disposable database, never application SQL.

Writer-first cases leave an append or deletion uncommitted, launch the real repository, and
observe SQL lock waits before committing/rolling back. Cases cover committed/rolled-back
append, contiguous-prefix retention, committed interior removal, and rolled-back interior
removal, under RCSI OFF and ON. Readers never consume uncommitted intermediate state.

Reader-first cases pause after the aggregate command returns while its transaction still
holds locks. Appends, prefix retention, and interior deletion are observed waiting in SQL
before releasing the reader. SQL DMVs confirm the reader's actual session isolation level 4
(SERIALIZABLE), in addition to checking the command's transaction. Subsequent validation sees
the committed mutation: append/prefix remains valid; an interior gap is invalid. These SQL
mutations model the storage effect of retention, not its separate authorization/hold protocol.

| Original-volume overlap | RCSI OFF | RCSI ON |
|-------------------------|----------|---------|
| Starting integrity rows | 217,719 | 217,719 |
| Protected scan including observed overlap | 128.0 ms | 129.3 ms |
| Concurrent writer including observed wait and commit | 14.3 ms | 14.6 ms |

The writer is intentionally started after the aggregate has executed to prove locks remain
held until completion; these timings are controlled overlap measurements, not sustained-load
or full-duration production append benchmarks. Both fixture scans met the two-second gate.
Sequential volume tests also retain generated SQL and STATISTICS IO from exact-SQL replays
inside SERIALIZABLE transactions, separately labeled from repository timings.

## Other coverage and limits

- A caller READ UNCOMMITTED transaction and a retry-enabled provider cannot weaken or wrap
  the dedicated read. Ambient scope suppression/restoration is tested independently.
- Custom schema/model mapping and authentication from an already-open connection are covered.
  Non-transient SQL contract errors are not retried.
- Real lock-timeout failures exercise two retries, success on the third attempt, and exhaustion.
  Recorded client connection IDs and DbContext IDs prove fresh attempts. Success after two
  timeouts took 3,315.9 ms; persistent lock timeout exhausted in 4,825.1 ms.
- Explicit cancellation during an observed lock wait makes only one attempt and releases the
  connection. Invalid committed summaries are not retried into apparent validity.
- Compiler nullable warnings were pre-existing; final build had no errors. Earlier development
  runs caught NUnit async-assertion mistakes, password loss caused by rebuilding a redacted
  cloned connection string, and a test probe incorrectly observing deferred reader failures
  through command-failure interception. Those were corrected before this retained passing run.
- SQL deadlock and command-timeout error codes use the same bounded retry handler but were not
  separately induced. The fixture is not a general workload, outage, or deployment qualification.

No packages were repacked, no API was built/restarted, no application data was changed, and
no application SQL snapshot settings or scan schedule were changed. Full-schema/live query
capture, reviewed-source package delivery, and three scheduled host scans remain separate
acceptance gates in the durability plan. Any new package must use an unused identity rather
than overwrite a distributed dev.5 package.
