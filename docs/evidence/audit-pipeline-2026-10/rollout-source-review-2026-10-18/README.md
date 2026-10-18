# Audit rollout source review — 2026-10-18

Reviewed the producer grants/bridges and retained provenance, standalone acceptance, operator
and retention authority, guarded cleanup, DI registrations and H4 orchestration seam across
AuditCore and MillWorks. The first pass changed source only while Jesse had other sessions running.
The follow-up review compiled AuditCore and ran focused SQLite ownership tests through the shared heavy-work gate.
No package publication/reference updates, migrations, application calls or user-managed
process changes were performed. Only our own unstarted SQL test wrapper was canceled after
waiting more than five minutes for the occupied heavy-work lane. Existing shared-checkout changes were preserved.
The prior dev.11 package/runtime evidence remains historical evidence for its tested snapshot.

## Findings fixed in source

### P1 — Standalone acceptance lacked an independent, guarded commit boundary

`MillWorks.Api/Infrastructure/Audit/AuditProducerAuthorization.cs` created a fresh context
but did not suppress an ambient `TransactionScope`. A supposedly standalone successful save
could therefore enlist in a caller transaction and later roll back with it (or require a
transaction promotion). It also checked its permit before `SaveChangesAsync` but not after
save interception/persistence; a tenant change during the save could escape the final check.

The standalone path now suppresses ambient transactions and owns an explicit transaction.
It rechecks authorization after persistence and before commit, and wraps the whole operation
in EF's execution strategy with a fresh context/consumer binding for each attempt. Caller
cancellation is passed to the strategy as well as individual operations, so retry delays do
not outlive a canceled request. Existing consumer-owned transaction handling is preserved.
The original observation/envelope identity is retained across provider retries.

Three API regression sources cover outer transaction rollback/context restoration, tenant
revocation after save with zero committed source/receipt rows, and a transient save fault
that must retry with a different context and retain one logical source/receipt.
Existing explicit failed-save/replay cases remain.

### P1 — Recovery authorization could expire while its evidence was being saved

`OutboxStateStore.Resolutions.cs` checked the permit before guarded state changes, then the
shared `ExecuteAsync` path persisted resolution/operation receipts and committed without
another authorization check. A revoked operator or replaced/disabled retention policy during
that await could still commit cleanup, replay or skip.

Protected operations now pass their permit into `ExecuteAsync`, which checks it again after
`SaveChangesAsync` and immediately before commit. Failure at this point rolls back source,
projection, resolution and operation-receipt changes together. Worker claim/apply/handoff
operations retain their existing behavior. The execution strategy also receives cancellation;
uncertain-commit verification still uses its independent bounded token.

A new provider regression arms revocation only after receipt persistence, then checks full
rollback for replay, skip, resolved cleanup and ordinary completed cleanup. These four cases
use the existing SQLite and SQL Server RCSI OFF/ON fixture configurations.

### P2 — Recovery HTTP responses obscured operation conflicts and uncertain commits

Reusing an operation identity with different input threw an undifferentiated
`InvalidOperationException`. A failed commit acknowledgement also escaped as a generic HTTP
500, hiding the distinction between an unverified result and a rejected transition.

The library now throws `AuditOutboxOperationConflictException` carrying the original
operation ID. The API maps that specific type to **409 / operation_identity_conflict**.
`AuditOutboxCommitUnknownException` maps to **503 / operation_commit_unknown**, also carrying
its operation ID. A caller must retain that ID when retrying uncertain outcomes; the API does
not report the transition as definitively rejected. Other storage failures are not mislabeled
as identity conflicts.

Controller regression sources cover both response contracts and preservation of unrelated
storage exceptions. The existing operation-reuse provider assertion now requires the typed
conflict. No schema change was needed.

### P2 — Recovery reads could outlive their last authorization check

The follow-up review found that both saved-operation receipt shortcuts bypassed the
mutation's permit checks. Authorization could expire during a receipt read or while waiting
for the operation lock, yet the retry would still return its retained result. Rejected
no-op transitions had the same gap after their eligibility queries. Inspection checked the
permit before, but not after, querying handoff counts.

Both receipt shortcuts and rejected no-op results now recheck the protected operation's
permit before returning. Inspection rechecks after its final query. Commit-acknowledgement
reconciliation still reports a verified committed result: revocation after commit must not
misrepresent a completed mutation as a rolled-back one. Worker operations have no recovery
permit and retain their existing receipt behavior.

New provider regressions revoke authorization during receipt reads for replay/skip, during
a rejected target read, and during handoff-count reads. The original committed operation
and resolution receipts remain intact when a retry is denied.

## Validation and next release order

The follow-up compiled AuditCore and its test project through the shared gate. The focused
ownership run passed **57 tests, zero failures**, including all eight new recovery cases on
SQLite. There were 165 skips: the two SQL fixtures were disabled in that first run, and
SQL-specific cases do not run on SQLite. The [TRX](review-ownership.trx) retains the result.
The SQL-enabled rerun never acquired the lane and was canceled before starting dotnet.
SQL Server RCSI OFF/ON verification remains pending; no SQL fixture was created by this review. API regression sources
remain pending Jesse's rebuild; previous green API binaries do not verify these fixes.
The [source manifest](source-manifest.json) records this review snapshot.

The new public exception is absent from published dev.11. The API source referencing it
requires a fresh AuditCore package before a solution rebuild; existing API references/feeds
remain untouched during this review. Once the other sessions clear:

1. Select a fresh version after dev.11, complete SQL and release validation of the updated AuditCore snapshot, preserve
   provider ownership/commit-fault coverage, and publish/verify its coordinated packages.
   Never overwrite dev.11 or pack its old binaries as these source fixes.
2. Update/restore the actual MillWorks package references, then batch API and test compilation
   with Jesse's rebuild. Include the two previously corrected breach assertions.
3. Run focused producer transaction/retry, provider recovery/cleanup and controller regressions
   with `--no-build --no-restore` after that rebuild; record real results.
4. Preserve the open Immediate-versus-Outbox/drainer, remaining workflow/consumer/copy and H5
   qualification gates. The running dev.11 host was not changed by this review.

## Dev.12 release follow-up

[Release evidence](../release-dev12/README.md) supersedes the pending library validation and
publication steps above: all 24 new provider cases passed on SQLite/SQL RCSI OFF/ON; all seven
SQL drainer cases passed after replacing three fixed-delay assertions with committed-outcome
checks. The broader result is 2,770 passed, one unchanged 10,000-row integrity SQL timeout and
23 skips. The full suite is not green, and H5 retains that known issue. All five dev.12 packages
and symbols are published to both local feeds; 21 active MillWorks project files were updated,
and API restore verifies their exact hashes. API compilation, API regressions and host runtime
acceptance remain pending Jesse's rebuild. No API build/restart occurred.
