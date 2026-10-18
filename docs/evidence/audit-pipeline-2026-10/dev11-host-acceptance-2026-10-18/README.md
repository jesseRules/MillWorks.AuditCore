# Dev.11 rebuilt-host acceptance — 2026-10-18

Jesse rebuilt the MillWorks solution and started the API. All agent test commands used
`--no-build --no-restore`; no build, restart, configuration change or application migration
was performed by the agent during this acceptance run.

## Compiled verification

**227 passed, two failed, three skipped** across four focused batches. Raw TRX/logs and
[per-fixture results](test-results.json) are retained. This is not an all-green claim.

- API producer/recovery/model/HTTP batch: **124 passed, two failed**. All 11 producer-grant/
  standalone-outbox cases, 19 HTTP-boundary cases, six recovery-authorization cases, the
  guarded retention selector, five external-model cases (including CommentThreads), speech,
  DLP, governance, workbench, visitor and dataset checks pass.
- Bridge batch: **61 passed**, including portal retained provenance across JSON retries,
  dataset/breach mapping and 43 SQL tool approval-bridge cases.
- Retention/policy batch: **40 passed, three skipped**. Real disposable SQL Server cases
  verify exact holds and tenant escalation, plus bounded retention translation/execution.
  Three existing Identity/Finance/Payments atomicity probes require a separately provisioned,
  migrated owned test database. They were not redirected to application SQL.
- DecisionModels saved-run/sharing domain observation cases: **two passed**.

The two failures are `ChangedPublicationScopeInvalidatesAnAcquiredPermit("tenant"/"actor")`.
Each test successfully asserts the nested permit is revoked, but then incorrectly expects the
outer bridge call to succeed. `PublishBoundAsync` correctly rechecks its independently acquired
permit after the sink returns and rejects publication. The test assertions now expect that
outer authorization exception. The separately disposed nested-permit case still expects success.
**These source-only assertion corrections await Jesse's next rebuild and a focused rerun.**
No production authorization was relaxed and the old binaries were not rerun as verification
of the corrected assertions.

## Running-host evidence

[Artifact/health verification](host-artifacts-and-health.json) matches all five API output DLLs
to the published dev.11 archives. Process 97343 started at 16:33:21 Chicago time. Liveness is
HTTP 200 Healthy; readiness is HTTP 200 Degraded, as previously observed.

[Read-only SQL](host-migration-and-queue-audit.json) matches all 18 owning migrations with no
queued envelopes or handoffs. Current startup records `AuditOutboxDrainer disabled` because
`AuditSinkMode` is not TransactionalOutbox. This verifies Immediate-mode operation only;
empty queues do not establish active Outbox-drainer acceptance.

The [bounded HTTP probe](live-http.json) used the HTTPS frontend proxy on port 4200 and two
independent seeded development sessions, both logged out afterward. No domain entity was
created/deleted for this probe. Two CRM hub reads each returned HTTP 200 and intentionally
represent distinct observations, rather than retries of a single event.
[SQL reconciliation](live-audit-observation.json) verifies **two source events, two distinct
receipts, one tenant and matching source/receipt tenant values** for the selected company.
New EF receipts observed since this host start also have matching source rows. SQL observations
read metadata/counts only; raw audit payloads and authentication material are not retained.

Recovery boundary checks on a random absent failure: anonymous inspect/replay both **401**,
regular-user inspect **403**, authorized-admin inspect **404**. No synthetic failure/replay,
cleanup, quarantine disposition or legal hold was injected into application SQL.

Retry idempotency and rollback are demonstrated in the passing compiled SQLite acceptance
cases: repeated publication of the retained observation yields one source/receipt, and an
injected failed save leaves neither before retrying with a fresh context. The live two-read CRM
probe does not substitute for retrying the same observation. Legal-hold and revoked-membership
behavior are additionally covered by the compiled policy and disposable SQL fixtures.

## Remaining gates

1. Rebuild corrected breach test assertions with Jesse's next batch; rerun the two cases.
2. Remaining producer-specific live workflows and consumer contexts, enabled Outbox/drainer
   acceptance in each owning configuration/database, and externally copied payload lifecycle.
3. H5: agreed workload/budgets and sustained load/reconciliation evidence. H4 is complete.
