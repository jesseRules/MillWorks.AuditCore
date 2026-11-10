# Audit full-text preparation — 2026-10-18

Prepared the default-schema installer and a separate read-only definition/population verifier.
The installer rejects unavailable FTS, ambient/implicit transactions, missing schema/key/columns,
and incompatible existing definitions instead of silently skipping them or replacing objects.
DDL is deferred with EXEC so feature binding cannot bypass the capability guard on SQL Edge.

**Three negative checks passed** against the existing disposable SQL test instance on loopback
port 1435, using master catalog metadata. No DDL was applied. Installer unavailable-engine error
51001, verifier unavailable-engine error 51010, and ambient-transaction error 51000 were all
observed with nonzero sqlcmd exits. [Results](preflight-results.json), [source hashes](source-hashes.json).

An initial installer probe exposed native error 7609 at feature binding before its guard; deferring
CREATE FULLTEXT statements corrected the observed failure path. No error/assertion was suppressed.

Positive installation, idempotency, mismatched-object cases on an FTS-capable engine, population,
and actual full-text matches remain pending target selection. No container was started/replaced,
no application database/connection was modified, and no API build/restart occurred.
The embedded installer changes are **not** in the already published dev.13 package.

[Activation plan](../../plans/AuditWordSearchActivation.md) records the ARM64/x64 target decision,
first-party references, exact scope and remaining acceptance steps.
