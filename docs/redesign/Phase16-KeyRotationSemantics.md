# Phase 16 — Admin-Driven Signing Key Rotation

**Status: Activated for planning — live-rotation contract selected; implementation and end-to-end verification pending**

**Decision: 2026-10-18.** Admins must be able to rotate audit signing keys
through MillWorks.Platform without restarting applications. This concrete
consumer requirement activates the previously deferred phase.

Master plan context: [`../RedesignPlan.md`](../RedesignPlan.md)

## Goal and selected contract

Support explicit, provider-driven live rotation initiated through an authorized
MillWorks.Platform administration workflow. New signatures adopt the new key;
historical records remain verifiable using their recorded key IDs. Rotation
must not require an application restart.

Restart-required behavior is a documented limitation for providers unable to
meet this contract. Automatic detection of overwritten PEM files is not the
primary mechanism. This document specifies target behavior, not a claim that
current providers or Platform integration already satisfy it. This update
changes planning documentation only.

## Current implementation foundation

The previous plan described process-lifetime RSA caches and configured PEM paths
in `TamperDetectionService`. The current implementation delegates HMAC and
optional RSA-PSS signing to MillWorks.Cryptography:

- `MillWorksAuditBuilder` registers separate integrity HMAC and RSA signing-key
  providers and signers; hosts can supply custom signers before registration.
- `TamperDetectionService` persists `HmacKeyId` and `DigitalSignatureKeyId`
  from signature envelopes and supplies those IDs during verification.
- Cryptography's `ISigningKeyProvider` includes `GetActiveAsync`, `GetByIdAsync`,
  and `RotateAsync`; its lookup contract keeps retired keys resolvable.
- File-system and Azure Key Vault signing providers expose rotation.

These are building blocks, not proof of coordinated live rotation. Verify the
exact packaged Cryptography version used by AuditCore, including active-key
caching, cross-process visibility, concurrency, and historical lookup.
Neighboring source alone is not release evidence.

## Responsibilities

| Component | Responsibility |
|---|---|
| MillWorks.Platform | Authorize requests, select application/environment/key usage/scope, initiate rotation, show progress and partial failures, and record the actor and outcome. |
| MillWorks.Cryptography | Own key generation, version storage, active-key selection, provider refresh, historical lookup, and atomic/concurrent rotation behavior. |
| AuditCore | Persist the actual key ID used for each signature, verify by historical ID, and preserve audit durability and integrity behavior during rotation. |
| Host composition / bridge | Connect Platform to the deployed integrity providers and expose adoption evidence without exposing key material. |

AuditCore must not depend on Platform or add a second key store/cache. Define
the host integration boundary before adding APIs. RSA, HMAC, and encryption
remain separate usages; rotating one must not silently rotate another. Scope
selection must match AuditCore's actual integrity key scope. Do not promise
per-tenant rotation without verifying that mapping.

## Rotation workflow

1. Authorize the request for the selected application, environment, usage, and
   supported scope. Record an operation ID, actor, and requested target.
2. Generate and durably store a new immutable key version. Make its verification
   material resolvable before signers begin using it.
3. Atomically activate the version for new signing operations. Operations already
   holding the previous key may finish with it; each persisted key ID must
   identify the key actually used.
4. Refresh participating instances through a defined provider mechanism. Select
   a measurable maximum propagation interval and expose each required instance's
   observed active version. Mixed old/new signatures during this bounded
   transition are valid when both versions remain verifiable.
5. Report completion only when the required instance set adopts the target
   version. An unavailable or stale instance leaves the operation pending or
   partially failed; initiating rotation alone is not completion.
6. Retire the previous version from new signing after convergence while
   preserving material required to verify retained audit records.

Specify instance membership, concurrent requests, retry behavior, and how stale
instances are prevented from signing beyond the propagation bound. Offline
instances must refresh before resuming signing. Recovery must handle missed
notifications; notifications alone are insufficient.

Existing `RotateAsync` generates and activates a key in one call. Assess whether
each provider makes the version resolvable before activation and whether the
workflow needs additional Cryptography capabilities. Do not assume an existing
prepare/activate API.

## Historical verification and retention

- Verify by recorded key ID, not the current active key. Rotation must not
  rewrite historical records or re-sign the chain.
- Retain verification material while retained audit data, archives, or supported
  restores require it. Deletion is a separate, explicitly authorized operation
  with retention checks.
- RSA verification mathematically requires only the public key. The inspected
  `RsaPssSigner` currently imports PKCS#8 private material for verification.
  Public-key-only retention therefore requires a Cryptography change and tests
  before old private keys can be destroyed under that policy.
