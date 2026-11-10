# AuditCore dev.15 — narrow audit paging/count index

Published five packages and five symbol packages at **1.15.4-dev.15** to both established local feeds without replacing any existing artifacts. Updated22 active MillWorks consumer project files; API restore resolves all five new packages with SHA512 hashes matching the published bytes. Historical evidence projects remain unchanged. [Delivery](delivery.json), [host resolution](host-resolution.json), [packages](package-manifest.json).

## Source and isolated SQL evidence

New owning migration `20261019182702_AddAuditEventPagingIndex` creates only `IX_AuditEvents_Date_EventId (InsertedDate DESC, EventId DESC)`. It has no included payload/display columns. Down drops only that index. The20 previous migration bodies/designers are unchanged; snapshot diff adds only this index. Upgrade and rollback scripts were generated from the actual compiled owning library, without connecting to the application database. [Review](migration-review.json), [upgrade](paging-up.sql), [rollback](paging-down.sql).

**88 focused Release tests passed, zero failed/skipped.** The added SQL fixture runs only when opted in, on existing loopback test endpoint1435 in its own nonce catalog. It seeds100000 synthetic rows with timestamp ties, null timestamp and varied event/user strings, then executes the actual generated migration Up/Down operations. It compares unchanged unfiltered/filtered shallow/deeper page IDs and ordering, exact count equality, actual plans, reads and rollback behavior. It does not use index hints, flush caches or change server settings. [Test summary](test-summary.json), [comparison](release-index-comparison.json).

| Read | Before logical reads | After logical reads |
| --- | ---: | ---: |
| Unfiltered first25 keys | 4453 | 3 |
| Date-filtered first25 keys | 4449 | 3 |
| Date-filtered offset25 | 4449 | 3 |
| Date-filtered offset10000 | 4449 | 45 |
| Exact date count | 4449 | 400 |

All four key-page sorts disappeared, and the optimizer selected the new index without hints. Both owned catalogs from initial/release checks are absent after teardown; a separate read-only1435 catalog check confirms cleanup. No other fixtures were removed. [Cleanup](cleanup.json).

This is a synthetic fixture comparison, not a claim of the same improvement on the application’s data distribution or cold-storage latency. Exact counts and deeper offsets still scan matching index entries. The added index also costs storage and maintenance on event writes. The observed app outlier was dominated by Buffer IO; reducing reads addresses that cost, but cannot guarantee storage latency.

## Package/rebuild boundary

A package-only consumer built without warnings/errors and ran successfully, checking all five versions, paging helper, the new migration type and existing embedded installer. Frozen build inputs were fingerprinted/rechecked, package dependency versions and DLL bytes were verified, and bounded checks used fresh private resource preflights with the shared gate/one worker. [Consumer](consumer-run.log), [source manifest](source-manifest.json).

Delivery first detected22 active consumers rather than the previous21: another session added `MillWorks.PackageRegistry.IntegrationTests`. The inventory was reconciled before any package delivery or consumer edit, then all22 current references were updated; no previous package version was overwritten.

**Ready for Jesse’s backend rebuild/startup.** No agent-launched Angular/API build, frontend/API service management or application database mutation occurred. Only the owning disposable1435 catalogs received test DDL/data. After rebuild, verify matching dev.15 host DLLs, migration/index definition, real paging/filter results and actual plans/reads. The cold-I/O latency limitation remains explicit. Full-text activation stays deferred and is separate from this paging index.

## After Jesse’s rebuild

All five host DLLs match the delivered packages; migration/index are applied with the intended descending keys. Actual key paging uses3 reads/no sort versus~8300 before, date counts1855 reads versus~8300, and fixed-cutoff HTTP pages took123–221ms. Browser pagination and the unchanged filter replay passed, with an initial blank bootstrap failure preserved. The initial HTTP request took7.5s despite short recorded SQL durations, so first-request/bootstrap overhead remains unqualified. Full matching receipts: `/Users/jesse/WebstormProjects/MillWorksApp/plans/code-quality/evidence/specialized-activity-feeds/2026-10-19-audit-dev15-live/verification.md`. No agent-launched application build, service management or app-database mutation occurred.
