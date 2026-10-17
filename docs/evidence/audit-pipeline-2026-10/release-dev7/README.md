# H1 local package release — 1.15.4-dev.7

**Status:** All five packages and symbols published to both established local feeds on
2026-10-16. Consumer references, application migrations, API builds/restarts and runtime
acceptance are outside this release step. Jesse will commit the working tree; H2 is next.

The full Release build/test command passed: **2,536 passed, zero failed**. The console reports
23 skipped and 2,559 total; the TRX includes nine additional explicitly opted-out soak/profile
cases, for **32 NotExecuted and 2,568 recorded cases**. Of those 32, 19 are the expected H1
provider-specific cases, four are existing provider limitations, and nine are explicit
soak/performance opt-outs. No filter was used. [Verification](verification.json) records the
exact command, SDK, raw counters, derived outcomes and skip reasons. Retained
[TRX](release-dev7.trx) and [build/test log](build-and-test.log) are the primary evidence.
Build warnings concern nullable test code; pack also reports missing package README files.

[Source fingerprints](source-manifest.json) identify the dirty working tree, baseline commit,
and source/test/configuration file hashes. This release includes all current source changes,
including the concurrent nullable/refactoring edits; it does not claim a clean release commit.
Source hashes were rechecked before pack and evidence closeout. The later user commit should
be recorded separately rather than retrospectively assigned as the package build commit.

[Package manifest](package-manifest.json) records five packages and five symbol packages,
SHA-256 hashes, exact pack commands and packaged DLL hashes. Inspection checked dev.7
metadata/release notes, matching internal dependency versions, equality with owning Release
DLLs, and both H1 migration identifiers. [Pack log](pack.log), [pack script](pack.py) and
[delivery script](deliver.py) retain the mechanics used; scripts contain this machine's paths
and delivery deliberately refuses existing versions.

[Delivery receipts](local-feed-delivery.json) record hash-verified publication to:

- `/Users/jesse/LocalNuGetPackages`
- `/Users/jesse/RiderProjects/MillWorks/local-packages`

Existing package versions were not replaced. No NuGet.org publication occurred.

H1 adds `20261016210852_AddOutboxOwnershipEvidence` and
`20261016211640_RetainOutboxFailureSources`. Apply these before starting new drainers, replace
all old unguarded drainers, and coordinate host authorization, retention and external DLQ
copy handling as described in the [H1 implementation contract](../../../plans/BatchPublishingH1Implementation.md).
Local publication does not establish safe host rollout. H2/H3a/H3 remain unimplemented;
the sequence release's populated-runtime and scheduled-scan acceptance remain separate.
