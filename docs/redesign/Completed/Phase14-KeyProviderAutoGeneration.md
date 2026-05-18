# Phase 14 — File-Based Key Provider Auto-Generation Control

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)

## Problem

`FileBasedKeyProvider` silently creates a brand-new key version whenever `current-version.txt` is missing. This is convenient in development but dangerous in production:

- Storage corruption → silent key rotation instead of hard failure
- Mount mistakes → new key, old ciphertext undecryptable
- Partial restores → data loss with no operator acknowledgement

**Severity:** Medium

**References:**
- `src/MillWorks.AuditCore.Services/Providers/FileBasedKeyProvider.cs:111` — async path
- `src/MillWorks.AuditCore.Services/Providers/FileBasedKeyProvider.cs:171` — sync path
- `src/MillWorks.AuditCore.Services/Providers/FileBasedKeyProvider.cs:241` — `InitializeFirstKeyAsync`

## Goal

Add explicit control over auto-key-generation behavior. Default to **disallowed** (fail-safe), with an opt-in flag for development/initial setup scenarios.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply.

## Files

| Action | Path | Purpose |
|---|---|---|
| Edit | `src/MillWorks.AuditCore.Services/Providers/FileBasedKeyProvider.cs` | Honor flag, throw if missing key and flag is false |
| Edit | `src/MillWorks.AuditCore.AspNetCore/Configuration/EncryptionConfigurationExtensions.cs` | Flow explicit auto-generation control into the file-based provider registration |
| Edit | `README.md` | Document the safer default and dev/bootstrap opt-in |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Encryption/EncryptionKeyProviderTests.cs` | Update tests that currently assume default auto-generation |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/Encryption/FileBasedKeyProviderSecurityTests.cs` | Add explicit coverage for both allow/deny modes |
| Edit | `tests/MillWorks.AuditCore.Tests/AspNetCore/EncryptionConfigurationExtensionsTests.cs` | Verify builder registration still resolves the provider after signature change |

## Design

### Constructor-Level Control

```csharp
public FileBasedKeyProvider(
    string keyStorePath,
    string masterKeyBase64,
    ILogger<FileBasedKeyProvider> logger,
    bool allowAutoKeyGeneration = false)
```

Add a private field:

```csharp
private readonly bool _allowAutoKeyGeneration;
```

This keeps the control local to the only provider that needs it and avoids
inventing a new options type solely for file-based key bootstrap behavior.

### FileBasedKeyProvider Changes

Update `GetCurrentKeyVersionAsync`:

```csharp
if (!File.Exists(versionFilePath))
{
    if (!_allowAutoKeyGeneration)
    {
        throw new KeyProviderException(
            $"Key version file not found at {versionFilePath}. " +
            "Provision encryption keys explicitly or enable auto-generation only for development/bootstrap scenarios.");
    }

    _logger.LogWarning(
        "Auto-generating initial encryption key because current-version.txt is missing. " +
        "This should only happen in development or initial bootstrap.");

    return await InitializeFirstKeyAsync(cancellationToken);
}
```

Same pattern for sync `GetCurrentKeyVersion()`.

### Builder API Flow

Update the public registration surface so callers can opt in intentionally:

```csharp
public MillWorksAuditBuilder UseFieldEncryptionWithFileStorage(
    string keyStorePath,
    string masterKeyBase64,
    bool allowAutoKeyGeneration = false)
```

That method should pass the flag through when constructing
`FileBasedKeyProvider`.

### Documentation Impact

Because the behavior is intentionally safer and breaking for dev/test
bootstrapping, update `README.md` to show both postures clearly:

- **Production/default:** pre-provision keys; auto-generation remains off.
- **Development/bootstrap:** explicitly pass `allowAutoKeyGeneration: true`.

### Test Updates

Tests that currently rely on auto-generation will need to either:
1. Construct `FileBasedKeyProvider(..., allowAutoKeyGeneration: true)`, or
2. Pre-create keys in test setup

Recommend option 1 for tests that are intentionally exercising bootstrap/dev
behavior, and option 2 for tests that are validating steady-state runtime
behavior.

## Decisions Left to Jesse

1. **Default value.** Proposing `false` (fail-safe). Alternative: `true` for backwards compatibility, require explicit `false` in production. **Recommendation:** default `false` — breaking change is acceptable for security improvement; existing prod deployments already have keys.

2. **Log level for auto-gen.** Proposing `Warning`. Alternative: `Information` (less alarming). **Recommendation:** `Warning` — it should stand out if happening unexpectedly.

3. **Public API surface for opt-in.** Should the opt-in live only on the
   `FileBasedKeyProvider` constructor, or also on
   `UseFieldEncryptionWithFileStorage(...)`? **Recommendation:** both. If the
   builder API does not expose it, callers using only `AddMillWorksAudit(...)`
   cannot intentionally enable bootstrap behavior.

## Verification

```bash
dotnet build MillWorks.AuditCore.sln
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~EncryptionKeyProviderTests|FullyQualifiedName~FileBasedKeyProviderSecurityTests|FullyQualifiedName~EncryptionConfigurationExtensionsTests"
```

### Test Cases

1. **Missing key + auto-generation disabled throws** — no version file, flag off, verify `KeyProviderException`.
2. **Missing key + auto-generation enabled auto-creates** — no version file, flag on, verify key files are created and a version is returned.
3. **Existing key + either flag works** — pre-existing version file, verify key loaded regardless of flag value.
4. **Warning logged on auto-gen** — verify a warning is emitted when bootstrap generation occurs.
5. **Builder API passes the flag through** — `UseFieldEncryptionWithFileStorage(..., allowAutoKeyGeneration: true)` still registers a resolvable `FileBasedKeyProvider`.

## Out of Scope

- HSM integration (different provider entirely)
- Key rotation ceremonies (operational concern)
- Multi-region key distribution (infrastructure concern)
- Provisioning CLI / bootstrap tooling (separate feature if needed)

## Done When

- `FileBasedKeyProvider` accepts explicit auto-generation control and defaults it to `false`
- `UseFieldEncryptionWithFileStorage(...)` exposes the same control for builder-based registration
- `FileBasedKeyProvider` throws `KeyProviderException` when key missing and auto-generation is disabled
- `FileBasedKeyProvider` logs warning and auto-creates when auto-generation is enabled
- Existing encryption and builder-registration tests cover the new behavior and pass
- `README.md` documents the safer default and explicit bootstrap opt-in
- `dotnet build` clean
