# H2/H3a/H3 local release — 1.15.4-dev.8

**Status:** All five packages and their symbols published to both established local feeds on
2026-10-16 (delivery receipt: 2026-10-17 UTC). Source and dev.8 metadata are committed as
`6f560a17dd4ced07ff19d410ecc79beb56eda068`. Release evidence/status documentation is produced
subsequently and awaits Jesse's next commit. No host consumer update, application migration,
API build/restart or populated-runtime acceptance occurred.

## Verification and provenance

- Release regression with disposable SQL enabled: **2,644 passed, zero failed**, 23 console
  skips. TRX records 32 NotExecuted entries, including nine explicit soak/profile opt-outs.
  The filter excludes the container SQL/Garnet namespaces; it does not claim those lanes ran.
  [Build/test output](initial-build-test.log), [TRX](release-dev8.trx).
- After Jesse committed during that first run, all five assemblies were rebuilt against
  `6f560a1` and rechecked: **416 passed, zero failed**, 19 expected provider skips. This filter
  covers AspNetCore plus identity/writer/processor/resilience/ownership tests, including the
  review regressions with RCSI off/on. [Final output](final-build-test.log),
  [TRX](release-dev8-final.trx).
- The initial assemblies embedded `76af4a9`, since that build began before the commit. Those
  binaries were not packaged. [Initial artifact hashes](initial-build-artifacts.json) preserve
  that distinction. Every packaged assembly and nuspec references `6f560a1`; the actual
  compiled inputs are fingerprinted in the [source manifest](source-manifest.json), captured
  when the source/version commit was clean and rechecked before packing.
- [Verification JSON](verification.json) retains exact commands, .NET SDK 10.0.202, raw
  counters and skip reasons. Existing nullable test warnings remain. Pack warns that the
  packages lack embedded README files; no new build/pack failure occurred.
- [Package manifest](package-manifest.json), [pack script](pack.py) and [pack log](pack.log)
  record version/release-note checks, matching internal dependencies, exact Release DLL/PDB
  hashes, source commit, and the H1/H3a migration identifiers. Five nupkg and five snupkg files
  were staged and verified before delivery; binaries themselves are not committed here.
- A fresh temporary consumer restored the umbrella package and all four transitive AuditCore
  packages as dev.8, without project references or API startup. It verified the new contracts
  and all five assembly commit/version values. [Build](consumer-build.log),
  [run](consumer-run.log), [project](consumer-Smoke.csproj), [source](consumer-Program.cs).
  Its private restore cache and staging feed avoid installing candidate bits into the user's
  global dev.8 package cache during validation. Scripts retain machine-local paths.

## Local feed delivery

[Delivery receipts](local-feed-delivery.json) record SHA-256-verified copies of all ten artifacts
in each feed:

- `/Users/jesse/LocalNuGetPackages`
- `/Users/jesse/RiderProjects/MillWorks/local-packages`

The [delivery script](deliver.py) refuses existing destinations and verifies staged hashes.
[Before-delivery inventory](feed-inventory-before.json) confirms **679 pre-existing feed
artifacts were preserved unchanged**. No distributed version was replaced and no NuGet.org
publication occurred. Raw release TRX files have a narrow gitignore exception so this curated
release proof can be included in the next commit.

## Contents and activation gates

Dev.8 contains H2 acceptance/null validation, H3a globally keyed explicit-event identity and
versioned comparison receipts, H3 exact per-event outcomes and bounded contract-defect
escalation, plus both [review corrections](../h3-review-fixes-2026-10-16/README.md). H1 is retained.
The new `20261016230118_AddAuditEnvelopeIdentities` migration is included alongside the H1
ownership/failure-source migrations. Existing migrations were not rewritten.

Before host activation, update producers to preserve ExplicitEventId and occurrence time,
provide trusted write authorization, update custom identity-aware capabilities/exact metadata,
map/apply receipts in owning databases, and review actual queued/persisted data before enabling
queue version 2. Existing H1 authorization/retention/recovery gates still apply. See the
[H3a contract](../../../plans/BatchPublishingH3aImplementation.md) and
[H3 contract](../../../plans/BatchPublishingH3Implementation.md). Local package availability
is not host acceptance. H4/H5, optional G and sequence populated-runtime/three-scan acceptance
remain separate work.
