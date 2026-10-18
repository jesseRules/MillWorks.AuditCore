# Dev.10 local package delivery — 2026-10-18

**Published to both local feeds; MillWorks.Api restore succeeds.** The umbrella
`MillWorks.AuditCore` and `MillWorks.AuditCore.EntityFramework` packages requested by the host
require the coordinated Abstractions, Providers and Services dev.10 dependencies. Neither
feed contained dev.10 artifacts before delivery, so all five packages and symbols were
published together. No existing artifact was overwritten.

## Provenance and validation

The Release build uses the fingerprinted working tree based on
`e45b3ac5578762f4ee750ad6ae8db2df2b7748b9`; this is not a claim that the current fixes are
committed. [Source manifest](source-manifest.json) retains 561 build/test inputs. The version
and release notes were already prepared for dev.10 when this publication task began.
No production source or version change was needed during publication.

The [initial broad run](release-dev10.trx) passed 2,503 tests, skipped 236 reported cases,
and failed one SQL fixture: `ConsumerRollback_AtomicallyRollsBackOutboxRow` created the
outbox table without its required identity-receipt table. It is retained as a failed run,
not relabeled green. The disposable Testcontainers server was separate from application SQL.

The fixture now maps `AuditEnvelopeIdentities` and checks that both the source row and receipt
exist before consumer rollback and disappear afterward. After rebuilding, the complete SQL
outbox fixture and selected administration/archive/tenant-restore regressions passed
**42 tests, zero failures/skips**. [Final TRX](release-dev10-final.trx),
[verification](verification.json). Only this test fixture changed after the broad run;
production inputs remained identical. Existing nullable warnings remain in test compilation.

Package checks verified coordinated versions and dependencies, repository metadata,
packaged DLL/PDB equality to owning Release outputs, and the three existing H1/H3a migrations.
There is no new schema migration. A package-only consumer with a fresh private cache restored
all five dev.10 packages, compiled against the new administration interfaces and loaded all
five assemblies with the expected informational versions. It opened no database and passed
before feed delivery. [Package manifest](package-manifest.json), [consumer run](consumer-2.log).

## Delivery and host restore

[Delivery receipts](local-feed-delivery.json) record package/symbol hashes in:

- `/Users/jesse/LocalNuGetPackages`
- `/Users/jesse/RiderProjects/MillWorks/local-packages`

All ten artifacts per feed were hash-verified after copying. All 3,167 previously inventoried
feed artifacts retained their original hashes. The retained scripts describe this historical
run; they do not authorize repacking or overwriting the distributed dev.10 identity.

The following command completed successfully, without building the API:

```sh
dotnet restore MillWorks.Api/MillWorks.Api.csproj --disable-parallel -p:BuildInParallel=false
```

[Host resolution](host-resolution.json) verifies all five AuditCore dev.10 package SHA-512
values against the delivered archives. The same assets resolve all three direct BackgroundJobs
packages as `2.0.14-dev.20`. [Restore log](api-restore.log).

The API is ready for its user-managed rebuild. This task did not build/restart the API,
change ScanWorker, apply application migrations or claim consuming HTTP/runtime acceptance.

## Rebuilt running host verification — October 18

Jesse rebuilt API/tests and restarted the API at 14:11:42 local.
[Artifact/process evidence](host-rebuilt-artifacts.json) verifies both dependency files resolve
all five AuditCore packages as dev.10 and the three direct BackgroundJobs packages as dev.20.
All five API output DLLs match the published dev.10 packages. The running process maps the
rebuilt API and AuditCore module paths, with its start after the recorded build timestamps.

[Rebuilt-host checks](host-rebuilt-verification.json) used existing artifacts with
`--no-build --no-restore`: **all 15 administration HTTP boundary tests passed**, zero skips.
These exercise real MVC/Identity permission handling, the host administration authority and
library facade in TestServer with in-memory persistence; they do not perform destructive
operations against the application database. Anonymous requests to the running host's
compliance export and integrity-proof export routes both returned 401.

The wider selected chain/query fixture passed four tests and failed one existing assertion:
`GetAuditEvents_ForwardsPaginationAndFiltersToSearchService` supplies an explicit 23:59:59
end time but expects the day's final tick. `InclusiveEndOfDay` expands only midnight/date-only
input and preserves explicit times. This unchanged assertion/helper mismatch is outside the
administration/package fix; no production query behavior or test source was changed here.
[TRX](dev10-host-administration.trx) retains all 20 results, including the failure.

The live liveness endpoint returned 200/Healthy; readiness returned 200/Degraded, so this is
not an all-services-healthy claim. The bounded startup log observation (14:11:42–14:14:44
local) contains 2,738 timestamped events, zero ERR/FTL events and no audit-matching errors.
[Log check](host-startup-log-check.json). The outbox drainer reports disabled because the
current sink mode is not TransactionalOutbox; these checks do not close outbox rollout or H5.

This closes rebuilt-artifact verification and the focused administration HTTP test gate.
Broader tenant/read/disclosure findings, the query assertion mismatch, and deployment-wide
readiness remain separately open. The agent performed no API build/restart or administrative
data mutation.
