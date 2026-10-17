# Dev.9 local release — EF mutation identity correction

Published all five NuGet packages and their symbols to both established local feeds:
`/Users/jesse/LocalNuGetPackages` and `/Users/jesse/RiderProjects/MillWorks/local-packages`.
All 20 delivered files were hash-verified, and all 3,039 pre-existing feed artifacts were
preserved byte-for-byte. No nuget.org publication or existing-version replacement occurred.

## Provenance

This is a **fingerprinted working-tree release**, not a committed-source release. Baseline
commit is `18090b6977a46ff76eac8d34a14843e9b685f1c3`; the feature/version changes remain
uncommitted for Jesse. `source-manifest.json` records all 556 source/build/test file hashes.
`release-boundary.json` names each changed build input and its reason. Assembly informational
versions and nuspec repository metadata identify the baseline commit; those fields alone do
not prove the uncommitted feature snapshot. The manifest, validated DLL equality, package
hashes and delivery receipts provide that link. No commit was created by the agent.

The Release build/test used SDK 10.0.202 and passed **2,664 tests**, zero failures. Console
output reports 23 skips; the raw TRX contains 32 NotExecuted results because expanded
provider-specific cases are represented separately. `verification.json` retains exact
commands, raw counters and individual skip reasons. Optional profile/soak and provider
limitations remain excluded from acceptance claims. Existing test nullable warnings and
package missing-README warnings remain; the package-only consumer built with zero warnings/errors.

The [focused mutation evidence](../mutation-identity-2026-10-16/README.md) retains the initial
two failures/one pass, followed by 27 focused passes and four real SQL Server passes with
RCSI off/on, integer/composite keys, durable audit acknowledgement loss and token restoration.

## Package and consumer verification

`package-manifest.json` records every package/symbol hash and pack command. Checks verified
coordinated versions/dependencies, release-note prefixes, nuspec baseline commit, packaged
DLL/PDB equality to the owning Release outputs, all three existing H1/H3a migration IDs,
and unchanged source hashes before packing. There is no new schema migration, comparison
version or queue-format change.

A fresh package-only consumer restored the staged dev.9 packages into a private cache,
verified all five assembly versions and package resolutions, and exercised mutation token
capture, JSON round-trip and restoration with SQL model metadata. It opened no database.
This consumer passed **before** delivery to the feeds. The scripts and consumer sources are
retained here; `/tmp` paths describe the actual run. These are historical commands, not
permission to repack or overwrite the distributed dev.9 version.

MillWorks' 21 active direct AuditCore reference projects now target dev.9. An API dependency
**restore only** succeeded; `host-resolution.json` verifies all five package SHA-512 values
match the delivered artifacts. Host EF authorization now validates the pending mutation ID
and the same unresolved-key provenance as the interceptor. Jesse subsequently rebuilt API/tests and restarted the API; the October 17 verification below records the result.

## Host artifact check, October 17

After Jesse reported a rebuild, a `--no-build --no-restore` focused run passed
38 existing tests. This **does not validate dev.9**: both API and API-test dependency
files still identify all five AuditCore packages as dev.8, all five API output DLL
hashes differ from the distributed dev.9 DLLs, and the new
`MillWorksAuditWriteAuthorizationTests` fixture is absent from the executed results.
The running API process started October 16 at 21:23:06 local, before dev.9 publication.
The exact command, artifact timestamps and dependency versions are retained in
[the artifact check](host-artifact-check-2026-10-17.json). This initial stale-artifact finding was superseded by the rebuilt-host checks below;
no build or process restart was performed by the agent.

## Rebuilt host verification, October 17

Jesse rebuilt API/tests and restarted the API at 09:39:37 local. Both dependency files
now identify all five packages as dev.9; all five API DLL hashes match the published
packages, and the process maps those output paths after the API build.
[Runtime and test evidence](host-rebuilt-verification.json) records the checks.

The same focused command above now executes **54 tests: 49 passed, five failed**;
[TRX](host-rebuilt-focused.trx) retains the failures. The newly added dataset producer
introduced a required `MlDatasetSnapshotAuditAuthorization` constructor dependency,
missing from three test-only service collections. Those registrations are corrected
using the real policy with a mock tenant accessor. The subsequent rebuilt run below
verifies the correction. No production source change was needed for this fixture fix.

The populated startup is verified: the [read-only SQL probe](host-startup-sql.json)
found **78 new role-claim creation audit rows, all 78 with entity identity receipts**,
created after this process started. The observed startup/runtime log window contains
zero `audit_identity_unverified`, publication-authorization, immediate-sink write or
interceptor processing errors. This is bounded observation, not proof of every producer.

## Final focused acceptance, October 17

After Jesse rebuilt the corrected test registrations, the same focused filter passed
**54 tests, zero failures and zero skips**, using `--no-build --no-restore` through the
shared runner. All five test dependencies resolve dev.9. The
[final verification](host-final-verification.json) retains the exact command, compiled
assembly hash/time and selected source hashes; [TRX](host-final-focused.trx) retains
all executed results. This closes the focused speech/EF host-test gate and, together
with the populated startup evidence above, the dev.9 EF mutation rollout acceptance.

## Remaining separate work

The agent performed no API build/restart or application data modification; Jesse supplied
the rebuilt running host. Populated EF startup and focused host-test acceptance are complete. Retained-token retries also require the caller's
original semantic input and business-commit reconciliation; a token is not proof of business
commit. Other explicit producer onboarding, H4/H5 and the three completed sequence host scans
remain separate. Old history/receipts were not rewritten, backfilled or reset.
