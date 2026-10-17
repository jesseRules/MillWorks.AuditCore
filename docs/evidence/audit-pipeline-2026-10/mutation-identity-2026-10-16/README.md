# EF mutation identity correction — 2026-10-16

Status: 27 focused checks and 4 SQL Server cases passed; Release regression and publication remain pending.

The real-interceptor SQLite reproduction against the old content-derived producer failed
2 cases and passed 1: separate creations with a reused temporary key shared an EnvelopeId,
as did separate updates repeating the same value transition. Failed-save retry already
preserved identity. `before.trx` and `before.log` retain that baseline.

After correction, 27 focused checks passed with zero failures/skips, including 15 new
mutation lifecycle cases, 11 existing interceptor-routing cases, and the new payload-safe
diagnostic case. `focused.trx` and `focused.log` retain this result.

Commands from the repository root:

```sh
python3 /Users/jesse/RiderProjects/MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build -- --filter FullyQualifiedName~InterceptorMutationIdentityTests --logger 'trx;LogFileName=mutation-before.trx' --results-directory /tmp/auditcore-mutation-identity
python3 /Users/jesse/RiderProjects/MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --build -- --no-restore --filter 'FullyQualifiedName~InterceptorMutationIdentityTests|FullyQualifiedName~InterceptorSinkRoutingTests|FullyQualifiedName~IdentityFailureDiagnostics' --logger 'trx;LogFileName=mutation-after.trx' --results-directory /tmp/auditcore-mutation-identity
```

The focused coverage includes generated-key reuse, repeated transitions, failed/cancelled
saves, token serialization/restoration into replacement contexts, rollback, changed retries,
accepted audit writes with lost acknowledgement, preserved unverified historical rows,
identical rows in one save, pooled-context lease reuse, internal audit-bookkeeping exclusion
and diagnostic IDs without payloads. A mutation token is not proof of business commit:
callers must reconcile uncertain business commits and preserve the original semantic inputs.

All 4 SQL Server cases passed with zero failures/skips on the owned disposable instance at
localhost:1435: integer/composite generated keys with RCSI off/on, durable audit acceptance
with lost acknowledgement, retained-token replay and a subsequent separate mutation.
`sql.trx` and `sql.log` retain the result.

```sh
python3 /Users/jesse/RiderProjects/MillWorks/scripts/dev-work.py test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj --sql -- --filter FullyQualifiedName~GeneratedIntegerAndCompositeKeysUseRetainedMutationTokens --logger 'trx;LogFileName=mutation-sql.trx' --results-directory /tmp/auditcore-mutation-identity
```

 No application database writes, API build,
service restart, migration or package publication were performed by these checks.

For an independent baseline reproduction, place `before-fixture.cs.txt` into the baseline
`18090b6` checkout as `tests/MillWorks.AuditCore.Tests/EntityFramework/InterceptorMutationIdentityTests.cs`
and run the first command above. This baseline-compatible fixture retains the original three
scenario bodies and supporting capture sink; it is extracted for reproducibility, not an
assertion that it is a byte-identical archive of the initial temporary test source. Perform
that reproduction in a separate checkout; do not replace the current fixed fixture.

The subsequent [dev.9 release](../release-dev9/README.md) passed 2,664 Release tests and was
published to both local feeds; exact MillWorks package restore is verified. Populated host
startup and updated host test execution remain pending the API/test rebuild.
