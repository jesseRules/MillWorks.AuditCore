# Consistent-read release — 1.15.4-dev.6

**Status:** Prepared; isolated build/test/pack and local-feed publication pending.

[Source inventory](source-manifest.json) names the committed implementation, baseline diff,
every changed file, hashes and purpose. NUnit 5 and dependency/Pkcs pin updates are explicitly
included. H1–H5/G and disposition/recovery implementation are excluded; their plans are included
as documents only. Existing 102-test and eight-profile results remain historical source evidence.

Build a clean isolated clone of the release metadata commit, then validate its Release artifacts.
The runtime implementation matches the earlier retained source hashes; changed version metadata
requires a new build. Record actual commands/results and package/DLL/dependency/migration checks
in `verification.json`, with the isolated source commit and clean-tree checks. Packages are
staged outside either live feed until all checks pass. Copy verified immutable artifacts to
both configured feeds without overwriting any existing identity. Consumer resolution and
loaded runtime checks are separate from package creation.

MillWorks API compilation requires explicit authorization naming `MillWorks.Api`, per its
AGENTS.md. Do not build or restart it as an incidental packaging step. Populated live query
capture and three completed host scans remain open after package publication.
