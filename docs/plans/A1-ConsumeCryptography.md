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
- [x] `TamperDetectionService` computes hash/HMAC/RSA via `MillWorks.Cryptography`; **chain orchestration unchanged**.
- [x] Keys resolve via `ISigningKeyProvider`; no PEM paths / `HmacKey` read for chain signing.
- [x] EF-entity hash overloads removed.
- [x] Existing tamper-chain + soak tests green (golden hashes updated iff the canonicalizer was adopted — it was **not**; see below).
- [x] Canonicalizer and distributed-lock decisions recorded; A2 noted as a separate follow-up.

---

## Outcome — A1 implemented (2026-06-28)

AuditCore now takes its first upstream MillWorks dependency (`MillWorks.Cryptography` on `.Services`,
`MillWorks.Cryptography.FileSystem` on `.AspNetCore`, both `0.1.0` from the `MillWorksLocal` feed; `.Abstractions`
arrives transitively). No dependency cycle — Cryptography references nothing in AuditCore.

**Delegated (extracted to `MillWorks.Cryptography`):**
- `ComputeEventHash` / `ComputeChecksum` → build the same byte projection (kept as domain logic) and call `IHasher.Sha256`.
- The chain-binding **HMAC** → `HmacSha256Signer` (`ISigner`/`IVerifier`) over an `ISigningKeyProvider`.
- The optional **RSA-PSS** digital signature → `RsaPssSigner` over a *separate* `ISigningKeyProvider`.
- Constant-time compares → `ConstantTime.EqualsBase64`; Base64 → `CryptoEncoding`; secure random (DI dev master key) → `ISecureRandom`.
- Removed: inline `IncrementalHash`/`HMACSHA256`/`RSA`/`RandomNumberGenerator`/`FixedTimeEquals`, the static PEM/RSA caches,
  `GetOrLoad{Signing,Verify}Key`, `GenerateDefaultHmacKey`, `ResetKeyCachesForTests`, and the `AuditEventEntity` hash overloads.

**Kept in AuditCore (unchanged):** the chain orchestration — `sp_getapplock` serialization, sequence allocation,
previous-hash linkage, the atomic event+integrity transaction, persistence, and the retry/DLQ path; plus the
length-prefixed field projection (which fields, the framing). `AuditCanonicalizer` is **untouched**.

**Key model (decision: persist KeyId — rotation-safe):** the HMAC and RSA signers resolve the *active* signing key via
`ISigningKeyProvider`, and each integrity row now persists the producing key id (`AuditIntegrityEntity.HmacKeyId`,
`DigitalSignatureKeyId`; additive migration `20260628213010_AddIntegritySigningKeyIds`). Verification rebuilds the exact
`SignatureEnvelope` and reselects that key id, so verification is unambiguous and survives signing-key rotation.

**Integrity-key backend (decision: FileSystem default, swappable):** `MillWorksAuditBuilder.UseSecurity` wires
`AddMillWorksCryptography()` + two disjoint file-system `ISigningKeyProvider`s (HMAC under `…/hmac`, RSA-PSS under `…/rsa`)
+ `HmacSha256Signer`/`RsaPssSigner` (all `TryAdd`, so a host can override with a KeyVault-backed signer). New
`SecurityOptions.IntegrityKeyStorePath` / `IntegrityKeyMasterKeyBase64` / `AllowIntegrityKeyAutoGeneration`. The
"HMAC key required in Production" rule moved from `AuditOptions`/the ctor to the key backend: **Production fails closed**
when no `IntegrityKeyMasterKeyBase64` is configured; non-Production uses a process-ephemeral master key + temp store
(warned; signatures do not survive a restart). The RSA backend is built only when `EnableDigitalSignatures` is on.

**Retired options:** `AuditOptions.HmacKey` and `SecurityOptions.DigitalSignaturePrivateKeyPath/PublicKeyPath` (and the
HmacKey-in-Production / HmacKey-with-DigitalSignatures validators) — they were the *source of keys*, which now comes from
the provider.

**Canonicalizer decision:** `AuditCanonicalizer` was **NOT** swapped to the RFC 8785 byte primitive (§6.3 optional; it is
internal tamper-evidence verified only by AuditCore). The event-hash and checksum byte projections are byte-identical to
before, so those golden values are unchanged. **HMAC and RSA signature values do change** (the key source moved from
config to the provider) — no stored chains exist (greenfield), and tests recompute/round-trip rather than pin literals.

**Distributed-lock decision:** AuditCore keeps its own `IAuditDistributedLockService` + `sp_getapplock` (not consuming
`MillWorks.BackgroundJobs`'s lock manager) — standalone-package posture; revisit per `LockingConsolidation-Orchestration.md`
only if AuditCore pulls BackgroundJobs for another reason.

**Key-usage isolation (guard test):** `IntegrityKeyUsageIsolationTests` boots the real DI and asserts the HMAC and RSA
integrity keys come from disjoint key spaces (distinct key ids; neither verifies the other's envelope) and that
`TamperDetectionService` takes no `IEncryptionKeyProvider` / AEAD dependency (cannot cross-route an encryption key).

**Tests:** library builds clean (0 warn/err). Crypto-affected unit fixtures (TamperDetection*, OptionsFlow,
IntegrityKeyUsageIsolation, hash property) and the SQLite tamper integration fixtures are green. SQL Server Testcontainers
lane uses `mssql/server:2022` (amd64) — see the run note in the session outcome for arm64-emulation status.

**A2 — DONE 2026-06-28 (see [A2-ConsumeCryptographyEncryption.md](A2-ConsumeCryptographyEncryption.md)):** reconciled
`FieldEncryptionService` + `FileBasedKeyProvider` + `AzureKeyVaultProvider` + AuditCore's own
`FieldKeyDerivation`/`IEncryptionKeyProvider` onto Cryptography's `IAeadCipher` (canonical `[ver][nonce][tag][ct]` frame,
replacing the `ENC_V1:` JSON frame inside an `ENC2:` envelope), `IEncryptionKeyProvider`, internal `FieldKeyDerivation`,
and `AeadContext.ForKey` for the `scope|keyVersion|fieldName` AAD binding. AuditCore's own
`KeyProviderException`/`IEncryptionKeyProvider` (the `.Abstractions` name collisions) were deleted as part of A2.
