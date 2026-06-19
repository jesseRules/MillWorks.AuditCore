# Tamper Detection Integrity Gaps

**Status:** Implemented (2026-06-09)
**Date:** 2026-06-09 (code review)
**Scope:** `TamperDetectionService`, `AuditIntegrityRepository`, `AuditCanonicalizer` verify path, GDPR anonymization vs. the hash chain

## Implementation Summary

Addressed findings #1, #2, #4, #6, #7, #8, #9, #10 (partial). Deferred #3 (head anchor) to Merkle pipeline, #5 (GDPR re-chaining) as requiring schema addition.

Key changes:
- **Algorithm v3**: HMAC and digital signatures now include chain metadata (eventHash, previousHash, sequenceNumber, trustedTimestamp). Uses length-prefixing to prevent concatenation ambiguity.
- **Checksum**: Now uses length-prefixed format.
- **Digital signatures**: Changed from PKCS#1 v1.5 to PSS padding.
- **Missing event detection**: Chain verification now treats null AuditEvent navigation as tamper evidence.
- **Boundary validation**: Added `ValidateIntegrityChainWithDetailsAsync` that validates requested range boundaries.
- **Exception safety**: Malformed JsonData now caught per-event and recorded as tamper finding.
- **Timestamp ordering**: TrustedTimestamp now captured inside the append lock.
- **Constant-time comparison**: Hash verification uses `CryptographicOperations.FixedTimeEquals`.
- **Truncation safety**: `AuditSecurityEventService` now uses `TruncateSafe` to avoid splitting surrogate pairs.
- **Key derivation**: Fixed domain-separation collision between versioned and unversioned field keys.
- **Batcher short-circuit**: `IntegrityWriteBatcher` now checks for existing integrity records before attempting creation.

## Problem

The audit hash chain is the core tamper-evidence claim of AuditCore. Review found that the keyed primitives (HMAC, digital signature) do not cover the fields that matter, several deletion/truncation scenarios verify clean, and one compliance feature (GDPR anonymization) is guaranteed to either fire false tamper alerts or prove that verification is not running. Related: `SecurityEventIntegrity.md` covers the separate `SecurityEvents` table; this document is about the main `AuditEvents` chain.

## Findings

### 1. HMAC and checksum do not cover the fields that matter (Critical)

`TamperDetectionService.cs:787-817`

```csharp
var dataToSign = $"{eventId}|{eventType}|{dateString}";          // ComputeHmac
var criticalFields = $"{eventId}{eventType}{userId}";            // ComputeChecksum
```

Neither covers `User`, `JsonData`, `PreviousEventHash`, `SequenceNumber`, or `TrustedTimestamp`. The only check that covers content is `ComputeEventHash` — an **unkeyed** SHA-256 anyone can recompute. With digital signatures disabled (the default), an attacker with DB write access can modify event k's `JsonData`/`User`, recompute `EventHash`, update row k and row k+1's `PreviousEventHash`, and every verification path passes. The HMAC key — the system's only secret — protects almost nothing.

**Fix:** HMAC the canonical event content plus chain metadata, e.g. `$"{eventHash}|{previousHash}|{sequenceNumber}|{trustedTimestamp}"`.

### 2. Digital signature binds only `EventHash`, not chain position (High)

`TamperDetectionService.cs:823-836`

The RSA signature signs only the event hash. `SequenceNumber`, `PreviousEventHash`, and `TrustedTimestamp` are unsigned, so even with signatures enabled an attacker can swap two events' sequence numbers and re-link `PreviousEventHash` values to reorder history, or re-anchor a truncated chain, without invalidating any signature.

**Fix:** Sign the tuple (eventHash, previousHash, sequenceNumber, trustedTimestamp). Also: prefer `RSASignaturePadding.Pss` over `Pkcs1` for a new design, and note that decrypted private-key `RSAParameters` are cached indefinitely in a static dictionary and can never be zeroed.

### 3. Tail truncation is undetectable — the chain has no head anchor (High)

`TamperDetectionService.cs:577-671`

Deleting the last N integrity rows plus their audit events leaves a contiguous sequence 1..k and an intact chain; both `VerifyChainIntegrityAsync` and `VerifySequenceIntegrityAsync` (gap check only) pass. This is the canonical "cover your tracks" attack on an audit log. The planned Merkle/anchoring pipeline (`NextStepsDocument.md`) addresses this long-term.

**Interim fix:** Periodically persist (head hash, max sequence) to an external/WORM location and verify against it.

### 4. Chain verification silently skips events whose audit-event row was deleted (High)

`TamperDetectionService.cs:620-633`

`if (integrity.AuditEvent != null) { ...verify... }` — if the `AuditEvents` row is deleted but the integrity row remains, the chain stays intact and the null navigation causes per-event verification to be skipped entirely. The event's content is gone, no `TamperedEvent` is recorded, and `IsValid = true`.

**Fix:** Treat `integrity.AuditEvent == null` as a tamper finding ("audit event missing for integrity record").

### 5. GDPR anonymization rewrites hashed fields without touching integrity records (High)

