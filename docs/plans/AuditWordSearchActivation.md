# Audit word-search activation

Status: provisioning preparation complete; target selection and positive qualification pending; current live word search remains unavailable. Updated 2026-10-18.

## Current evidence and target choice

The running host accepts exact event/entity filters with the dev.13 packages. Word search returns
HTTP 503 rather than a successful empty result. The configured local Azure SQL Edge instance reports
Full-Text Search absent, and `audit.AuditEvents` has no full-text index. See
[dev.13 evidence](../evidence/audit-pipeline-2026-10/release-dev13/README.md).

This machine is ARM64. Microsoft supports SQL Server Linux containers on x64 hosts and explicitly
excludes emulator/translation environments from tested/supported configurations. Choose either an
existing x64 SQL Server target with Full-Text Search or a local x64 container under emulation used only
for development experiments. Do not present emulation success as supported deployment qualification.
No target has been selected by this plan; no paid resource, connection switch, or service restart is authorized.

First-party references checked 2026-10-18:

- [SQL Server Linux container prerequisites](https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker?view=sql-server-ver16).
- [Install Full-Text Search on Linux](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/setup-full-text-search?view=sql-server-ver17): install the matching `mssql-server-fts` component; engine availability alone is insufficient.
- [Microsoft parameterized Docker sample](https://github.com/microsoft/mssql-docker/tree/master/linux/samples/Parameterized%20SQL%20Server%20Docker%20Image): reference mechanics, not a qualified image.
- [CREATE FULLTEXT INDEX](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-fulltext-index-transact-sql?view=sql-server-ver16): requires a unique single-column non-null key and execution outside user transactions.
- [Full-text index metadata](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-fulltext-indexes-transact-sql?view=sql-server-ver16): population is asynchronous; completed crawl and automatic tracking are separate facts.

## Exact preparation scope

- `src/MillWorks.AuditCore.EntityFramework/Sql/InstallAuditEventFullText.sql`: default-schema installer; validate engine, transaction state, table, columns, key, and existing definitions before accepting/provisioning. Refuse conflicts rather than silently dropping/rebuilding objects. Creation starts asynchronous population.
- `src/MillWorks.AuditCore.EntityFramework/Sql/VerifyAuditEventFullText.sql`: read-only definition/population diagnostic with nonzero failure via THROW.
- This plan and the linked evidence directory. No EF migration rewrite or API/library behavior change is required for standalone SQL provisioning.

The installer is already embedded in the EntityFramework package; a future package must carry its
updated bytes before consumers rely on that embedded copy. Source scripts can be reviewed/run directly
without rebuilding the host. The separate verifier is a source deployment artifact, not embedded by this change.

## Activation sequence

- [ ] Select the target and record engine/component versions, architecture and image/package hashes. For a local experiment, prepare a separate service, loopback port and fresh volume; do not attach the existing SQL Edge volume or replace the 1433/1434/1435 services. Pin the selected image and matching FTS package after resolving their actual versions.
- [ ] On the isolated target, create an owned synthetic audit fixture. Verify both scripts reject an unavailable engine, and the installer refuses an ambient transaction, missing table/key/columns, wrong language, disabled index, wrong catalog/accent setting, manual tracking or enabled stoplist without replacing objects.
- [ ] Run the installer outside user/implicit transactions, then run it again to prove idempotency. Run the verifier until the bounded population deadline; pending is not complete. If existing objects disagree, retain diagnostics and obtain an explicit repair decision; never drop automatically.
- [ ] Prove indexed literal words, multiple ANDed words, quoted phrases, short words with stoplist off, exact identifiers combined with words, metadata filtering and tenant authorization through the existing consumer boundary. Verify a known newly inserted owned fixture becomes searchable under automatic tracking within a bounded wait. Compare event identity and stored payload before/after provisioning. Use synthetic data; no payloads or credentials in evidence.
- [ ] For moving the running host, prepare a consistent backup/restore to a fresh target and verify matching integrity keys, migrations and counts. Retain the original database and configuration. Obtain Jesse's explicit instruction before changing the host connection or restarting user-managed services. Full backup/recovery qualification remains under the disposition/recovery plan, not this index installer.
- [ ] After the authorized host switch/restart, verify exact filters still work and word search returns HTTP 200 with known matches. Capture unsupported/unready 503 behavior independently; a 503 is a correct failure contract but not successful activation.

## Validation state

Three negative preflight checks passed on the existing disposable SQL test instance (1435), with
no DDL applied: unsupported installer/verifier and ambient transaction refusal. Dynamic DDL was
required to defer feature binding until after the guards. [Evidence](../evidence/audit-word-search-2026-10-18/README.md).

Target-dependent positive provisioning and query tests remain pending. Preparation must not mark
word search activated, change retention/disposition semantics, or weaken the explicit 503 contract.
The live custody assessment/AppLog configuration findings are separate operational work.
