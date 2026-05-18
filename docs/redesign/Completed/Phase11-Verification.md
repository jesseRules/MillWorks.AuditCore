# Phase 11 — Verification & soak

**Completed 2026-04-26**

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase10-DocsRewrite.md`](Phase10-DocsRewrite.md)

## Goal

End-to-end verification that the redesign holds under realistic load
and that the two new apps about to integrate AuditCore can do so against
the new contract without regressions. Three deliverables:

1. Full test suite green across both repos.
2. Endurance soak (100k events, four concurrent writers) passes under
   `AuditSinkMode.TransactionalOutbox` AND `AuditSinkMode.Immediate`.
3. Manual end-to-end MillWorks smoke confirms the FailClosed posture
   actually rolls back business writes for `[PHI]` entities, and that
   chain integrity verification covers consumer-DbContext writes.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- **Test failures are blockers.** Any test red after this phase is a
  Phase 11 incomplete state; do not mark the phase done with red tests.
- **Soak regressions are blockers.** The Phase 6.5 soak's 750 MB managed
  memory cap and DLQ-empty assertion must hold for both sink modes.
- **No code changes in this phase.** If verification finds bugs, they
  open follow-up phases; do not patch in-line and call Phase 11 done.

## Files

This phase is mostly test runs and manual verification. Files touched:

| Action | Path | Purpose |
|---|---|---|
| Modified | `tests/MillWorks.AuditCore.Tests/Integration/Endurance/IntegrityChainSoakTests.cs` (or sibling) | Add a sink-mode parameterization so both `Immediate` and `TransactionalOutbox` are covered |
| New (optional) | `docs/redesign/Phase11-VerificationLog.md` | Capture results, dates, observed metrics |
| Updated | `docs/RedesignPlan.md` | Mark master plan "Completed YYYY-MM-DD" |

## Test runs

### AuditCore — full suite

```bash
cd /Users/jesse/RiderProjects/MillWorks.AuditCore
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj
```

### AuditCore — SQL Server lane

```bash
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~Integration.SqlServer"
```

Docker required.

### AuditCore — endurance soak

Run for both sink modes:

```bash
# Immediate mode (default)
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~Integration.Endurance" \
    -e AUDITCORE_RUN_ENDURANCE=1 \
    -e AUDITCORE_SOAK_SINK_MODE=Immediate

# TransactionalOutbox mode
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~Integration.Endurance" \
    -e AUDITCORE_RUN_ENDURANCE=1 \
    -e AUDITCORE_SOAK_SINK_MODE=TransactionalOutbox
```

Existing soak budget: 100k events, four concurrent writers, 750 MB
managed memory hard cap, DLQ must be empty at the end. Both modes must
pass.

### MillWorks — full suite

```bash
cd /Users/jesse/RiderProjects/MillWorks
dotnet build MillWorks.sln
dotnet test
```

All 54 companion test projects green.

## Manual smoke tests

Run against `dotnet run --project MillWorks.Api` with a fresh DB:

1. **Audit row appears for Compliance create.** POST a Compliance
   record (e.g., `ComplianceFramework`). Query `audit.AuditLogs` —
   one row with `EntityName = "ComplianceFrameworkEntity"`,
   `Action = "Created"`, `UserId` populated.
2. **Chain coverage for Compliance.** Run a chain integrity verification:
   ```sql
   -- via your preferred SQL client, or via API endpoint
   EXEC audit.usp_VerifyChainIntegrity;  -- if exposed
   ```
   Or call `ITamperDetectionService.VerifyChainIntegrityAsync()` from a
   diagnostic endpoint. Result: `IsValid = true`, `ChainBroken = false`,
   `TotalEvents` includes the Compliance row.
3. **FailClosed rollback for `[PHI]` entity.** Inject a fault into
   `IFieldEncryptionService` (e.g., temporarily configure a wrong
   master key path) so encryption throws on
   `DataBreachReportEntity` save. Attempt a POST. Observe:
   - HTTP 500 (or whatever the FailClosed handler returns).
   - `audit.AuditLogs` does NOT contain the partial write.
   - `compliance.DataBreachReports` does NOT contain the row.
4. **Consumer DbContext rollback isolation under `Immediate` mode.**
   Switch `AuditSinkMode = Immediate`, repeat scenario 3. Observe:
   - HTTP 500.
   - `audit.AuditLogs` does NOT contain the row (sink-publish failure
     rethrows, business txn rolls back).
   - `compliance.DataBreachReports` does NOT contain the row.
5. **No interceptor on a non-audited DbContext stays silent.** Pick a
   DbContext that the Api does NOT register the interceptor for (one
   that's not in the 9-library list). POST a record. Observe: zero
   audit rows, zero exceptions, zero log warnings.
6. **Outbox drainer recovery.** Under `TransactionalOutbox` mode,
   stop the Api mid-drain (force-kill while there are pending
   `AuditOutbox.Status = Pending` rows). Restart. Observe: the
   drainer picks up the pending rows and completes them within one
   poll cycle.

## Decisions left to Jesse

1. **Performance-regression budget.** The redesign moves writes off
   the consumer connection. For the soak, the existing budget (100k
   events, 4 writers, single SQL Server container) was tuned against
   the old single-connection design. Should the new acceptance be
   "no worse than old by more than X%"? **Recommendation:** measure
   first, then set the budget. Capture in `Phase11-VerificationLog.md`.
2. **Coverage gap for the 5 unaudited libraries.** The 9 migrated
   libraries are covered. The 40 other MillWorks libraries that don't
   currently use audit — should this phase smoke-test that they
   continue to NOT generate audit rows? **Recommendation:** yes —
   spot-check 2 (e.g., Project, Survey). If they accidentally start
   producing audit rows, that's a real regression.
3. **Two-new-apps readiness sign-off.** Once Phase 11 is green, the
   two new apps Jesse plans to integrate (per the kickoff
   conversation) can adopt AuditCore against the new contract. Is
   the sign-off a separate doc, or just a line in
   `Phase11-VerificationLog.md`? **Recommendation:** the latter.

## Verification

This phase IS the verification phase. Acceptance:

- All test runs above are green.
- All manual smoke scenarios pass.
- Endurance soak passes for both sink modes.
- Soak metrics recorded in `Phase11-VerificationLog.md` (or chosen
  alternative location).
- Master plan + all 11 phase docs carry "Completed YYYY-MM-DD".
- AuditCore ready for integration into the two upcoming apps.

## README impact

None. Phase 10 finalized docs.

## Out of scope

- New features.
- Performance tuning (regression detection only — fixes are follow-up
  phases).
- Multi-region soak.
- Chaos testing (network partition, SQL failover, etc.) — out of scope
  for this redesign; can become a future phase.

## Done when

- All test runs green.
- All manual smoke tests pass.
- Endurance soak passes for both sink modes.
- Verification log written.
- Master plan marked complete.
- Two new apps cleared to integrate.
- Phase doc updated with "Completed YYYY-MM-DD".