`AuditComplianceService.cs:113-169`

`AnonymizeUserDataAsync` overwrites `User` and `JsonData`, both inputs to `ComputeEventHash`. Every anonymized event will fail hash verification and fire Critical `AuditTamperAlert` security events, indistinguishable from real tampering. There is no re-chaining or annotation path.

**Fix:** Record a signed "anonymization supersession" event and update/version the integrity record inside the same transaction, so verification can distinguish lawful erasure from tampering.

### 6. `ValidateIntegrityChainAsync` cannot detect truncation at range boundaries (Medium)

`AuditIntegrityRepository.cs:67-98`

The method validates linkage only between rows that exist. It never checks `records[0].SequenceNumber == startSequence` or `records[^1].SequenceNumber == endSequence`, and an empty result returns `true`. Deleting rows 1–4, or all rows in the requested range, passes `ValidateIntegrityChainAsync(1, 10)`.

**Fix:** Validate boundary sequence numbers against the requested range, and return a result that distinguishes "no data" from "valid."

### 7. One malformed `JsonData` row aborts entire chain verification (High)

`AuditCanonicalizer.cs:58` (`JsonDocument.Parse` with no error handling), caller `TamperDetectionService.cs:531, 603-636, 772`

If a row's `JsonData` has been corrupted/truncated to unparseable text — the very tampering this system detects — `JsonException` propagates out of the per-event verification loop, crashing the whole run with no `TamperedEvent` recorded. Corrupting one row to invalid JSON is a verification-DoS rather than a detected tamper.

**Fix:** Document the throw on `Canonicalize` (or add `TryCanonicalize`), and have the verify paths catch `JsonException` per event and record it as a tamper finding.

### 8. Non-monotonic `TrustedTimestamp` vs. date-windowed chain verification (Medium)

`TamperDetectionService.cs:164, 336, 595-642`

The timestamp is captured before the append lock, so two concurrent appends can commit with sequence order opposite their timestamps. `VerifyChainIntegrityAsync` filters its window by `TrustedTimestamp` but checks linkage on consecutive in-window records; a timestamp inversion at the window boundary excludes a middle record and produces a spurious "Chain discontinuity" Critical alert.

**Fix:** Capture the timestamp inside the lock, or window verification by sequence number and only flag when `SequenceNumber` is contiguous.

### 9. Hash-input concatenation is ambiguous (Low)

`TamperDetectionService.cs:763-772, 808`

Adjacent variable-length fields joined with `|` (or nothing, in `ComputeChecksum`) mean two different events can produce identical hash input: (`"A|B"`, `"C"`) vs (`"A"`, `"B|C"`). Length-prefix the fields or hash a canonical JSON envelope of the top-level fields, as is already done for `JsonData`.

### 10. Hygiene items (Low)

- HMAC/hash verification uses `!=` string comparison (`TamperDetectionService.cs:532, 544, 554`); use `CryptographicOperations.FixedTimeEquals` on decoded bytes.
- Tamper alerts are fire-and-forget and log-only on failure (`TamperDetectionService.cs:946-975`): if the security-event DB write fails — plausible in exactly the scenarios where tampering occurs — the Critical alert reduces to an app log line. Verification still returns `false`, so detection isn't lost, but consider routing `AuditTamperAlert` writes through the DLQ/outbox as a fallback.
- `AuditSecurityEventService` truncates `Message` with `[..MaxMessageLength]` (`AuditSecurityEventService.cs:63`), which can split a surrogate pair; use the existing `TruncateSafe` helper.
- `FieldKeyDerivation` overloads have a domain-separation collision (`FieldKeyDerivation.cs:28, 48`): field `"X:version:1"` (unversioned) derives the same key as field `"X"` version `"1"`. Length-prefix or use distinct labels per overload. Similarly the AEAD AAD `$"{version}|{keyVersion}|{fieldName}"` is pipe-ambiguous.
- A flush retry in `IntegrityWriteBatcher` that overlaps a reconciler which just created the integrity record will burn all 10 duplicate-key retries and fail callers even though integrity exists; short-circuit on "already exists."

## Implementation Outline

1. Rework the signed/keyed inputs (#1, #2, #9) together — they share the canonical-input change. This invalidates existing HMACs/signatures; greenfield policy applies (no back-compat shims), but verification tests must be regenerated.
2. Add missing-row and boundary detection (#4, #6) with tests that delete an `AuditEvents` row, delete a tail range, and delete an interior integrity row.
3. Make per-event verification exception-safe (#7) with a corrupted-JSON fixture.
4. Fix the anonymization/integrity interaction (#5) — design the supersession record first; it likely needs a schema addition.
5. Apply the timestamp/window fix (#8) and the low-severity hygiene items (#10).
6. Track the head-anchor gap (#3) explicitly in the Merkle pipeline plan so the interim WORM anchor isn't forgotten.

## Non-Goals

- Implementing the full Merkle batching pipeline (tracked separately in `NextStepsDocument.md`).
- `SecurityEvents` table integrity (tracked in `SecurityEventIntegrity.md` / `SecurityEventHardeningRoadmap.md`).
