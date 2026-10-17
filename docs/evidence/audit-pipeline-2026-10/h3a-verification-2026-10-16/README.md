# H3a local verification — 2026-10-16

**Status:** Library source and focused SQL Server verification complete. Jesse restored Docker;
the gated helper started the disposable SQL container on localhost:1435. No application
SQL/API action or package publication occurred. Host producer/authorization/data transition
gates remain open in the [implementation contract](../../../plans/BatchPublishingH3aImplementation.md).

## Results and reproduction

Run from the AuditCore root. The shared runner serializes heavy work. These commands do not
build MillWorks.Api or enable disposable SQL tests.

```sh
python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build -- --configuration Release --no-restore --filter 'FullyQualifiedName!~MillWorks.AuditCore.Tests.Integration.SqlServer&FullyQualifiedName!~MillWorks.AuditCore.Tests.Integration.Garnet' --logger 'trx;LogFileName=h3a-regression.trx' --results-directory /tmp/auditcore-h3a-regression

python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build -- --configuration Release --no-restore --filter 'FullyQualifiedName~EnvelopeIdentitySqliteTests|FullyQualifiedName~AuditEnvelopeContentTests|FullyQualifiedName~BatchSinkAcceptanceSqliteTests|FullyQualifiedName~AuditEntityBatchWriterTests|FullyQualifiedName~AuditEventBatchWriterTests' --logger 'trx;LogFileName=h3a-identity.trx' --results-directory /tmp/auditcore-h3a-identity
```

- Broad regression: **2,420 passed, zero failed**, 214 console skips. TRX has 215 NotExecuted
  records including one explicit endurance opt-out. Skips cover 114 ownership SQL cases,
  55 sequence SQL cases, 24 new identity SQL cases, 14 execution-strategy cases, one SQL
  migration case, two real SQL overlap cases, four provider limitations and one opt-in soak.
  Container SQL/Garnet namespaces were excluded by the filter. Skips are not SQL acceptance.
- Final identity/writer/SQLite run: **88 passed, zero failed, zero skipped**. This ran after
  adding the single-event race/tracker regression; the earlier broad run preceded the SQL-only savepoint fix described below. [Output](identity.log), [TRX](identity.trx).
- [Broad output](regression.log), [TRX](regression.trx), and
  [working-tree source fingerprints](source-fingerprints.json) are retained locally in this repo.
  Fingerprints include pre-existing uncommitted H2 work; no clean-commit provenance is claimed.
- Earlier attempts exposed missing test authorization/capability fixtures and an improperly
  wired background-service test. These were corrected before the retained passing runs.
  Earlier SQL-enabled ownership setup failed on connection refusal; no SQL result is claimed.

Coverage includes canonical JSON/number/Unicode rules; globally keyed replay/conflict in both
sink modes; actor/tenant/payload changes; aliases only succeeding after durable acceptance;
legacy/unverified evidence; retained receipts preventing recreation; entity partial property
repair; consumer rollback; lost SaveChanges acknowledgement; authorization before disclosure;
DLQ envelope round-trip; guarded drainer conflict retention; and same-context replay race cleanup.
The direct drain-cycle test exercises the real state store/processor/writer path without a
background poll or distributed leader lock.

## SQL Server verification completed

The disposable instance on **1435** was restored. The application instance on 1433 was not
used. Exact successful commands (the wrapper supplies no-build automatically when omitted):

```sh
python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build --sql -- --configuration Release --no-restore --filter 'FullyQualifiedName~EnvelopeIdentitySqlServerTests|FullyQualifiedName~OutboxOwnershipTests' --logger 'trx;LogFileName=h3a-sql.trx' --results-directory /tmp/auditcore-h3a-sql

python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build --sql -- --configuration Release --no-restore --filter 'FullyQualifiedName~EnvelopeIdentitySqlServerTests|FullyQualifiedName~EnvelopeIdentitySqliteTests|FullyQualifiedName~AuditEnvelopeContentTests|FullyQualifiedName~BatchSinkAcceptanceSqliteTests|FullyQualifiedName~AuditEntityBatchWriterTests|FullyQualifiedName~AuditEventBatchWriterTests' --logger 'trx;LogFileName=h3a-final.trx' --results-directory /tmp/auditcore-h3a-final
```

- Identity/ownership run: **178 passed, zero failed, 19 expected provider skips**.
  Of the passes, 26 are H3a SQL cases (13 each with RCSI off/on), 112 are SQL ownership
  cases and 40 are SQLite ownership cases. Seventeen SQLite provider skips and two SQL
  portable-timestamp skips account for all 19. [Output](sql.log), [TRX](sql.trx).
- Final combined identity run: **114 passed, zero failed, zero skipped**: 26 SQL cases and
  88 local identity/writer/SQLite regressions. [Output](final.log), [TRX](final.trx).
- First SQL run: 10 failed, 166 passed, 19 skipped. It exposed a raw-path savepoint name
  longer than SQL Server's 32-character limit. The fix uses a 32-character randomized name.
  [Original output](sql-before-savepoint-fix.log) and [TRX](sql-before-savepoint-fix.trx) are
  retained. An earlier runner invocation rejected a redundant explicit --no-build argument
  before executing tests; the commands above use the supported wrapper arguments.
- The strengthened final cross-chunk test catches a conflict and then **commits** the caller
  transaction, proving all chunks from the failed call roll back while an earlier publication
  in that transaction survives. This avoids relying on transaction disposal to prove savepoint
  behavior. The SQL entity test repairs a missing property, proves an independent cleanup
  delete times out on retained SERIALIZABLE locks, rejects changed original values, and blocks
  recreation after all source rows are removed. Both run with RCSI off and on.
- SQL fixtures also exercise actual immediate and mapped/raw outbox uniqueness races, consumer
  rollback, and incremental migration without historical comparison backfill. Test databases
  are disposable and removed by fixture teardown. The final source fingerprints include the
  savepoint fix and strengthened tests. The prior 2,420-pass broad run remains valid historical
  evidence; final targeted regressions cover the subsequent SQL-only production change.

Do not publish H2/H3a using already distributed dev.7. H3 exact mixed-batch classifications,
H4/H5, host producer updates, queued/persisted data inventory and deployment remain separate.
