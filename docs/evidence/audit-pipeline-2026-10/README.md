# Audit pipeline evidence register

This repository-local bundle was assembled on 2026-10-16 from historical MillWorks records.
No new database probes, tests, API builds, package builds, or deployments were performed.
Source paths below document origin; reviewers do not need the sibling checkout to read the
included evidence. The bundle is selected evidence, not a full source snapshot of dev.3.

## Controlled sequence performance investigation — 2026-10-16

[Paired performance evidence](sequence-performance-2026-10-16/README.md) compares the old and
protected query, raw-client execution, lock hints, and grouped-count SQL across unique/nonunique
indexes, pooling, and RCSI. It corrects interpretation of the earlier separate-run timings;
production query and locking remain unchanged. Actual plans and the read-only application
index inventory are retained there.

## Fresh consistent-read implementation and verification — 2026-10-16

[Consistent-read protection](sequence-consistency-2026-10-16/README.md) records the subsequent
implementation and passing concurrency/regression run. It includes actual SQL lock-overlap
and isolation evidence, bounded retries/deadline cleanup, and source/assembly fingerprints.
This supersedes the earlier provider-only limitation for the tested isolated fixture; it does
not close package or live-host acceptance.

## Fresh sequence SQL Server evidence — 2026-10-16

[Isolated SQL Server verification](sequence-sqlserver-2026-10-16/README.md) adds a new retained
22-test run with exact command, source/assembly fingerprints, TRX, generated SQL and fixture
read statistics. It is separate from the historical counts below. The original introductory
statement describes the historical bundle assembly, not this later test/observation run.
Concurrent consistency and running-API acceptance remain open.

## Index verification

- [Before statistics](audit-before-statistics.txt) and [after statistics](audit-after-statistics.txt):
  same-envelope historical probe reported 55,571 → 3 logical reads and 1,749 → 28 ms query execution.
- [Actual plan](audit-after.sqlplan): index seek on `IX_AuditLogs_Envelope_Lookup`.
  The envelope GUID parameter was replaced with a zero GUID for repository publication.
  Plan-internal operator timing can differ from the surrounding SQL batch timing.
- [Migration SQL](audit-index-migration.sql): incremental index and migration history insert.
- [Individual test outcomes](audit-phase1-results.json): extracted TRX times, counters, and test names.
- [Post-rebuild record](post-rebuild-record.md): copied historical narrative, not raw DLL/probe output.
  Original raw observation lived under `/tmp/mw-sql-post-rebuild-20261011`; that raw observation
  is not included here, so runtime delivery remains supported by the narrative record only.

## Test reproducibility

| Historical count | Available evidence | Original command / limitation |
|------------------|--------------------|-------------------------------|
| 19, October 9 | Included TRX-derived results; all 19 passed, none skipped; classes are `AuditEnvelopeLookupIndexTests` and `AuditEntityBatchWriterTests` | Original CLI invocation not retained. Reconstructed selection below; not represented as the original command or a newly passing run. |
| 78, October 8 (29 repository, 39 service, 10 SQLite) | Historical plan narrative only | Exact `--filter`, build flags, result files, and exact source snapshot not recovered. Count cannot currently be reproduced or credited as fresh acceptance. |
| 164, October 16 (71 + 93) | Historical review narrative only | Exact commands, result files, and exact source snapshot not recovered. Recover them or replace with a new explicitly scoped run; do not guess a filter and call it historical evidence. |

Reconstructed command for the 19-test class selection, run from this repository root:

```sh
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --filter 'FullyQualifiedName~MillWorks.AuditCore.Tests.EntityFramework.AuditEnvelopeLookupIndexTests|FullyQualifiedName~MillWorks.AuditCore.Tests.Sinks.Writers.AuditEntityBatchWriterTests' --logger 'trx;LogFileName=audit-index-reproduction.trx' --results-directory /tmp/audit-index-reproduction -m:1 -p:UseSharedCompilation=false
```

A future run must record its actual command, SDK, source commit/dirty state, discovered test
names, pass/fail/skip counts, and retained result file. Today's test inventory may differ from
the historical one. Missing historical commands remain an explicit provenance gap.

## Source fingerprints

- `audit-before-statistics.txt`: derived from `/Users/jesse/RiderProjects/MillWorks/Plan/SharedSqlHostIsolation/Evidence/2026-10-09/phase1-before.txt`; original SHA-256 `978d5ebf7efb845e94d9aeea158d45fd0a122fd5056a0a1eaea144813d33b018`.
- `audit-after-statistics.txt`: derived from `/Users/jesse/RiderProjects/MillWorks/Plan/SharedSqlHostIsolation/Evidence/2026-10-09/phase1-after.txt`; original SHA-256 `617d54f74598871aeb97b53ee3af9f05555bf31ed13888da2eb3f62bdc5e4e5b`.
- `audit-after.sqlplan`: derived from `/Users/jesse/RiderProjects/MillWorks/Plan/SharedSqlHostIsolation/Evidence/2026-10-09/phase1-after.txt`; original SHA-256 `617d54f74598871aeb97b53ee3af9f05555bf31ed13888da2eb3f62bdc5e4e5b`.
- `audit-index-migration.sql`: derived from `/Users/jesse/RiderProjects/MillWorks/Plan/SharedSqlHostIsolation/Evidence/2026-10-09/audit-index-migration.sql`; original SHA-256 `9449d41461208d2abec6c03e708c4c49f70b91ec34dd5ae770a5880b8afb2b78`.
- `audit-phase1-results.json`: derived from `/Users/jesse/RiderProjects/MillWorks/Plan/SharedSqlHostIsolation/Evidence/2026-10-09/audit-phase1.trx`; original SHA-256 `7c25d738baea676462b1ca2a72fb9216c57676245773165a8b5deea7f89d78eb`.
- `post-rebuild-record.md`: derived from `/Users/jesse/RiderProjects/MillWorks/Plan/SharedSqlHostIsolation/SqlStallRecovery-2026-10-11.md`; original SHA-256 `1beb3d981e5ac7bb65224258e23bde8026bca0feedcc2b029e49c5b23256e84a`.
