# H2 batch acceptance and failure contract — 2026-10-16

**Status:** Implemented and locally verified. Not packaged or deployed; published dev.7
packages remain unchanged. A future publication must use a fresh unused version beyond dev.7.
No public signatures, transaction boundaries or schema changed.

[Manifest](manifest.json) retains exact commands, SDK, baseline commit, dirty source state,
assembly hashes, raw TRX counters and skip reasons. [Source fingerprints](source-manifest.json)
identify the tested source. **62 focused tests passed**, zero failures or skips. The full
Release regression run with disposable SQL enabled passed **2550 tests, zero failed**.
Its TRX records 32 NotExecuted cases; the console excludes nine explicit soak/profile
opt-outs from its total/skip count. See the retained logs and TRX for both runs.

## Behavior and proof

| Contract | Regression evidence |
|---|---|
| Null entries at start/middle/end reject the batch before persistence | ImmediateSinkTests and TransactionalOutboxSinkTests: neither immediate writer nor outbox writer is called. |
| Valid order and IDs remain intact | Mixed immediate writer inputs preserve relative order; serialized outbox envelopes and keys preserve input order/identity. Existing empty-batch tests still pass. |
| Mixed writers commit independently | BatchSinkAcceptanceSqliteTests: actual entity writer persists before returned event failure; actual entity failure does not prevent the real event writer from persisting. AuditWriteException reports only failed IDs. Retrying the failed event subset preserves the entity write. |
| Entity retries remain whole-attempt/fresh-scope operations | AuditEntityBatchWriterTests: existing replay/new and partial-race cases pass; exhausted uniqueness retries make three attempts, retain the prior replay and leave no fresh rows. Returned failed IDs are not proof of absence. |
| Explicit-event race fallback can partially commit | A seeded duplicate plus a competing commit forces the real AuditLogger through both duplicate catches into per-event writes. The test observes a successful earlier commit and then a SQLite-triggered write failure or cancellation, propagated without complete failure-ID feedback. |
| Exceptions/cancellation can interrupt mixed work | Entity write remains committed when the next writer throws; the original exception/cancellation propagates. |
| Outbox acceptance precedes transaction completion | OutboxAtomicityPolicyTests: successful batch publication stages rows before consumer SaveChanges; a subsequent save inside the transaction and rollback removes both business and outbox rows. |

The public sink/logger XML documentation, exception correlation documentation, sink comments,
and README now agree. The README includes a retry-candidate example that retains original
envelopes and explains incomplete feedback on interruption. It also corrects the old claim
that immediate audit writes cannot remain after business rollback.

An initial compile found missing required Action values in new test inputs; corrected before
execution. The first executed focused run found two assertions incorrectly relying on EF's
change-tracker enumeration order; batch-attempt membership now uses set comparison while
single-event fallback order and sink input order remain explicitly checked. Final retained
runs passed; earlier attempts are not counted as acceptance.

H3a identity/content-conflict semantics and H3 precise duplicate reporting remain separate
work. H1 host rollout and sequence runtime acceptance remain open.
