# Phase 12 — Digital Signature Key Cache Isolation

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)

## Problem

The RSA key cache in `TamperDetectionService` is process-global (`static RSAParameters?`), but the key path comes from per-instance `SecurityOptions`. The first keypair loaded wins for every subsequent instance in the process, regardless of configured path.

**Impact:** In multi-tenant hosts, test hosts, or apps that spin up multiple differently-configured service providers, signatures may be created or verified with wrong key material.

**Severity:** High

**References:**
- `src/MillWorks.AuditCore.Services/TamperDetectionService.cs:86` — `_cachedSigningKey`
- `src/MillWorks.AuditCore.Services/TamperDetectionService.cs:865` — `GetOrLoadSigningKey()`
- `src/MillWorks.AuditCore.Services/TamperDetectionService.cs:892` — `GetOrLoadVerifyKey()`

## Goal

Make the RSA key cache keyed by the configured file path, so each unique key path gets its own cached parameters. Multiple service instances with different key paths will use their respective keys.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply:

1. Plan is spec — only the files named below change.
2. No backwards-compat shims.
3. Build + test after every file change.
4. List unresolved decisions before editing — see "Decisions" below.
5. Ambiguity is a stop, not a permission.

## Files

| Action | Path | Purpose |
|---|---|---|
| Edit | `src/MillWorks.AuditCore.Services/TamperDetectionService.cs` | Replace static nullable fields with path-keyed cache |
| Edit | `tests/MillWorks.AuditCore.Tests/Services/TamperDetectionServiceDigitalSignatureTests.cs` | Replace reflection-based cache reset and verify isolation across different key paths |

## Design

### Replace Static Nullable with ConcurrentDictionary

Current (broken):
```csharp
private static RSAParameters? _cachedSigningKey;
private static RSAParameters? _cachedVerifyKey;
```

New (path-keyed):
```csharp
private static readonly ConcurrentDictionary<string, RSAParameters> _signingKeyCache = new();
private static readonly ConcurrentDictionary<string, RSAParameters> _verifyKeyCache = new();
```

### Update GetOrLoadSigningKey

```csharp
private RSAParameters GetOrLoadSigningKey()
{
    var keyPath = _securityOptions.DigitalSignaturePrivateKeyPath;
    if (string.IsNullOrEmpty(keyPath))
    {
        throw new InvalidOperationException(
            "Digital signature private key path is not configured.");
    }

    // Normalize path to handle casing/slash differences
    var normalizedPath = Path.GetFullPath(keyPath);

    return _signingKeyCache.GetOrAdd(normalizedPath, path =>
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Digital signature private key file does not exist: {path}");
        }

        var privateKeyPem = File.ReadAllText(path);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem.ToCharArray());
        return rsa.ExportParameters(true);
    });
}
```

### Update GetOrLoadVerifyKey

Same pattern as signing key, using `_verifyKeyCache` and `DigitalSignaturePublicKeyPath`.

### Remove Lock Field

The `_keyLoadLock` static field becomes unnecessary — `ConcurrentDictionary.GetOrAdd` handles thread safety. Remove it to avoid dead code.

### Add Internal Test Reset Hook

The current test suite clears the private static fields via reflection in
`TamperDetectionServiceDigitalSignatureTests`. Once the implementation moves to
`ConcurrentDictionary<string, RSAParameters>`, keep test isolation explicit and
cheap by replacing that reflection seam with an internal static reset method
on `TamperDetectionService`, for example:

```csharp
internal static void ResetKeyCachesForTests()
{
    _signingKeyCache.Clear();
    _verifyKeyCache.Clear();
}
```

This repository already treats internal-only test seams as acceptable when they
remove brittle reflection from tests.

## Decisions Left to Jesse

1. **Path normalization strategy.** Use `Path.GetFullPath()` before cache lookup. Alternative: use raw path as-is (simpler but risks duplicate cache entries for relative-vs-absolute references to the same file). **Recommendation:** normalize with `GetFullPath`. Do not try to force case normalization in this phase; that would be platform-sensitive and is unnecessary for the primary correctness fix.

2. **Test seam shape.** Keep using reflection from tests, or expose an internal reset hook? **Recommendation:** add the internal reset hook and use it from `TamperDetectionServiceDigitalSignatureTests`; it is less brittle than reflection and stays non-public.

3. **Cache eviction.** Currently no eviction — keys live for process lifetime. This matches current behavior. Alternative: add general runtime eviction/invalidation. **Recommendation:** no runtime eviction in this phase; only the test reset hook above.

## Verification

```bash
dotnet build MillWorks.AuditCore.sln
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~TamperDetectionServiceDigitalSignatureTests"
```

### Test Cases

1. **Two instances with different key paths use correct keys** — create two `TamperDetectionService` instances with different `SecurityOptions`, sign data with each, verify each signature only validates with its corresponding instance.
2. **Path normalization** — verify relative and absolute references to the same PEM file resolve to the same cache entry and continue to sign/verify correctly.
3. **Test isolation reset works** — one test can populate the cache and a later test can safely start from a cleared state without reflection.

## Out of Scope

- Key rotation at runtime (existing limitation, separate feature)
- Distributed key caching across processes (not needed for this fix)

## Done When

- `_cachedSigningKey` and `_cachedVerifyKey` replaced with `ConcurrentDictionary<string, RSAParameters>`
- `_keyLoadLock` removed
- `GetOrLoadSigningKey` and `GetOrLoadVerifyKey` use normalized path as cache key
- `TamperDetectionServiceDigitalSignatureTests` covers multi-path isolation and passes
- Existing digital signature tests still pass
- `dotnet build` clean
