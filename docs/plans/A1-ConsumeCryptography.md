# A1 — AuditCore consumes MillWorks.Cryptography (extract inline crypto)

**Status:** Authored; **gated on MillWorks.Cryptography C2** (needs `ISigner`/`ISigningKeyProvider`) and C0 (`IHasher`,
RFC 8785 canonicalizer). Subplan for the AuditCore row of `MillWorks/CryptographyConsolidation-Orchestration.md` (A1).
**Created:** 2026-06-28. Plan only — no code here.

## Goal

Stop hand-rolling crypto inside `TamperDetectionService`. Delegate hashing/signing/key-resolution to the shared
`MillWorks.Cryptography` primitives, while **keeping AuditCore's chain orchestration** (the append-lock, sequence allocation,
previous-hash fetch, persistence) exactly where it is. This is the trickiest extraction in the program — the crypto is
*inlined inside the DB critical section*.

> This makes AuditCore take its **first upstream MillWorks dependency** (`MillWorks.Cryptography.*`). That package is held to
> standalone publishable quality precisely so this is acceptable (same posture as depending on the Azure KeyVault SDK).

## What moves vs. what stays

| Concern | Today (inline in `TamperDetectionService`) | After A1 |
| --- | --- | --- |
| Event hash (`ComputeEventHash`) | `IncrementalHash` SHA-256 inline | `IHasher.Sha256` (C0) |
| HMAC (`ComputeHmac`) | `IncrementalHash` HMAC inline, key from `AuditOptions.HmacKey` | `ISigner` (HMAC) over C1 `ISigningKeyProvider` |
| RSA-PSS digital signature (`CreateDigitalSignatureAsync` / `GetOrLoadSigningKey`) | inline `RSA` + PEM file paths + static cache | `ISigner` (RSA-PSS) over C1 `ISigningKeyProvider` |
| Canonical JSON (`AuditCanonicalizer`) | bespoke deterministic serializer | **optional** adopt C0 RFC 8785 byte canonicalizer (see §Decisions) |
| **Chain orchestration** (lock, sequence, previousHash, persist) | `TamperDetectionService` + `IntegrityWriteBatcher` | **UNCHANGED — stays in AuditCore** |
| Field encryption key provider (`IEncryptionKeyProvider`) | AuditCore's own KeyVault/file providers | **A2** (optional reconcile onto C1's encryption provider) — out of A1 scope |

## Implementation outline (plan)

1. Add package refs: `MillWorks.Cryptography.Abstractions` + `MillWorks.Cryptography` (+ `.KeyVault`/`.FileSystem` matching the
   current key config). Register via `AddMillWorksCryptography*` in the AuditCore composition.
2. Inject `IHasher`, `ISigner`, `ISigningKeyProvider` into `TamperDetectionService`; replace the private
   `ComputeEventHash`/`ComputeHmac`/`CreateDigitalSignatureAsync`/`GetOrLoadSigningKey` bodies with calls to them. **Keep the
   call sites where they are** — HMAC/signature are still computed *inside* the append-lock (they bind chain position); only
   the primitive implementation moves out.
3. Retire `AuditOptions.HmacKey` + `SecurityOptions.DigitalSignaturePrivateKeyPath/PublicKeyPath` as the *source of keys* —
   keys now come from `ISigningKeyProvider` (the host wires the backend). Keep the option shells only if other paths use them.
4. Remove the EF-entity overloads of the hash methods (`AuditEventEntity`/`AuditIntegrityEntity`) — callers extract primitive
   values, then call `IHasher`/`ISigner`. (This is the coupling the audit flagged.)
5. (Optional, §Decisions) Point `AuditCanonicalizer` at C0's RFC 8785 byte canonicalizer.

## Decisions for this subplan
- **Adopt the shared canonicalizer?** AuditCore's audit-event chain hash is *internal* tamper-evidence (verified by AuditCore,
  not re-derived by a regulator), so adopting RFC 8785 is **optional** (§6.3 of the Cryptography orchestration). Adopting gives
  one canonical form platform-wide; it **changes the chain hash values** — fine on greenfield (no data), update golden test
  values. Recommend: adopt, for one verifiable form. (Confidence ~70% — leave-as-is is defensible since it's internal.)
- **Distributed lock:** AuditCore's `IAuditDistributedLockService` + `sp_getapplock` is the cross-repo decision from
  `LockingConsolidation-Orchestration.md` — consume `MillWorks.BackgroundJobs.Core.Abstractions.IDistributedLockManager` vs
  keep its own. Decide alongside A1 (AuditCore touches both). Recommend: keep its own unless AuditCore is already pulling
  BackgroundJobs for another reason (standalone-package posture).
- **A2 (field encryption providers):** reconciling AuditCore's `IEncryptionKeyProvider` onto C1's encryption provider is a
  **separate, optional** follow-up — not required for A1.

## Tests
- **Regression first:** the existing tamper-chain / integrity / digital-signature suites and the 100k-event endurance soak
  must stay green. If the canonicalizer is adopted, update the golden hash values (greenfield — no stored chains to honor).
- Sign/verify now route through `ISigner`/`IVerifier`; assert the chain still verifies end-to-end.
- A behavioral test that no key paths/material are read from `AuditOptions`/`SecurityOptions` anymore (keys come from the provider).

## Non-goals
Rewriting chain orchestration · SecurityEvent integrity (**shelved** — superseded by MillWorks.Security; do not build) · A2
encryption-provider reconcile · the locking move itself (decided here, executed per `LockingConsolidation-Orchestration.md`).

## Done when
- [ ] `TamperDetectionService` computes hash/HMAC/RSA via `MillWorks.Cryptography`; **chain orchestration unchanged**.
- [ ] Keys resolve via `ISigningKeyProvider`; no PEM paths / `HmacKey` read for chain signing.
- [ ] EF-entity hash overloads removed.
- [ ] Existing tamper-chain + soak tests green (golden hashes updated iff the canonicalizer was adopted).
- [ ] Canonicalizer and distributed-lock decisions recorded; A2 noted as a separate follow-up.
