# Local release dev.12 — 2026-10-18

All five AuditCore packages and their symbols at **1.15.4-dev.12** were published to both
local feeds and hash-verified. Updated 21 active MillWorks project files;
API restore resolves all five packages with SHA-512 hashes matching the delivered archives.
BackgroundJobs versions were preserved. API compilation/restart was not performed.

## Validation

- Frozen isolated Release snapshot: **2770 passed, 1 failed,
  23 expected skips**. **The full suite is not green**: its one failure is the existing
  10,000-row integrity SQL command timeout described below. [Final TRX](release-dev12.trx), [command](final-test-command.json).
- All **24 recovery regression cases passed**: eight cases on SQLite and SQL Server with
  RCSI OFF/ON. [Case results](recovery-regressions.json).
- Package-only consumer compiled, loaded all five dev.12 assemblies, and checked the new
  `AuditOutboxOperationConflictException.OperationId` and existing cleanup contracts.
  The consumer opened no database. [Verification](verification.json).
- Packages, DLLs, symbols, internal dependency versions, migration identifiers and baseline
  commit metadata were verified. [Package manifest](package-manifest.json).
- Existing 3,213 feed artifacts were preserved; ten fresh artifacts were copied to each feed
  with exclusive creation. [Delivery receipts](local-feed-delivery.json).
- [Source manifest](source-manifest.json) fingerprints 568 build inputs over baseline
  `3257d6b7ef6f029b797b464fc7e4c6f0a5e9ef0f`. Build/test/pack used the isolated
  `/tmp/auditcore-dev12-release` checkout. Shared build inputs were checked again before delivery.
- [Host resolution](host-resolution.json) and [restore log](api-restore.log) retain consumer proof.

## Corrections during validation

The first full run had 2,769 passes, two failures and 23 skips. A retry-exhaustion test canceled
its worker after a fixed 800 ms while SQL work remained in flight. Focused checks revealed the
same timing assumption in backoff and DLQ tests. These three tests now observe committed
terminal/retry/handoff outcomes, bound their waits and always stop their worker; the backoff
check exercises the production claim guard directly. All seven drainer scenarios passed in
the focused SQL run. Prior attempts are retained alongside the final result.

The unchanged 10,000-row integrity test both passed and timed out in early runs. Its source,
repository query and service are byte-identical to dev.11. The final run also hit that timeout. No query, timeout, assertion or
coverage was relaxed; the failure remains visible in the final TRX and verification JSON.
Publication required all 24 recovery regressions, all seven drainer cases and zero unexpected
failures, plus byte identity with dev.11 for the failing test/query/service.
[Known issue and release decision](known-issue.json). This intermittent timeout remains relevant
to the open H5 load/resource qualification gate; publication does not close it or establish a fully passing release suite.

An initial isolated restore mixed `/tmp` with `/private/tmp` and omitted project dependencies;
using the resolved project path restored the full graph. No API/library behavior was changed
for that path issue. An unstarted queued wrapper was removed before using the user's explicit
release-run authorization. User-managed processes were not signaled or restarted.

## Included behavior and next host step

The library rechecks recovery authorization after evidence persistence and before commit,
and before returning retained receipts, rejected transitions or inspection summaries. It
adds the typed operation-conflict exception. Verified uncertain-commit reconciliation retains
its original semantics. This release retains the dev.11 JSON reader fixes and H4 seam.
No schema migration or queue-format change was introduced.

MillWorks source already contains independent standalone producer transactions, fresh-context
retries, post-save authorization and HTTP 409/503 conflict/uncertain-commit mappings. Those
API-side sources and regressions await Jesse's rebuild with dev.12. The [source review](../rollout-source-review-2026-10-18/README.md)
records the fixes; historical dev.11 host acceptance does not verify them. After rebuilding
the solution/tests, run focused acceptance with `--no-build --no-restore`.

Current host activation, enabled Outbox/drainer workflows, copied-data acceptance, sequence
qualification and H5 remain open. This local release does not claim runtime activation.
