# H3 outcomes verification — 2026-10-16

**Status:** H3 library implementation verified. No package, commit, MillWorks host source change,
consumer migration, API build/restart or deployment was performed. Existing uncommitted H2/H3a
work is preserved. [Implementation and operational contract](../../../plans/BatchPublishingH3Implementation.md).

## Retained verification

- Final broad Release regression: **2,442 passed, zero failed**, 218 console skips.
  TRX contains 219 NotExecuted entries, including one explicit endurance opt-out. Disposable SQL
  is disabled in this broad rerun; excluded container SQL/Garnet namespaces are not counted.
  [Output](regression.log), [TRX](regression.trx).
- Final SQL-enabled focused run: **155 passed, zero failed, zero skipped**. This includes
  28 real SQL identity cases (14 each with RCSI off/on), 39 SQLite identity/result tests,
  30 resilient-logger tests, 18 idempotency tests, 17 event-writer tests, 15 processor tests,
  seven SQLite batch-acceptance tests and one identity-related canonicalization test selected
  by the filter. The full canonicalization suite is covered by the broad regression.
  [Output](focused.log), [TRX](focused.trx).
- Earlier SQL-enabled broad run: **2,624 passed, two failed, 23 console skips**; TRX has
  32 NotExecuted entries including explicit opt-outs. Both failures were older security tests
  expecting incomplete writer results to become terminal failures. They now assert a contract
  defect, matching H3's required recoverable behavior, and pass in the final broad run.
  All 28 SQL identity cases passed in this earlier run as well. This is retained diagnostic
  evidence, not a claimed passing full SQL suite.
  [Output](sql-regression-before-test-updates.log), [TRX](sql-regression-before-test-updates.trx).
- A final empty-envelope correction, resilience validation test and success/exception
  contradiction guard are included in the final focused/broad runs. Existing compiler
  warnings remain; neither retained final run failed its build.

## Exact commands

Run from the AuditCore root. The shared runner gates builds and disposable SQL work; it does
not reach MillWorks.Api. Only localhost:1435 disposable SQL was used by SQL-enabled commands.

```sh
python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build --sql -- --configuration Release --no-restore --filter 'FullyQualifiedName!~MillWorks.AuditCore.Tests.Integration.SqlServer&FullyQualifiedName!~MillWorks.AuditCore.Tests.Integration.Garnet' --logger 'trx;LogFileName=h3-regression.trx' --results-directory /tmp/auditcore-h3-regression

python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build --sql -- --configuration Release --no-restore --filter 'FullyQualifiedName~AuditEventBatchWriterTests|FullyQualifiedName~AuditBatchProcessorTests|FullyQualifiedName~EnvelopeIdentity|FullyQualifiedName~BatchSinkAcceptanceSqliteTests|FullyQualifiedName~IdempotencyTests|FullyQualifiedName~ResilientAuditLoggerTests' --logger 'trx;LogFileName=h3-final.trx' --results-directory /tmp/auditcore-h3-final

python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build -- --configuration Release --no-restore --filter 'FullyQualifiedName!~MillWorks.AuditCore.Tests.Integration.SqlServer&FullyQualifiedName!~MillWorks.AuditCore.Tests.Integration.Garnet' --logger 'trx;LogFileName=h3-regression-final.trx' --results-directory /tmp/auditcore-h3-regression-final
```

## Proven behavior and boundaries

Real persistence tests verify new-only, replay-only and mixed batches; correlated matching
aliases; conflict/failure metrics; two-stage bulk-to-single concurrent recovery; partial commit
followed by failure/cancellation and safe replay; real SQL competing mixed batches; and exact
single-event duplicate reporting. Malformed logger, writer and processor outcomes are rejected
before result-derived transitions/counters. Durable writes remain intact; claims stay recoverable
without fabricated attempt increments or handoffs. A background-service test verifies escalation
at the configured contract-defect threshold across idle polling and lease recovery. Resilient
logging does not retry or dead-letter a malformed internal result.

[Source fingerprints](source-fingerprints.json) identify the final working tree, including
pre-existing H2/H3a source. No clean-commit or package provenance is claimed. H3 adds no schema
migration. H3a's producer/authorization/queue-version/receipt rollout gates remain open. H4/H5
and G are separate. Any subsequent package must use a fresh unused version, never distributed dev.7.
