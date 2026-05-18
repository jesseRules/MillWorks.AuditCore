# Phase 18 — Encryption Key Provisioning Workflow

**Status: Deferred until needed**

Master plan context: [`../RedesignPlan.md`](../RedesignPlan.md)
Related immediate fix: [`Phase14-KeyProviderAutoGeneration.md`](Phase14-KeyProviderAutoGeneration.md)

## Why this phase exists

Phase 14 tightens the runtime safety posture for `FileBasedKeyProvider` by
making missing-key auto-generation an explicit opt-in instead of an implicit
default.

That solves the immediate problem, but it leaves one operational question
deliberately unanswered: how should operators and developers provision the
initial file-based encryption keys when auto-generation is disabled?

For now, the answer can remain "provision keys explicitly before runtime" and
be documented. If that becomes too manual or error-prone, this deferred phase
captures the follow-on work.

## Problem

Once runtime auto-generation is disabled by default, operators may want a
clearer bootstrap workflow than "start the app in a special mode" or "write the
files yourself."

Potential needs include:

1. A one-time initialization command for file-based key stores.
2. A safer bootstrap ceremony than enabling runtime auto-generation.
3. Better operator guidance around backup/restore and missing-key failure modes.

These are workflow and tooling concerns, not blockers for Phase 14's safety fix.

## Goal

If this phase is activated, provide an explicit, documented provisioning path
for file-based encryption keys that does not rely on normal application runtime
to bootstrap itself.

## Why this is deferred

The current repository does not yet show a real operator workflow requirement
beyond the immediate runtime safety fix. Adding bootstrap tooling now would
increase API/tooling surface without evidence that the documented manual
provisioning path is insufficient.

Phase 14 should stay small: fail safe by default, allow explicit dev/bootstrap
opt-in, and document the posture clearly.

## Candidate directions when activated

### Option A — Documentation-only workflow

- Keep the runtime/library API as-is.
- Publish a documented manual provisioning procedure.
- Lowest implementation cost.

### Option B — Dedicated initialization API or helper

- Add an explicit initialization entry point for file-based key stores.
- Separate "bootstrap" from "normal runtime."
- Better operational clarity than enabling runtime auto-generation.

### Option C — External provisioning tool

- Ship a CLI or script-based initialization workflow.
- Best separation between provisioning and runtime.
- Highest packaging/documentation cost.

## Recommendation if this phase is ever started

Start with **Option B** or **Option C**, not more runtime flags.

The main value here is separating provisioning from steady-state application
execution. If provisioning becomes important enough to formalize, it should be
intentional and operator-facing rather than another hidden startup behavior.

## Decisions left to Jesse when activated

1. **Who provisions keys?** Application startup, deployment pipeline, or human
   operator?
2. **Tool shape.** Library API helper, hosted bootstrap path, or separate CLI?
3. **Environment model.** Is file-based storage expected only for dev/air-gapped
   setups, or also for regulated production deployments?
4. **Backup/restore contract.** What operator guidance must be documented to
   prevent undecryptable data after restore mistakes?

## Candidate files if activated

Exact file list depends on the chosen approach, but likely includes:

| Action | Path | Purpose |
|---|---|---|
| Edit | `src/MillWorks.AuditCore.Services/Providers/FileBasedKeyProvider.cs` | Add explicit bootstrap/provisioning entry point if done in-library |
| Edit | `src/MillWorks.AuditCore.AspNetCore/Configuration/EncryptionConfigurationExtensions.cs` | Expose provisioning-related configuration only if required |
| Edit | `README.md` | Document the provisioning workflow |
| Edit | `docs/ACEDProductionConfiguration.md` | Document regulated deployment posture |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Encryption/EncryptionKeyProviderTests.cs` | Verify chosen provisioning behavior |

## Activation trigger

Do not start this phase proactively.

Start it only when at least one of these becomes true:

1. Operators need a repeatable bootstrap process beyond the runtime opt-in.
2. Manual provisioning becomes a support burden.
3. Regulated deployments want a clear provisioning ceremony distinct from app startup.

## Done when

This phase remains deferred until activated.

Once activated, it is done only when the provisioning workflow is explicit,
tested, and documented.
