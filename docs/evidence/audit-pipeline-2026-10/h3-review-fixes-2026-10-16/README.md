# H3a/H3 review fixes — 2026-10-16

**Status:** Both review findings fixed. Fourteen targeted SQL cases and 322 wider focused tests
pass, as does the final broad regression (2,443 passed, zero failed). No package, migration, commit, host producer
change, API build/restart or deployment was performed. Existing H2/H3a/H3 work is preserved.

## Findings and resolutions

1. **P1 — false unverified replay after a concurrent commit.** The ledger read receipts before
   source rows. A competing atomic commit between those reads left a stale receipt miss and a
   visible source. The ledger now rechecks only that ambiguous set, in chunks of at most 500,
   before classifying historical data. A matching receipt proves replay, changed content is a
   conflict, and a source still lacking a receipt remains unverified. No receipt is fabricated,
   no new locking/isolation policy is introduced, and normal new/replay cases need no extra read.
2. **P2 — lost new-event status after a late conflict.** Per-event fallback could commit C and
   then throw for D; the writer retried C and relabeled it Duplicate. Fallback now returns a
   complete result containing confirmed Persisted/Duplicate observations and non-retryable
   typed Failed outcomes for conflicts. It continues unresolved inputs. The writer maps typed
   failures to conflict outcomes and preserves matching aliases. Resilient logging returns
   that complete result without retry/DLQ conversion and marks its tracing outcome as failure.

## Results and reproduction

- New SQL regressions **before fixes: 10 failed, four passed, zero skipped**. The four passing
  cases verify genuine historical rows still lack comparison evidence. [Output](before.log),
  [TRX](before.trx).
- Same regressions **after fixes: 14 passed, zero failed or skipped**. Tests cover competing
  commits between receipt/source reads for immediate and mapped outbox paths, matching and
  changed content, genuine missing evidence, and a late fallback conflict with prior commits,
  a matching alias, a later valid event and subsequent safe replay. Both RCSI modes run.
  [Output](after.log), [TRX](after.trx).
- Wider focused suite: **322 passed, zero failed, 19 expected provider skips**. It includes
  the existing identity/writer/processor/resilience tests and SQL/SQLite ownership cases.
  The new resilience test verifies that complete mixed-conflict metadata is preserved with no
  retry or DLQ write. [Output](focused.log), [TRX](focused.trx).

- Final broad Release regression: **2,443 passed, zero failed**, 232 console skips. TRX
  contains 233 NotExecuted entries including the explicit endurance opt-out. SQL fixtures are
  disabled in this broad run; their relevant cases passed in the focused SQL-enabled run.
  [Output](regression.log), [TRX](regression.trx).

Exact commands from the AuditCore root follow. The first command was run twice, before and
then after production fixes; replace `before` with `after` in its logger/results names for the
second run. The shared wrapper supplies no-build/no-restore automatically on the final broad
run, using the artifacts compiled by the focused command. Only disposable SQL on localhost:1435
is used by SQL-enabled tests; application SQL on 1433 is outside this work.

```sh
python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build --sql -- --configuration Release --no-restore --filter 'FullyQualifiedName~CommitBetweenReceiptAndSourceReadsUsesTheWinningEvidence|FullyQualifiedName~ExistingSourceWithoutReceiptRemainsUnverified|FullyQualifiedName~LateConflictPreservesEarlierFallbackCommitsAndAliases' --logger 'trx;LogFileName=review-before-fixes.trx' --results-directory /tmp/auditcore-review-before-fixes

python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build --sql -- --configuration Release --no-restore --filter 'FullyQualifiedName~EnvelopeIdentity|FullyQualifiedName~AuditEventBatchWriterTests|FullyQualifiedName~AuditBatchProcessorTests|FullyQualifiedName~BatchSinkAcceptanceSqliteTests|FullyQualifiedName~IdempotencyTests|FullyQualifiedName~ResilientAuditLoggerTests|FullyQualifiedName~OutboxOwnershipTests' --logger 'trx;LogFileName=review-focused.trx' --results-directory /tmp/auditcore-review-focused

python3 ../MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj -- --configuration Release --filter 'FullyQualifiedName!~MillWorks.AuditCore.Tests.Integration.SqlServer&FullyQualifiedName!~MillWorks.AuditCore.Tests.Integration.Garnet' --logger 'trx;LogFileName=review-regression.trx' --results-directory /tmp/auditcore-review-regression
```

[Source fingerprints](source-fingerprints.json) identify the uncommitted working tree, including
prior H2/H3a/H3 changes. Earlier [H3 verification](../h3-outcomes-2026-10-16/README.md) remains
historical evidence; its passing tests did not exercise these interleavings. Implementation
contracts: [H3a](../../../plans/BatchPublishingH3aImplementation.md),
[H3](../../../plans/BatchPublishingH3Implementation.md). Host rollout gates, H4/H5 and G remain
separate. A future release must use an unused version; published dev.7 is immutable.
