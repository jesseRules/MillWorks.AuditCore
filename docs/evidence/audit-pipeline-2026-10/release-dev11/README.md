# Local release dev.11 — 2026-10-18

All five AuditCore packages and matching symbols at **1.15.4-dev.11** were published to
both local feeds and hash-verified. Existing feed artifacts were preserved. The isolated
package-only consumer builds and loads all five assemblies and sees `CleanupCompletedAsync`.

- SQL-enabled isolated Release build/regression: **2747 passed, 0 failed,
  23 expected skips**. [TRX](release-dev11-current.trx).
- Final isolated-drainer cases: **4 passed**, including cancellation. Earlier focused
  provider/cleanup verification: **182 passed**, 19 expected skips. CRM: **18 passed**.
- [Source snapshot](source-manifest.json) records baseline commit and all dirty/untracked
  build-input hashes. Concurrent edits in the shared checkout invalidated earlier release
  attempts; none of those attempts published. Final verification/packaging used the frozen
  `/tmp/auditcore-dev11-release` detached worktree; packages match its compiled DLL/PDB hashes.
- [Package manifest](package-manifest.json), [consumer verification](verification.json),
  and [both feed receipts](local-feed-delivery.json) retain delivery evidence.
- Updated 21 active MillWorks project files to dev.11 after publication.
  [API restore](api-restore.log) succeeded; [resolved package hashes](host-resolution.json)
  match all five published archives. BackgroundJobs versions were not changed.

Dev.11 supplies guarded completed-outbox cleanup and H4's internal state-store seam. It also
contains the reviewed source snapshot's concurrent shared JSON-reader fix. No schema/migration
change was introduced. Host producer, recovery/retention and CommentThreads source changes
are described in the [implementation evidence](../rollout-implementation-2026-10-18/README.md).

API compilation and API-dependent tests still require explicit authorization under MillWorks'
AGENTS.md. No API build/restart, application mutation or runtime acceptance occurred in this
release. The read-only application SQL observation matched 18 migrations and found no queued
rows/handoffs; active drainer/copied-data and H5 qualification gates remain open.

## User-rebuilt host follow-up

Jesse subsequently rebuilt/started the API. [Host acceptance](../dev11-host-acceptance-2026-10-18/README.md)
verifies exact deployed DLLs and records compiled/live checks plus remaining gates. Two
corrected test assertions await the next user rebuild. No agent build/restart occurred.
