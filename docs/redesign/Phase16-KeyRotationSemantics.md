# Phase 16 — Digital Signature Key Rotation Semantics

**Status: Deferred until needed**

Master plan context: [`../RedesignPlan.md`](../RedesignPlan.md)

## Why this phase exists

Phase 12 fixes the immediate correctness bug: RSA key caches in
`TamperDetectionService` must be isolated by configured key path so one service
instance does not accidentally reuse another instance's key material inside the
same process.

That fix is enough for the current documented contract in `README.md`. It does
**not** add stronger runtime guarantees such as live key rotation, in-place PEM
replacement detection, or cross-process refresh coordination. Those guarantees
should only be added if a real deployment requires them, because they expand
operational and verification complexity.

This phase captures that future work so it is specified, but intentionally
deferred.

## Problem

Today the library treats digital-signature key material as effectively static
for the lifetime of a process. That is acceptable for the current README
contract, but a future regulated deployment may require one or more of these:

1. Rotate digital-signature keys without restarting the application.
2. Replace PEM contents at the same configured file path and have AuditCore
   detect the change.
3. Keep multiple app instances consistent during a coordinated key rollover.

Without an explicit contract, operators and developers will make assumptions
that the current implementation does not guarantee.

## Goal

If this phase is activated, define and implement a clear production contract for
digital-signature key rotation semantics.

The first decision is the product contract, not the cache mechanism:

- **Option A — Restart required after key change.**
  Simple operational model. AuditCore documents that keys are loaded once per
  process lifetime and a restart/redeploy is required after rotation.
- **Option B — Live rotation supported.**
  AuditCore detects key changes and refreshes cached RSA parameters while the
  process stays up.

**Recommendation if this phase is ever started:** begin with Option A unless a
consumer has a concrete no-restart rotation requirement.

## Why this is deferred

The README currently positions digital signatures as an optional security layer
configured by file paths; it does not promise hot rotation or distributed cache
invalidation. The library's primary published guarantees remain:

- tamper-evident hash chain
- HMAC integrity protection
- sink-mode durability semantics
- fail-closed behavior for regulated entity writes

Adding live rotation before there is an actual deployment need would add code,
tests, and documentation burden without improving the current documented
contract.

## Scope if activated

### Minimum scope

1. Pick one explicit runtime contract: restart-required or live-rotation.
2. Update `README.md` and production configuration docs to state that contract
   clearly.
3. Add tests that prove the stated behavior and prevent accidental drift.

### If restart-required is chosen

This is mostly a documentation/validation phase:

- Keep the path-keyed cache from Phase 12.
- Document that key changes require process restart or redeploy.
- Document the recommended operational model: versioned key paths preferred
  over in-place file replacement.
- Add tests only for the static-runtime assumptions the library intends to keep.

### If live-rotation is chosen

This becomes a real feature phase. Candidate implementation areas:

- file timestamp / hash polling
- explicit cache invalidation API
- background key reload service
- atomic swap of cached RSA parameters
- verification behavior across old and new signatures during rollover

The rollover acceptance criteria must define whether historical signatures
remain verifiable by:

1. retaining previous public keys,
2. versioning signatures with key identity metadata, or
3. requiring operators to preserve old public keys outside AuditCore.

## Non-goals for the deferred phase

- Distributed coordination for its own sake.
- Over-engineered path canonicalization beyond what the active contract needs.
- Supporting every possible key-management topology before one is actually
  demanded by a consumer deployment.

## Decisions left to Jesse when activated

1. **Contract choice.** Is restart after key change acceptable, or is live
   rotation required?
2. **Operator workflow.** Will rotations use new file paths per key version, or
   replace PEM contents in place?
3. **Historical verification.** If live rotation is supported, how are old
   signatures verified after rollover?
4. **Multi-instance posture.** Must all replicas observe the new key
   immediately, or is rollout consistency handled operationally?

## Candidate files if activated

Exact file list depends on the chosen contract, but likely includes:

| Action | Path | Purpose |
|---|---|---|
| Edit | `src/MillWorks.AuditCore.Services/TamperDetectionService.cs` | Implement chosen runtime key semantics |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/TamperDetectionServiceDigitalSignatureTests.cs` | Verify the chosen contract |
| Edit | `README.md` | Document operational expectations and guarantees |
| Edit | `docs/ACEDProductionConfiguration.md` | Document regulated-deployment rotation guidance |

## Activation trigger

Do not start this phase proactively.

Start it only when at least one of these becomes true:

1. A real deployment requires no-restart key rotation.
2. Operators expect in-place PEM replacement to be picked up automatically.
3. Documentation ambiguity around key lifetime becomes a support issue.

## Done when

This phase remains deferred until activated.

Once activated, it is done only when the chosen runtime contract is explicit,
implemented, tested, and documented.
