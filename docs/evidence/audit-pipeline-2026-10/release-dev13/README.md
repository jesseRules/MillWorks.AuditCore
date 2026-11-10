# Local release dev.13 — 2026-10-18

Published all five AuditCore packages and symbols at **1.15.4-dev.13** to both established
local feeds, preserving 3,233 existing artifacts. Updated all 21 active MillWorks project files
from dev.12 to dev.13; historical evidence projects remain unchanged. API and AI were not built
or restarted. This release addresses the package/source mismatch behind the reported missing
`AuditSearchRequest.EventId`, `AuditSearchRequest.EntityId`, and `AuditSearchUnavailableException`.

## Verification

- **230 focused Release tests passed, zero failed/skipped.** Coverage includes query services,
  administration, tenant restore, archive compression, and audit logger fixtures. The SQL test lane
  was not enabled and the full suite was not run. This does not close H5 or historical SQL issues.
  [Results](focused-results.xml), [summary](test-summary.json).
- A package-only consumer built with zero warnings/errors and executed without a database. It
  assigns both filters, verifies JSON property names, constructs/catches the exception, and checks
  informational versions of all five assemblies. [Source](consumer-Program.cs),
  [output](consumer-output.txt), [resolved dependencies](consumer-verification.json).
- Five package dependency sets and versions, owning DLL bytes, and symbol PDB bytes were verified.
  The EF package includes `20261018233845_RestoreAuditEventRequestPathLimit`; host database
  migration/application is separate from this release. [Package hashes](package-manifest.json).
- Source was frozen from baseline commit `1ebc847c26f8b566a0d942c79d7e1406fbcc5c83` plus
  fingerprinted version/test changes in `/private/tmp/auditcore-dev13-release`. Exact build input
  hashes were rechecked against the shared checkout before delivery. This is working-tree provenance,
  not a claim that the baseline commit alone contains dev.13. [Source manifest](source-manifest.json).
- Publication used exclusive creation and verified hashes in both feeds. Existing artifacts were
  checked for changes. [Delivery and consumer-edit receipts](local-feed-delivery.json).

## Validation correction and packaging retry

The initial focused run had 229 passes and one failure in
`SearchAuditEventsAsync_AllFiltersPopulated_ReturnsFilteredResults`. Its fixture expected a row
with different JSON text to be excluded even though its request no longer supplied a word-search
term. The fixture now varies entity type, a filter the request actually supplies. No production
filter, expected count, or assertion was relaxed. [Initial results](focused-initial-results.xml).
Existing nullable-reference warnings were emitted when compiling the test project.

An initial sandboxed no-build pack stalled on the Services project. That owned process was sent
TERM; a later KILL attempt found that child had already exited, but the original Python parent
continued to the next package. After delivery, its remaining AspNetCore pack child and Python
parent were explicitly stopped. Packaging succeeded outside the
sandbox with one MSBuild worker and a fresh output directory. Initial staging artifacts were
not published. [Pack verification script](pack.py).

## Commands and remaining host step

Build/test used the shared runner:

```text
python3 /Users/jesse/RiderProjects/MillWorks/scripts/dev-work.py test /private/tmp/auditcore-dev13-release/tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build -- --configuration Release --filter 'FullyQualifiedName~Services.Query|FullyQualifiedName~AuditAdministration|FullyQualifiedName~AuditTenantRestore|FullyQualifiedName~AuditArchivalServiceCompression|FullyQualifiedName~AuditLogger' --logger 'trx;LogFileName=focused.trx' --results-directory /private/tmp/auditcore-dev13-results
python3 /Users/jesse/RiderProjects/MillWorks/scripts/dev-work.py build /private/tmp/auditcore-dev13-consumer/Smoke.csproj -- --configuration Release
dotnet /private/tmp/auditcore-dev13-consumer/bin/Release/net10.0/Smoke.dll
```

The first runner invocation with an extra build-property override was rejected before execution;
validation used the supported command above. API dependency restore is verified separately below.
Jesse's next application/test rebuild must establish host compilation and runtime acceptance;
this package consumer does not claim the full API builds or that dispositions are implemented.

API dependency restore completed successfully with `dotnet restore MillWorks.Api/MillWorks.Api.csproj --disable-parallel`. All five resolved dev.13 SHA-512 hashes match the delivered packages; all 21 reference edits were rechecked. [Host resolution](host-resolution.json). No host compilation was performed.

## Post-rebuild check

Jesse confirmed rebuilt and running. All five AuditCore DLLs in the API Debug output match
the delivered dev.13 hashes: [module checks](rebuilt-host-modules.json). Live health returned
HTTP 200 / `Healthy`; readiness returned HTTP 200 / `Degraded`. The aggregate endpoint does
not identify the degraded component, so this does not establish an AuditCore readiness failure
or full application health. [Health evidence](host-health.json).

Focused host tests ran with `--no-build --no-restore`: **29 passed, 1 failed**. Search request
forwarding and the unavailable-word-search exception mapping passed. The failure was a stale
end-date expectation in `GetAuditEvents_ForwardsPaginationAndFiltersToSearchService`: it expected
an explicit 23:59:59 timestamp to expand, while the controller expands only midnight values.
The test source now covers both midnight expansion and explicit timestamp preservation.
That source correction is **pending Jesse's next API-test rebuild**; old binaries do not verify
it. No API/AI compilation or service restart was launched. [Host results](host-results.xml),
[summary](host-test-summary.json).

## Live search and readiness diagnosis

Follow-up read-only checks identified the degraded readiness component:
`custody-deployment` has no configured capability assessment or essential deployment requirements.
The full health report additionally lists disabled AppLog OTEL export and its corresponding no-op
seam; these AppLog entries are not tagged `ready`. AuditCore's `audit_integrity_pipeline` is
**Healthy**, with no pending/stale/failed integrity work reported. [Sanitized health diagnosis](health-diagnosis.json).

Supported seeded authentication through the frontend proxy permitted read-only checks against two
retained, owned synthetic export events. Exact `eventId` and `entityId` queries returned the expected
records with unchanged retained payloads; a nonexistent event ID returned an empty successful result.
No sessions were replaced and no domain mutation or disposition was executed. Evidence retains counts
and outcomes rather than tokens or payloads. [Live search checks](live-search-verification.json).

Word search returned HTTP **503**, matching the explicit unavailable contract. A read-only SQL metadata
probe against the configured local database reported `SERVERPROPERTY('IsFullTextInstalled') = 0`,
and no full-text index on `audit.AuditEvents`. [Metadata result](fulltext-metadata.txt).
The service requires an enabled, crawl-completed index covering `JsonData`, `User`, `UserFullName`,
`EventType`, `EntityType`, `EntityId`, and `AdditionalData` with neutral language ID 0.
Word-search activation therefore remains pending a full-text-capable SQL topology and reviewed index
provisioning/population, followed by a successful live word-search check. No database DDL, SQL image
change, configuration change, rebuild, restart, or health-check weakening was performed.

The custody capability assessment and AppLog exporter configuration are separate host operational
work. Disposition implementation, recovery qualification, and the corrected host date-filter test
still retain their previously recorded outstanding status.
