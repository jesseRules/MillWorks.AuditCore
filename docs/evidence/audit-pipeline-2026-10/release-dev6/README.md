# Consistent-read release — 1.15.4-dev.6

**Status:** Published to both local feeds; MillWorks references updated and API restore passed.
API compilation, restart, and populated runtime/three-scan acceptance remain open.

[Verification](verification.json) records the exact source commit, SDK, commands, package checks,
consumer resolution and test counters. The clean isolated checkout was built in Release with
zero errors and 66 existing nullable warnings. **125 selected tests passed, none failed or
skipped**, covering the sequence SQL concurrency/repository/service paths, envelope lookup/
replay and transaction-cleanup regressions. [TRX](release-verification.trx) and
[test log](release-tests.log) retain the result; the earlier 102-test/eight-profile evidence
remains separately labeled historical evidence.

[Source inventory](source-manifest.json) identifies the implementation baseline and included
NUnit 5/dependency changes. [Package manifest](package-manifest.json) records all five packages
and symbols, SHA-256 hashes, packaged DLL hashes, dependencies, and repository commit
`f4eb7cd76ed21c1ab6f781fcb87b5d17b2d612d3`. Package inspection checked matching dev.6 versions,
release notes, internal dependencies and inclusion of the envelope lookup migration. No
H1/H2/H3a/H3/H4/H5/G or disposition/recovery implementation is claimed.

[Delivery receipts](local-feed-delivery.json) record immutable publication into MillWorks'
`local-packages` feed and `/Users/jesse/LocalNuGetPackages`; hashes in both feeds were rechecked.
All 25 projects in the [consumer inventory](consumer-refs-before.json) now pin AuditCore dev.6.
Restore initially exposed older direct dependency references; the
[alignment receipt](dependency-alignment.json) names each actual owning project/package and
before/after version. These are existing references updated to requirements reported by NuGet;
no dependency references were added and no downgrade checks were suppressed.

[Final API restore log](api-restore.log) records the passing restore. All five resolved API
AuditCore packages are dev.6. Restore does not compile or restart the API; no user-managed
process was stopped or signalled. Fresh runtime verification still needs the loaded DLL
identities, populated scan query/reads/timing, and three completed host scans. No application
data, database settings, or scan schedule was changed.

The first package-inspection script incorrectly expected dependency DLLs to appear in only
one build output directory. All packages had already been created successfully; inspection
was corrected to compare each packaged DLL with its owning project's Release output. The
existing staged packages were inspected again without rebuilding or overwriting them.
