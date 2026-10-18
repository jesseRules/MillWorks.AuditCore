# Producer, recovery/retention and H4 implementation — 2026-10-18

This supersedes the earlier inventory's source-gap status. It does not establish acceptance
of the new code in a running host. The existing user-managed process remains on dev.10.

## Implemented source

- Eight typed producer purposes now bind the exact envelope, tenant, actor, logical ID,
  occurrence time and canonical input. The global router retains the existing speech,
  dataset and breach grants. Exact-reference substitution, changed input and expired bindings
  are rejected. Standalone Outbox observations save source and identity receipt together in
  a fresh scope; this does not make earlier business writes atomic with the observation.
- CRM retains an observation DTO and awaits publication. Portal freezes trusted original
  provenance on that DTO, including across serialized retries. Governance no longer refreshes
  correlation from a later HTTP request. Workbench retains the original entity tenant.
  Training uses a trusted tenant scope; visitor dereferences create new distinct observations.
- Removed unused CRM section and Finance write methods, plus the unused arbitrary-envelope
  forwarding entry point. The 14 earlier construction sites reduce to 11 active sites. The refreshed inventory
  scans 32,896 production files: zero active sites lack explicit IDs or governance metadata.
- CommentThreads now composes the external outbox/identity model like the other ten contexts.
- Operator recovery requires current tenant, active persisted membership and Read+Admin;
  permissions are rechecked before mutation. Quarantine inspection requires a separate
  permission; quarantine mutation remains denied pending authoritative tenant disposition.
- Retention uses the approved policy revision, current legal holds and the guarded recovery
  facade. Unresolved failures are preserved. Ordinary completed rows require retained identity
  evidence, no failure/handoff history and expired verification windows. Completed replays use
  retained resolution history even after normal completion clears current pointers. Operation
  receipts protect retry and uncertain-commit outcomes. No migration/schema change was needed.
- H4: internal `IOutboxStateStore` is registered over the existing store; drainer, recovery and
  handoff dispatcher consume it. Provider scopes and ownership predicates remain unchanged.
  Queue-depth telemetry remains a separate read-only query with its own scope/coverage.

## Verification

- Focused library/provider run: **182 passed, zero failed, 19 expected provider skips**.
  SQLite and SQL Server RCSI OFF/ON cover guarded completed cleanup and retained evidence;
  isolated drainer cases cover original tokens, malformed outcomes and lost ownership.
- Broad SQL-enabled Release run: **2747 passed, zero failed, 23 expected skips**.
  A concurrent nullable-duration query fix required rebuilding/revalidation before packaging;
  final release results are retained with the dev.11 release evidence.
- CRM focused build/tests: **18 passed, zero failures/skips**, including an awaited original
  tenant-bound observation. API-dependent test source has been updated/extended but awaits
  an explicitly authorized API build; existing binaries cannot validate these source changes.
- [Read-only application SQL observation](host-migration-and-queue-audit.json): all **18**
  owning source migrations match applied history, with no queued rows or handoffs. Empty
  tables do not prove active drainer behavior. No application rows/migrations were changed.

## Remaining gates

[Dev.11 publication and API restore](../release-dev11/README.md) are complete. Next: explicitly
authorized API/test builds, then user-managed host restart
and real producer/recovery/hold/receipt checks. Verify each configured drainer and externally
copied payload lifecycle. H5 still requires agreed workload budgets and sustained qualification;
no throughput or end-to-end latency claim is made by this work.

Final H4 orchestration run: **4 passed, zero failures/skips**, including cancellation.
Final release validation used a frozen isolated checkout to prevent concurrent source edits
from invalidating the tested package inputs. API-dependent source verification remains open.

## Rebuilt-host follow-up

The [October 18 no-build acceptance](../dev11-host-acceptance-2026-10-18/README.md) supersedes
API-compilation-pending statements above: Jesse rebuilt the solution and the running host
loads the exact dev.11 libraries. Retained results distinguish 227 passes, two assertion-only
source corrections pending the next rebuild and three opt-in skips, plus live CRM source/receipt
and recovery HTTP-boundary proof. Immediate mode is active; Outbox-drainer activation, remaining
workflow/copy checks and H5 qualification remain open.
