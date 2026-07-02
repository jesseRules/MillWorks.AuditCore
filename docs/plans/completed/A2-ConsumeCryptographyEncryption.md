# A2 — AuditCore field encryption consumes MillWorks.Cryptography (reconcile encryption providers)

**Status:** **DONE 2026-06-28.** Follow-on to [A1](A1-ConsumeCryptography.md) and the AuditCore row of
`MillWorks/CryptographyConsolidation-Orchestration.md` (A2). A1 reconciled the integrity **signing** path;
A2 reconciles the field **encryption** path onto the same shared library.

## Goal

Stop hand-rolling AES-256-GCM field encryption and its key storage inside AuditCore. Delegate the cipher and
the encryption-key provider (file-system + Key Vault, HKDF field-derivation, rotation/versioning) to the
shared `MillWorks.Cryptography` primitives, while keeping AuditCore's domain layer — the
`IFieldEncryptionService` contract and the EF value-converter seam — exactly where it is.

This collapses the duplicate AES-GCM cipher and the duplicate file/Key-Vault key stores that A1 flagged, and
removes the name collision between AuditCore's own `IEncryptionKeyProvider` / `KeyProviderException` and
Cryptography's.

## What moves vs. what stays

| Concern | Before (AuditCore-owned) | After |
| --- | --- | --- |
| AES-256-GCM cipher | inline `AesGcm` in `FieldEncryptionService` | `IAeadCipher` (`AesGcmCipher`) over the canonical `[version:1][nonce:12][tag:16][ciphertext]` frame |
| Encryption-key provider | `IEncryptionKeyProvider` (AuditCore) + `FileBasedKeyProvider` + `AzureKeyVaultProvider` | `MillWorks.Cryptography.KeyManagement.IEncryptionKeyProvider` + `FileEncryptionKeyProvider` / `AzureKeyVaultEncryptionKeyProvider` |
| Field-key HKDF derivation | AuditCore `FieldKeyDerivation` (public helper) | Cryptography-internal `FieldKeyDerivation` (provider returns an already-derived `KeyMaterial`) |
| Stored frame | `ENC_V1:` + Base64(JSON `{Version,KeyVersion,Nonce,Ciphertext,Tag,FieldName,EncryptedAt}`) | `ENC2:` + Base64(`[envVersion:1][keyVersionLen:2 BE][keyVersion][AEAD frame]`) |
| Metadata authentication (AAD) | hand-built `version|keyVersion|fieldName` pipe string | `AeadContext.ForKey(scope, keyVersion, fieldName)` (length-prefixed) |
| Key-provider exception | AuditCore `KeyProviderException` | Cryptography `KeyProviderException` (`: CryptographyException`) |
| **Domain layer** (`IFieldEncryptionService`, `EncryptedValueConverter`, `ModelBuilderEncryptionExtensions`, `EncryptedFieldAttribute`/`SensitiveDataAttribute`, `FieldEncryptionException`) | AuditCore | **UNCHANGED — stays in AuditCore** |

## Decisions

1. **Delete AuditCore's provider stack (not a thin adapter).** AuditCore's own `IEncryptionKeyProvider`,
   `FileBasedKeyProvider`, `AzureKeyVaultProvider`, `KeyProviderException`, `FieldKeyDerivation`, and the
   `EncryptedFieldPayload` JSON DTO are **removed**. `FieldEncryptionService` consumes Cryptography's
   `IEncryptionKeyProvider` + `IAeadCipher` directly. This is the full collapse the orchestration calls for
   ("no parallel storage backend remains"); greenfield, so no migration.

2. **`FieldKeyDerivation` is Cryptography-internal, not AuditCore domain logic.** The Cryptography providers
   derive the per-field key (HKDF-SHA256) inside `GetEncryptionKeyAsync` and hand back a ready 32-byte
   `KeyMaterial`. AuditCore no longer derives keys itself, so its public `FieldKeyDerivation` helper is
   deleted (this resolves the open sub-decision in the orchestration row).

3. **Key scope = `KeyScope.Global` for AuditCore field encryption.** The encryption seam is the EF value
   converter, bound at model-build time with no per-row tenant context, and `IFieldEncryptionService` takes
   only `(plainText, fieldName)`. So this consumer uses the global key ring. The Cryptography provider is
   tenant-capable (per orchestration §6.6) for future use; AuditCore's converter-based consumption is
   deliberately global. (No regression — the old AuditCore providers had no tenant concept at all.)

4. **Storage envelope carries the key version; field+version+scope are AAD-bound.** The canonical AEAD frame
   carries no key id, but decryption must resolve the producing key version (rotation), so the `ENC2`
   envelope length-prefixes the key version outside the frame. The field name, key version, and scope are
   bound into the AEAD associated data, so a cross-field or cross-version swap fails GCM authentication —
   a cryptographically enforced check that replaces (and strengthens) the previous stored-`FieldName` string
   compare. The `"ENC2:"` sentinel is the storage marker `IsEncrypted` keys off (so the converter never
   double-encrypts).