- HMAC verification requires the original secret. Retired HMAC secrets must stay
  protected and resolvable for the required verification lifetime; RSA public-key
  retention rules cannot be applied to HMAC.
- Unknown, missing, or inaccessible historical keys must never produce successful
  verification. Define reporting that distinguishes invalid signatures from
  unavailable verification material where the API permits it.

Routine rotation preserves historical trust. Compromise response is a separate
workflow requiring an explicit policy for affected signatures, revocation,
incident reporting, and retention exceptions. Rotation alone must not silently
invalidate history or certify a compromised key's signatures.

## Failure and administration behavior

- Failure before activation leaves the previous active version usable and reports
  failure; an unusable version must not become active.
- After activation, propagation failures report the actual active version and
  affected instances. Retry/reconciliation must recover without silently
  reverting activation or generating an unintended extra rotation.
- Signing-key failures preserve existing sink durability and regulated
  fail-closed behavior. Never fall back to unsigned records or another key usage
  to make rotation appear successful.
- Record request, activation, completion, and failure with operation ID, actor,
  scope, usage, old/new key IDs, and timestamps. Never log private keys or HMAC
  secrets. Define durable recovery of operation status if an audit record cannot
  be written during a signing-provider outage.

## Implementation sequence and remaining design decisions

The product contract is settled: Platform-driven live rotation is required.
The following are implementation design work, not reasons to return to the
restart-required recommendation:

1. Inventory packaged provider capabilities and name the providers supported by
   the first implementation.
2. Specify the host administration boundary, permissions, operation status,
   idempotency/concurrency rules, and integrity scope mapping.
3. Select the propagation bound, refresh/reconciliation mechanism, instance
   membership policy, and treatment of instances exceeding the bound.
4. Specify RSA public-key support, HMAC retention, backup/restore dependencies,
   and the separate compromise-response policy.
5. Name exact implementation files and cross-repository dependencies before code
   changes. Implement provider capabilities in Cryptography, wire the host and
   Platform workflow, then validate AuditCore integration.
6. Update README and production configuration guidance after validation,
   replacing obsolete key-path guidance where applicable and stating tested
   provider guarantees and limitations.

## Documentation and candidate implementation areas

| Path / area | Purpose |
|---|---|
| `docs/redesign/Phase16-KeyRotationSemantics.md` | Selected contract and acceptance criteria; updated in this planning change. |
| `docs/RedesignPlan.md` | Link the activated follow-up without changing completion of the original sink redesign. |
| `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs` | Review provider composition and host access to the correct integrity usages. |
| `src/MillWorks.AuditCore.Services/TamperDetectionService.cs` | Preserve key-ID binding and historical verification; change only if integration requires it. |
| `tests/MillWorks.AuditCore.Tests/Services/TamperDetectionServiceDigitalSignatureTests.cs` | Historical RSA verification and no-restart integration coverage. |
| `tests/MillWorks.AuditCore.Tests/AspNetCore/IntegrityKeyUsageIsolationTests.cs` | Preserve key usage isolation during rotation. |
| `README.md`, `docs/ACEDProductionConfiguration.md` | Publish validated operator guidance during implementation. |
| MillWorks.Cryptography provider/signing areas | Rotation, refresh, concurrency, historical lookup, and public-key verification. Exact files pending inventory. |
| MillWorks.Platform and host bridge areas | Administration, authorization, progress, reconciliation, and operation audit trail. Exact files pending inventory. |

Cross-repository implementation is pending. This documentation update changes no
Cryptography, Platform, application composition, or runtime guarantees.

## Acceptance criteria / done when

1. An authorized Platform admin rotates each supported integrity signing usage
   without restarting applications; unauthorized requests change nothing.
2. New signatures adopt the target key within the specified propagation bound.
   Every persisted key ID matches the actual signing key, including concurrent
   in-flight writes.
3. Records signed before, during, and after rotation verify correctly, including
   after process restart and supported archive/restore operations.
4. Multi-instance tests cover stale/offline instances, missed notifications,
   partial failures, recovery, and truthful completion reporting.
5. Concurrent requests and retries have deterministic, tested behavior. Failures
   before and after activation preserve the durability/integrity contract.
6. RSA/HMAC/encryption and supported scopes remain isolated. Missing historical
   keys never pass verification; retention rules prevent premature deletion.
7. Private-key destruction is enabled only after public-key-only RSA verification
   is implemented and tested. HMAC retention remains explicitly separate.
8. Operations have a durable, secret-free audit trail. Operator documentation
   states tested providers, propagation bounds, retention requirements, recovery
   steps, and unsupported-provider limitations.

The phase is complete only after implementation, tests, and operator documentation
meet these criteria. Selecting the contract does not mark the feature complete.