5. **DI mirrors A1: construct the provider directly, register `TryAdd`.** `EncryptionConfigurationExtensions`
   builds `FileEncryptionKeyProvider` / `AzureKeyVaultEncryptionKeyProvider` directly (not via the bundled
   `AddMillWorksCryptographyFileSystem`/`KeyVault` extensions, which would also `TryAdd` an unused
   `ISigningKeyProvider`). This keeps the encryption key space disjoint and intentional, matching how A1
   wired its integrity signing providers. The encryption provider is `TryAdd`, so a host can register its own
   `IEncryptionKeyProvider` before `AddMillWorksAudit` to override the backend.

## Sync boundary (known, accepted)

The EF value converter is synchronous by EF's design, so `IFieldEncryptionService.EncryptField` /
`DecryptField` (the converter call path) run sync-over-async over the now async-only Cryptography key
provider. The file-system provider caches its master key, so steady-state this is CPU-bound HKDF, not I/O; the
only real blocking is the first master-key read. The concrete `FieldEncryptionService` no longer overrides the
sync methods — it relies on the interface's default sync-over-async members.

## Outcome — A2 implemented (2026-06-28)

**Delegated (extracted to `MillWorks.Cryptography`):**
- The AES-256-GCM cipher → `IAeadCipher` / `AesGcmCipher` over the canonical frame.
- Field encryption-key resolution + HKDF derivation + rotation/versioning + at-rest master-key wrapping →
  `IEncryptionKeyProvider` (`FileEncryptionKeyProvider` / `AzureKeyVaultEncryptionKeyProvider`).
- AAD construction → `AeadContext.ForKey`. Constant-time/encoding stay where the cipher owns them.

**Deleted from AuditCore (6 production files):** `Abstractions/Interfaces/IEncryptionKeyProvider.cs`,
`Services/Providers/FileBasedKeyProvider.cs`, `Services/Providers/AzureKeyVaultProvider.cs`,
`Services/Providers/KeyProviderException.cs`, `Services/FieldKeyDerivation.cs`, `Services/EncryptedFieldPayload.cs`.

**Kept in AuditCore (unchanged):** `IFieldEncryptionService`, `FieldEncryptionException`,
`EncryptedValueConverter`, `ModelBuilderEncryptionExtensions`, `EncryptedFieldAttribute`,
`SensitiveDataAttribute`, and `AuditDbContext`'s optional `IFieldEncryptionService` dependency.

**Rewritten:** `FieldEncryptionService` (consumes `IEncryptionKeyProvider` + `IAeadCipher`, ENC2 envelope,
AAD binding, `KeyScope.Global`); `EncryptionConfigurationExtensions` (`UseFieldEncryption(keyVaultUrl)` /
`UseFieldEncryptionWithFileStorage(...)` / `UseFieldEncryption(IEncryptionKeyProvider)` construct Cryptography
providers directly). `ReEncryptFieldAsync` wraps sub-failures in the re-encryption context (inner cause
preserved).

**Package refs:** `.AspNetCore` gains `MillWorks.Cryptography.KeyVault` 0.1.0 + `Azure.Security.KeyVault.Secrets`
4.11.0 + `Azure.Identity` 1.21.0 (for the direct `AzureKeyVaultEncryptionKeyProvider` / `SecretClient`
construction; it already had `.FileSystem` from A1). `.Services` drops the now-unused
`Azure.Security.KeyVault.Secrets` + `Azure.Identity` refs (it keeps `Azure.Storage.Blobs` for archival).

**Coverage that moved repos (not dropped):** the provider-internal AuditCore tests
(`AzureKeyVaultProviderTests`, `FileBasedKeyProviderSecurityTests`, `EncryptionKeyProviderTests`,
`FieldKeyDerivationTests`) were deleted — file/Key-Vault storage, rotation, HKDF derivation, and key-file
wrapping are now owned and tested by the `MillWorks.Cryptography` suite. AuditCore's responsibility shrank to
the storage envelope, AAD binding, the `IFieldEncryptionService` boundary, and the DI wiring, which are
covered by the rewritten fixtures. The detailed Key-Vault 404/403 error-mapping the old AuditCore provider had
is now Cryptography's concern.

**Tests:** library builds clean (0 warn/err, full `.AspNetCore` graph). The encryption-affected unit fixtures
are green — `FieldEncryptionServiceTests`, `FieldEncryptionServiceEdgeCaseTests`,
`FieldEncryptionServiceErrorPathTests` (rewritten over a `FakeEncryptionKeyProvider` + the **real**
`AesGcmCipher`, so genuine encryption / AAD binding / tamper detection are exercised),
`EncryptionConfigurationExtensionsTests` (new Cryptography wiring), and the unchanged
`EncryptedValueConverterEdgeCaseTests` / `EncryptionValueConverterTests` (EF converter via an
`IFieldEncryptionService` fake). The A1 guard `IntegrityKeyUsageIsolationTests` stays green — its
`IEncryptionKeyProvider` reference now binds to Cryptography's type and still asserts `TamperDetectionService`
takes no encryption dependency. 89 tests green via positive filters (the SQL Server Testcontainers
`[SetUpFixture]` hang under arm64 emulation is the same pre-existing limit noted in A1; no encryption fixture
needs SQL Server). New shared helper: `tests/.../Helpers/FakeEncryptionKeyProvider.cs` (+ `EncryptionTestHarness`).

**No dependency cycle, no production data:** Cryptography still references nothing in AuditCore. The ENC2
frame replaces `ENC_V1:` outright (greenfield — no stored ciphertext to honor).
