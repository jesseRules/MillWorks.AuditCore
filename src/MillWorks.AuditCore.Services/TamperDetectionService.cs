using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Canonicalization;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;
using MillWorks.Cryptography;
using MillWorks.Cryptography.Hashing;
using MillWorks.Cryptography.KeyManagement;
using MillWorks.Cryptography.Signing;

namespace MillWorks.AuditCore.Services.TamperDetection;

/// <summary>
/// Tamper detection service for audit events. Cross-process serialization of the
/// hash-chain append happens via a SQL Server <c>sp_getapplock</c> bound to the
/// write transaction (see <see cref="IAuditIntegrityRepository.AcquireAppendLockAsync"/>).
/// </summary>
/// <remarks>
/// The cryptographic primitives are delegated to <c>MillWorks.Cryptography</c>: SHA-256 event hash
/// and checksum via <see cref="IHasher"/>; the chain-binding HMAC via an HMAC <see cref="ISigner"/>;
/// the optional RSA-PSS digital signature via an RSA-PSS <see cref="ISigner"/>. Both signers resolve
/// their key material from an <see cref="ISigningKeyProvider"/> backend wired by the host composition —
/// the integrity keys never come from configuration and never cross-route through an encryption-key
/// provider. The hash-chain orchestration (append-lock, sequence allocation, previous-hash linkage,
/// atomic persistence, retry/DLQ) stays here unchanged; only the primitive implementations moved out.
/// The length-prefixed field projection that binds each event to its chain position is audit-domain
/// logic and likewise stays here.
/// </remarks>
public sealed class TamperDetectionService : ITamperDetectionService
{
    /// <summary>The tenant scope for integrity signing keys. Audit integrity is a single global chain.</summary>
    private static readonly KeyScope IntegrityKeyScope = KeyScope.Global;

    /// <summary>
    /// Audit event repository
    /// </summary>
    private readonly IAuditEventRepository _auditEventRepository;

    /// <summary>
    /// Audit integrity repository
    /// </summary>
    private readonly IAuditIntegrityRepository _auditIntegrityRepository;

    /// <summary>
    /// Security event service
    /// </summary>
    private readonly IAuditSecurityEventService _securityEventService;

    /// <summary>
    /// Logger
    /// </summary>
    private readonly ILogger<TamperDetectionService> _logger;

    /// <summary>
    /// Raw SHA-256 hashing for the event hash and checksum.
    /// </summary>
    private readonly IHasher _hasher;

    /// <summary>
    /// HMAC-SHA-256 signer/verifier for the chain-binding integrity MAC. Resolves its symmetric key via
    /// <see cref="ISigningKeyProvider"/>; the resolved key id is persisted per row so verification
    /// reselects the exact key (rotation-safe).
    /// </summary>
    private readonly HmacSha256Signer _hmacSigner;

    /// <summary>
    /// RSA-PSS signer/verifier for the optional digital signature, or <c>null</c> when digital signatures
    /// are disabled. When non-null it resolves an RSA private key via its own <see cref="ISigningKeyProvider"/>
    /// — a key space disjoint from the HMAC key. Its presence is the single source of truth for whether
    /// digital signatures are enabled.
    /// </summary>
    private readonly RsaPssSigner? _signatureSigner;

    /// <summary>
    /// Time provider for testable timestamps
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>True when digital signatures are enabled (an RSA-PSS signer was supplied).</summary>
    private bool DigitalSignaturesEnabled => _signatureSigner is not null;

    /// <summary>
    /// Process-local serializer used only when the active EF provider does not support
    /// <c>sp_getapplock</c> (e.g. SQLite in tests). On SQL Server the cross-process
    /// applock is the sole serializer — holding this semaphore there would make the
    /// applock look correct in single-process tests even when the applock itself was
    /// removed or broken, turning the regression test into a false positive. Static
    /// because TamperDetectionService is Scoped — an instance lock would be private to
    /// each request and provide zero mutual exclusion across concurrent saves.
    /// </summary>
    private static readonly SemaphoreSlim _localAppendLock = new(1, 1);

    /// <summary>
    /// Tamper detection service for audit events. Serializes hash-chain appends cross-process
    /// via a SQL Server <c>sp_getapplock</c> bound to the write transaction. The cryptographic
    /// primitives are delegated: SHA-256 via <paramref name="hasher"/>, the chain-binding HMAC via
    /// <paramref name="hmacSigner"/>, and the optional RSA-PSS digital signature via
    /// <paramref name="signatureSigner"/> (pass <c>null</c> to disable digital signatures). Both signers
    /// own their key material through <see cref="ISigningKeyProvider"/>; no key material is read from
    /// configuration here.
    /// </summary>
    public TamperDetectionService(
        IAuditEventRepository auditEventRepository,
        IAuditIntegrityRepository auditIntegrityRepository,
        IAuditSecurityEventService securityEventService,
        ILogger<TamperDetectionService> logger,
        IHasher hasher,
        HmacSha256Signer hmacSigner,
        RsaPssSigner? signatureSigner = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(auditEventRepository);
        ArgumentNullException.ThrowIfNull(auditIntegrityRepository);
        ArgumentNullException.ThrowIfNull(securityEventService);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(hmacSigner);

        _auditEventRepository = auditEventRepository;
        _auditIntegrityRepository = auditIntegrityRepository;
        _securityEventService = securityEventService;
        _logger = logger;
        _hasher = hasher;
        _hmacSigner = hmacSigner;
        _signatureSigner = signatureSigner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates an integrity record for a given audit event. The read-modify-write of the
    /// hash chain is serialized cross-process by <c>sp_getapplock</c> bound to the write
    /// transaction (see <see cref="IAuditIntegrityRepository.AcquireAppendLockAsync"/>).
    /// </summary>
    public async Task<AuditIntegrityDto> CreateIntegrityRecordAsync(
        AuditIntegrityDto auditEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Pre-compute event hash and checksum OUTSIDE the append lock — these depend only
        // on event data, not on chain state. HMAC and digital signature are computed INSIDE
        // the lock because they now include chain metadata (previousHash, sequenceNumber,
        // trustedTimestamp) that isn't known until after we read the chain head.
        var eventHash = ComputeEventHash(auditEvent);
        var checksum = ComputeChecksum(auditEvent);
        var algorithmVersion = AuditCanonicalizer.CurrentVersion;
        var enableDigitalSignatures = DigitalSignaturesEnabled;

        const int maxRetries = 10;
        var baseDelay = TimeSpan.FromMilliseconds(100);

        // On SQL Server the transaction-scoped sp_getapplock is the sole serializer —
        // taking a process-local semaphore on top would mask a broken applock in
        // single-process tests. On providers without applock support (SQLite in tests)
        // the semaphore is what serializes the read-modify-write.
        var useLocalFallback = !_auditIntegrityRepository.SupportsCrossProcessAppendLock;

        // The retry loop is defense-in-depth. Under sp_getapplock the read-modify-write
        // is strictly serialized, so duplicate-key and deadlock on SequenceNumber should
        // essentially never fire here.
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            // Captured by AppendCore and read by the retry/catch below so we can detach
            // ONLY the integrity entity we just added, not clear the whole change tracker.
            // Clearing would also detach entities AuditLogger has tracked in the outer
            // transaction (e.g. the AuditEventEntity it just saved), silently decoupling
            // them from EF even though their rows still live inside the same SQL transaction.
            AuditIntegrityEntity? pendingIntegrity = null;

            try
            {
                AuditIntegrityEntity? committed = null;

                if (useLocalFallback)
                {
                    await _localAppendLock.WaitAsync(cancellationToken);
                }

                try
                {
                    // Body of the append critical section — takes the applock, reads the
                    // chain head, inserts the new row. Shared by the two entry paths
                    // below (join-outer-transaction vs open-our-own).
                    async Task AppendCore()
                    {
                        // sp_getapplock @LockOwner='Transaction' — cross-process serializer.
                        // No-op on non-SQL-Server providers; the outer semaphore covers them.
                        await _auditIntegrityRepository.AcquireAppendLockAsync(cancellationToken);

                        var previousIntegrity =
                            await _auditIntegrityRepository.GetLatestBySequenceAsync(cancellationToken);
                        var previousHash = previousIntegrity?.EventHash;
                        var nextSequence = (previousIntegrity?.SequenceNumber ?? 0) + 1;

                        // Capture timestamp inside the lock to ensure monotonic ordering
                        // with sequence numbers (#8: prevents spurious chain discontinuity
                        // alerts when two concurrent appends commit in opposite timestamp order).
                        var timestamp = _timeProvider.GetUtcNow();

                        // HMAC and digital signature are computed inside the lock because
                        // they now bind the event to its chain position. Each carries the id of the
                        // signing key that produced it so verification reselects the exact key.
                        var hmac = await ComputeHmacAsync(
                            eventHash, previousHash, nextSequence, timestamp, cancellationToken);
                        var signature = enableDigitalSignatures
                            ? await CreateDigitalSignatureAsync(
                                eventHash, previousHash, nextSequence, timestamp, cancellationToken)
                            : (Signature: (string?)null, KeyId: (string?)null);

                        pendingIntegrity = new AuditIntegrityEntity
                        {
                            EventId = auditEvent.EventId,
                            EventHash = eventHash,
                            PreviousEventHash = previousHash,
                            TrustedTimestamp = timestamp,
                            SequenceNumber = nextSequence,
                            HmacSignature = hmac.Signature,
                            HmacKeyId = hmac.KeyId,
                            Checksum = checksum,
                            AlgorithmVersion = algorithmVersion,
                            DigitalSignature = signature.Signature,
                            DigitalSignatureKeyId = signature.KeyId
                        };

                        await _auditIntegrityRepository.AddAsync(pendingIntegrity, cancellationToken);
                        await _auditIntegrityRepository.SaveChangesAsync(cancellationToken);
                        committed = pendingIntegrity;
                    }

                    // When AuditLogger wraps event+integrity in one atomic transaction, the
                    // context already owns a transaction on this DbContext. Opening a nested
                    // one would throw "connection already in a transaction", corrupting EF's
                    // change tracker and breaking ResilientAuditLogger retries. Join the
                    // existing transaction instead; the applock still binds to it.
                    if (_auditIntegrityRepository.CurrentTransaction is not null)
                    {
                        await AppendCore();
                    }
                    else
                    {
                        await _auditIntegrityRepository.ExecuteInTransactionAsync(
                            AppendCore, cancellationToken);
                    }
                }
                finally
                {
                    if (useLocalFallback)
                    {
                        try
                        {
                            _localAppendLock.Release();
                        }
                        catch (SemaphoreFullException ex)
                        {
                            _logger.LogWarning(ex, "Attempted to release local append lock that was not held");
                        }
                    }
                }

                _logger.LogDebug(
                    "Created integrity record for event {EventId} with sequence {SequenceNumber} (attempt {Attempt})",
                    auditEvent.EventId, committed!.SequenceNumber, attempt);

                return new AuditIntegrityDto { EventId = committed.EventId };
            }
            catch (DbUpdateException ex)
                when (DuplicateKeyDetector.IsDuplicateKey(ex) || DeadlockDetector.IsDeadlock(ex))
            {
                var conflictKind = DuplicateKeyDetector.IsDuplicateKey(ex) ? "duplicate-key" : "deadlock";

                if (attempt >= maxRetries)
                {
                    _logger.LogError(ex,
                        "Failed to create integrity record for event {EventId} after {MaxRetries} attempts ({ConflictKind})",
                        auditEvent.EventId, maxRetries, conflictKind);
                    throw new InvalidOperationException(
                        $"Failed to create integrity record for event {auditEvent.EventId} after {maxRetries} attempts. " +
                        "This may indicate high concurrency or a systemic issue.", ex);
                }

                // Detach only the integrity entity this attempt added. Leaves any entities
                // tracked by an outer transaction (e.g. AuditLogger's AuditEventEntity)
                // attached so the outer commit still persists them.
                if (pendingIntegrity is not null)
                {
                    await _auditIntegrityRepository.DetachAsync(pendingIntegrity, cancellationToken);
                }

                var exponentialDelay = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 5));
                var jitterAmount = Random.Shared.Next(0, (int)(exponentialDelay * 0.3));
                var delay = TimeSpan.FromMilliseconds(exponentialDelay + jitterAmount);

                _logger.LogWarning(
                    "Retryable write conflict ({ConflictKind}) for event {EventId}. Retry {RetryCount}/{MaxRetries} after {DelayMs}ms",
                    conflictKind, auditEvent.EventId, attempt, maxRetries, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Failed to create integrity record for event {auditEvent.EventId} after {maxRetries} retries");
    }


    /// <summary>
    /// Creates integrity records for a batch of audit events atomically.
    /// Pre-computes per-event hashes outside the append lock, then acquires the lock once
    /// to read the latest sequence and build the entire chain in memory before a single bulk write.
    /// HMAC and digital signature are computed inside the lock because they now include chain metadata.
    /// </summary>
    public async Task<IReadOnlyList<AuditIntegrityDto>> CreateIntegrityRecordBatchAsync(
        IReadOnlyList<AuditIntegrityDto> auditEvents,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (auditEvents.Count == 0)
            return [];

        // Single event: delegate to existing method to avoid regression risk
        if (auditEvents.Count == 1)
        {
            var single = await CreateIntegrityRecordAsync(auditEvents[0], cancellationToken);
            return [single];
        }

        const int maxRetries = 10;
        var baseDelay = TimeSpan.FromMilliseconds(100);
        var algorithmVersion = AuditCanonicalizer.CurrentVersion;
        var enableDigitalSignatures = DigitalSignaturesEnabled;

        // Pre-compute event hashes and checksums OUTSIDE the lock — these depend only on
        // event data. HMAC and digital signature are computed INSIDE the lock because they
        // now include chain metadata (previousHash, sequenceNumber, trustedTimestamp).
        var precomputed = new (string EventHash, string Checksum)[auditEvents.Count];
        for (int i = 0; i < auditEvents.Count; i++)
        {
            var evt = auditEvents[i];
            precomputed[i] = (ComputeEventHash(evt), ComputeChecksum(evt));
        }

        // Semaphore is a fallback for providers without applock support — see the
        // per-event method for the rationale.
        var useLocalFallback = !_auditIntegrityRepository.SupportsCrossProcessAppendLock;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            // Captured by AppendBatchCore and read by the retry/catch below so we can detach
            // ONLY the integrity entities we added in this attempt, not clear the whole change
            // tracker. See the per-event method for the rationale — clearing would also strip
            // AuditLogger's tracked AuditEventEntity rows out of the outer transaction's view.
            List<AuditIntegrityEntity>? pendingEntities = null;

            try
            {
                if (useLocalFallback)
                {
                    await _localAppendLock.WaitAsync(cancellationToken);
                }

                try
                {
                    async Task AppendBatchCore()
                    {
                        // sp_getapplock @LockOwner='Transaction' — cross-process serializer.
                        // No-op on non-SQL-Server providers; the outer semaphore covers them.
                        await _auditIntegrityRepository.AcquireAppendLockAsync(cancellationToken);

                        var previousIntegrity =
                            await _auditIntegrityRepository.GetLatestBySequenceAsync(cancellationToken);
                        string? previousHash = previousIntegrity?.EventHash;
                        long baseSequence = previousIntegrity?.SequenceNumber ?? 0;

                        // Capture timestamp inside the lock to ensure monotonic ordering
                        // with sequence numbers (#8).
                        var timestamp = _timeProvider.GetUtcNow();

                        var entities = new List<AuditIntegrityEntity>(auditEvents.Count);
                        for (int i = 0; i < auditEvents.Count; i++)
                        {
                            var sequenceNumber = baseSequence + 1 + i;

                            // HMAC and digital signature bind the event to its chain position; each
                            // carries the id of the signing key that produced it.
                            var hmac = await ComputeHmacAsync(
                                precomputed[i].EventHash, previousHash, sequenceNumber, timestamp, cancellationToken);
                            var signature = enableDigitalSignatures
                                ? await CreateDigitalSignatureAsync(
                                    precomputed[i].EventHash, previousHash, sequenceNumber, timestamp, cancellationToken)
                                : (Signature: (string?)null, KeyId: (string?)null);

                            var entity = new AuditIntegrityEntity
                            {
                                EventId = auditEvents[i].EventId,
                                EventHash = precomputed[i].EventHash,
                                PreviousEventHash = previousHash,
                                TrustedTimestamp = timestamp,
                                SequenceNumber = sequenceNumber,
                                HmacSignature = hmac.Signature,
                                HmacKeyId = hmac.KeyId,
                                Checksum = precomputed[i].Checksum,
                                AlgorithmVersion = algorithmVersion,
                                DigitalSignature = signature.Signature,
                                DigitalSignatureKeyId = signature.KeyId
                            };
                            entities.Add(entity);

                            // Chain: next event links to this event's hash
                            previousHash = entity.EventHash;
                        }

                        pendingEntities = entities;
                        await _auditIntegrityRepository.AddRangeAsync(entities, cancellationToken);
                        await _auditIntegrityRepository.SaveChangesAsync(cancellationToken);
                    }

                    // Join an outer transaction if one is already active (e.g. AuditLogger's
                    // atomic event+integrity path); otherwise open our own. Nesting via
                    // ExecuteInTransactionAsync would call BeginTransactionAsync on a context
                    // that already has a transaction and throw, corrupting EF's change
                    // tracker and breaking ResilientAuditLogger's retry.
                    if (_auditIntegrityRepository.CurrentTransaction is not null)
                    {
                        await AppendBatchCore();
                    }
                    else
                    {
                        await _auditIntegrityRepository.ExecuteInTransactionAsync(
                            AppendBatchCore, cancellationToken);
                    }
                }
                finally
                {
                    if (useLocalFallback)
                    {
                        try
                        {
                            _localAppendLock.Release();
                        }
                        catch (SemaphoreFullException ex)
                        {
                            _logger.LogWarning(ex, "Attempted to release local append lock that was not held");
                        }
                    }
                }

                _logger.LogDebug(
                    "Created {Count} integrity records in batch (attempt {Attempt})",
                    auditEvents.Count, attempt);

                return auditEvents.Select(static e => new AuditIntegrityDto { EventId = e.EventId }).ToList();
            }
            catch (DbUpdateException ex)
                when (DuplicateKeyDetector.IsDuplicateKey(ex) || DeadlockDetector.IsDeadlock(ex))
            {
                var conflictKind = DuplicateKeyDetector.IsDuplicateKey(ex) ? "duplicate-key" : "deadlock";

                if (attempt >= maxRetries)
                {
                    _logger.LogError(ex,
                        "Failed to create batch integrity records after {MaxRetries} attempts ({ConflictKind})",
                        maxRetries, conflictKind);
                    throw new InvalidOperationException(
                        $"Failed to create batch integrity records after {maxRetries} attempts. " +
                        "This may indicate high concurrency or a systemic issue.", ex);
                }

                // Detach only the integrity entities this attempt added. Leaves any entities
                // tracked by an outer transaction (e.g. AuditLogger's AuditEventEntity rows)
                // attached so the outer commit still persists them.
                if (pendingEntities is { Count: > 0 })
                {
                    await _auditIntegrityRepository.DetachRangeAsync(pendingEntities, cancellationToken);
                }

                var exponentialDelay = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 5));
                var jitterAmount = Random.Shared.Next(0, (int)(exponentialDelay * 0.3));
                var delay = TimeSpan.FromMilliseconds(exponentialDelay + jitterAmount);

                _logger.LogWarning(
                    "Retryable write conflict ({ConflictKind}) in batch. Retry {RetryCount}/{MaxRetries} after {DelayMs}ms",
                    conflictKind, attempt, maxRetries, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Failed to create batch integrity records after {maxRetries} retries");
    }

    /// <summary>
    /// Verifies the integrity of a specific audit event by its ID.
    /// </summary>
    public async Task<bool> VerifyIntegrityAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var auditEvent = await _auditEventRepository.GetByIdAsync(eventId, cancellationToken);

        if (auditEvent == null)
        {
            _logger.LogWarning("Audit event {EventId} not found", eventId);
            return false;
        }

        var integrity = await _auditIntegrityRepository.GetByEventIdAsync(eventId, cancellationToken);

        if (integrity == null)
        {
            _logger.LogWarning("Integrity record for event {EventId} not found", eventId);
            return false;
        }

        return await VerifyIntegrityAsync(auditEvent, integrity, cancellationToken);
    }

    /// <summary>
    /// Verifies the integrity of an audit event using already-loaded entities.
    /// Avoids re-fetching from the database when the caller already has the data.
    /// Uses constant-time comparison for cryptographic values to prevent timing attacks (#10).
    /// </summary>
    private async Task<bool> VerifyIntegrityAsync(
        AuditEventEntity auditEvent,
        AuditIntegrityEntity integrity,
        CancellationToken cancellationToken)
    {
        var eventId = auditEvent.EventId;

        if (integrity.AlgorithmVersion != AuditCanonicalizer.CurrentVersion)
        {
            _logger.LogWarning(
                "Integrity record for event {EventId} has unexpected AlgorithmVersion {Version} (expected {Expected})",
                eventId, integrity.AlgorithmVersion, AuditCanonicalizer.CurrentVersion);
        }

        // Verify event hash (use constant-time comparison to prevent timing attacks)
        string currentHash;
        try
        {
            currentHash = ComputeEventHash(
                auditEvent.EventId, auditEvent.EventType, auditEvent.User,
                auditEvent.InsertedDate, auditEvent.JsonData);
        }
        catch (JsonException ex)
        {
            // Malformed JsonData is itself evidence of tampering (#7)
            _logger.LogError(ex, "Failed to parse JsonData for event {EventId} — treating as tamper", eventId);
            await LogTamperAlertAsync(eventId, "JsonData is malformed (unparseable JSON)", cancellationToken);
            return false;
        }

        // Constant-time Base64 comparison via MillWorks.Cryptography (consolidated FixedTimeEquals).
        if (!ConstantTime.EqualsBase64(currentHash, integrity.EventHash))
        {
            _logger.LogError("Hash mismatch for event {EventId}. Expected: {Expected}, Actual: {Actual}",
                eventId, integrity.EventHash, currentHash);
            await LogTamperAlertAsync(eventId, "Hash mismatch detected", cancellationToken);
            return false;
        }

        // Verify HMAC (binds chain-position metadata; verified via the HMAC ISigner over the
        // signing key reselected by the persisted key id).
        if (!string.IsNullOrEmpty(integrity.HmacSignature))
        {
            var hmacValid = await VerifyHmacAsync(integrity, cancellationToken);
            if (!hmacValid)
            {
                _logger.LogError("HMAC mismatch for event {EventId}", eventId);
                await LogTamperAlertAsync(eventId, "HMAC verification failed", cancellationToken);
                return false;
            }
        }

        // Verify checksum (version-independent — uses only immutable fields)
        var currentChecksum = ComputeChecksum(auditEvent.EventId, auditEvent.EventType, auditEvent.UserId);
        if (!ConstantTime.EqualsBase64(currentChecksum, integrity.Checksum))
        {
            _logger.LogError("Checksum mismatch for event {EventId}", eventId);
            await LogTamperAlertAsync(eventId, "Checksum verification failed", cancellationToken);
            return false;
        }

        // Verify digital signature if present (now includes chain position metadata)
        if (string.IsNullOrEmpty(integrity.DigitalSignature)) return true;
        var signatureValid = await VerifyDigitalSignatureAsync(integrity, cancellationToken);

        if (signatureValid) return true;
        _logger.LogError("Digital signature verification failed for event {EventId}", eventId);
        await LogTamperAlertAsync(eventId, "Digital signature invalid", cancellationToken);
        return false;
    }

    /// <summary>
    /// Verifies the integrity of the audit chain within a specified date range.
    /// Processes records in pages to avoid loading the entire chain into memory.
    /// </summary>
    public async Task<TamperDetectionResult> VerifyChainIntegrityAsync(
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 1000;

        var result = new TamperDetectionResult
        {
            StartDate = startDate ?? DateTimeOffset.MinValue,
            EndDate = endDate ?? _timeProvider.GetUtcNow(),
            VerificationTime = _timeProvider.GetUtcNow()
        };

        result.TotalEvents = await _auditIntegrityRepository.GetCountAsync(startDate, endDate, cancellationToken);
        string? previousHash = null;

        // Keyset (seek) pagination over the unique SequenceNumber index. OFFSET paging re-sorted the
        // whole window and grew more expensive with each page (quadratic over a high-volume window);
        // seeking past the last sequence number keeps each page a bounded index range read.
        long afterSequence = long.MinValue;

        while (true)
        {
            var page = await _auditIntegrityRepository.GetWithAuditEventsAfterSequenceAsync(
                startDate, endDate, afterSequence, pageSize, cancellationToken);

            if (page.Count == 0)
                break;

            foreach (var integrity in page)
            {
                result.EventsChecked++;

                // Verify chain continuity
                if (previousHash != null && integrity.PreviousEventHash != previousHash)
                {
                    result.ChainBroken = true;
                    result.TamperedEvents.Add(new TamperedEvent
                    {
                        EventId = integrity.EventId,
                        Reason = "Chain discontinuity detected",
                        DetectedAt = _timeProvider.GetUtcNow()
                    });
                    _logger.LogError("Chain broken at event {EventId}", integrity.EventId);
                }

                // Verify individual event integrity using already-loaded entities (avoids N+1 re-fetches)
                if (integrity.AuditEvent != null)
                {
                    var isValid = await VerifyIntegrityAsync(integrity.AuditEvent, integrity, cancellationToken);
                    if (!isValid)
                    {
                        result.TamperedEvents.Add(new TamperedEvent
                        {
                            EventId = integrity.EventId,
                            Reason = "Integrity verification failed",
                            DetectedAt = _timeProvider.GetUtcNow()
                        });
                    }
                }
                else
                {
                    // #4: Missing AuditEvent row is itself a tamper finding — the integrity record
                    // exists but the actual audit event was deleted without deleting the chain link.
                    result.TamperedEvents.Add(new TamperedEvent
                    {
                        EventId = integrity.EventId,
                        Reason = "Audit event missing for integrity record",
                        DetectedAt = _timeProvider.GetUtcNow()
                    });
                    _logger.LogError(
                        "Audit event {EventId} missing (integrity record exists but event row was deleted)",
                        integrity.EventId);
                    await LogTamperAlertAsync(integrity.EventId, "Audit event missing for integrity record", cancellationToken);
                }

                previousHash = integrity.EventHash;
            }

            if (page.Count < pageSize)
                break;

            afterSequence = page[^1].SequenceNumber;
        }

        result.IsValid = result is { ChainBroken: false, TamperedEvents.Count: 0 };
        return result;
    }

    /// <summary>
    /// Verifies the integrity of the sequence numbers in the audit records.
    /// </summary>
    public async Task<bool> VerifySequenceIntegrityAsync(CancellationToken cancellationToken = default)
    {
        var sequenceNumbers = await _auditIntegrityRepository.GetAllSequenceNumbersAsync(cancellationToken);
        var sequenceList = sequenceNumbers.ToList();

        if (!sequenceList.Any())
        {
            return true; // Empty sequence is valid
        }

        // Check for gaps in the sequence
        for (int i = 1; i < sequenceList.Count; i++)
        {
            if (sequenceList[i] == sequenceList[i - 1] + 1) continue;
            _logger.LogError("Sequence gap detected between {Prev} and {Current}",
                sequenceList[i - 1], sequenceList[i]);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Detects tampering events within a specified time frame.
    /// </summary>
    public async Task<List<TamperAlert>> DetectTamperingAsync(
        int hoursBack = 24,
        CancellationToken cancellationToken = default)
    {
        var alerts = new List<TamperAlert>();
        var checkTime = _timeProvider.GetUtcNow().AddHours(-hoursBack);

        // Check for sequence gaps
        var sequenceValid = await VerifySequenceIntegrityAsync(cancellationToken);
        if (!sequenceValid)
        {
            alerts.Add(new TamperAlert
            {
                AlertType = "Sequence Gap",
                Description = "Audit sequence numbers have gaps",
                DetectedAt = _timeProvider.GetUtcNow(),
                Severity = TamperSeverity.High
            });
        }

        // Check chain integrity — this also verifies individual event integrity,
        // so there's no need for a separate per-event loop
        var chainResult = await VerifyChainIntegrityAsync(checkTime, null, cancellationToken);
        if (chainResult.ChainBroken)
        {
            alerts.Add(new TamperAlert
            {
                AlertType = "Chain Broken",
                Description = "Audit chain integrity compromised",
                DetectedAt = _timeProvider.GetUtcNow(),
                Severity = TamperSeverity.Critical
            });
        }

        foreach (var tamperedEvent in chainResult.TamperedEvents)
        {
            alerts.Add(new TamperAlert
            {
                EventId = tamperedEvent.EventId,
                AlertType = "Integrity Violation",
                Description = tamperedEvent.Reason,
                DetectedAt = tamperedEvent.DetectedAt,
                Severity = TamperSeverity.Critical
            });
        }

        return alerts;
    }

    /// <summary>
    /// Exports the integrity proof for a specific audit event as a byte array.
    /// </summary>
    public async Task<byte[]> ExportIntegrityProofAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var integrity = await _auditIntegrityRepository.GetByEventIdAsync(eventId, cancellationToken);

        if (integrity == null)
        {
            throw new InvalidOperationException($"No integrity record found for event {eventId}");
        }

        var proof = new
        {
            EventId = eventId,
            integrity.EventHash,
            PreviousHash = integrity.PreviousEventHash,
            Timestamp = integrity.TrustedTimestamp,
            Sequence = integrity.SequenceNumber,
            Signature = integrity.DigitalSignature,
            Algorithm = integrity.AlgorithmVersion,
            VerificationInstructions = "Use SHA-256 to hash the event data and compare with EventHash"
        };

        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(proof);
    }

    // Private helper methods
    /// <summary>
    /// Computes the SHA-256 event hash over the canonical <c>'|'</c>-delimited field projection
    /// (eventId, eventType, user, normalized inserted date, canonicalized JSON). Which fields are
    /// hashed and how they are framed is audit-domain logic and stays here; the raw SHA-256 is
    /// delegated to <see cref="IHasher"/>.
    /// </summary>
    private string ComputeEventHash(Guid eventId, string? eventType, string? user,
        DateTimeOffset? insertedDate, string? jsonData)
    {
        var writer = new ArrayBufferWriter<byte>();
        AppendUtf8(writer, eventId.ToString());
        AppendUtf8(writer, "|");
        AppendUtf8(writer, eventType ?? string.Empty);
        AppendUtf8(writer, "|");
        AppendUtf8(writer, user ?? string.Empty);
        AppendUtf8(writer, "|");
        AppendUtf8(writer, AuditCanonicalizer.NormalizeDate(insertedDate));
        AppendUtf8(writer, "|");
        AppendUtf8(writer, AuditCanonicalizer.Canonicalize(jsonData));

        return CryptoEncoding.ToBase64(_hasher.Sha256(writer.WrittenSpan));
    }

    /// <summary>
    /// Overload that computes the event hash directly from an <see cref="AuditIntegrityDto"/>.
    /// </summary>
    private string ComputeEventHash(AuditIntegrityDto e) =>
        ComputeEventHash(e.EventId, e.EventType, e.User, e.InsertedDate, e.JsonData);

    /// <summary>
    /// Computes a checksum for the immutable fields of the audit event using SHA-256.
    /// Uses length-prefixing to avoid ambiguous hash inputs; the raw SHA-256 is delegated to
    /// <see cref="IHasher"/>.
    /// </summary>
    private string ComputeChecksum(Guid eventId, string? eventType, Guid? userId)
    {
        var writer = new ArrayBufferWriter<byte>();
        AppendLengthPrefixed(writer, eventId.ToString());
        AppendLengthPrefixed(writer, eventType ?? string.Empty);
        AppendLengthPrefixed(writer, userId?.ToString() ?? string.Empty);

        return CryptoEncoding.ToBase64(_hasher.Sha256(writer.WrittenSpan));
    }

    /// <summary>
    /// Overload that computes the checksum directly from an <see cref="AuditIntegrityDto"/>.
    /// </summary>
    private string ComputeChecksum(AuditIntegrityDto e) =>
        ComputeChecksum(e.EventId, e.EventType, e.UserId);

    /// <summary>
    /// Computes the chain-binding HMAC via the HMAC <see cref="ISigner"/>, returning the Base64
    /// signature and the id of the signing key that produced it. The signer resolves the active
    /// symmetric key from <see cref="ISigningKeyProvider"/>; the key id is persisted so verification
    /// can reselect the exact key (rotation-safe).
    /// </summary>
    private async Task<(string Signature, string KeyId)> ComputeHmacAsync(
        string eventHash,
        string? previousEventHash,
        long sequenceNumber,
        DateTimeOffset trustedTimestamp,
        CancellationToken cancellationToken)
    {
        var input = BuildChainBindingInput(eventHash, previousEventHash, sequenceNumber, trustedTimestamp);
        var envelope = await _hmacSigner.SignAsync(input, IntegrityKeyScope, cancellationToken);
        return (envelope.ValueBase64, envelope.KeyId);
    }

    /// <summary>
    /// Verifies the chain-binding HMAC via the HMAC <see cref="IVerifier"/>, reselecting the signing
    /// key by the id persisted on the row. A row with an HMAC but no recorded key id, or a non-Base64
    /// HMAC, fails closed (treated as tamper).
    /// </summary>
    private async Task<bool> VerifyHmacAsync(AuditIntegrityEntity integrity, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(integrity.HmacKeyId) || string.IsNullOrEmpty(integrity.HmacSignature))
        {
            return false;
        }

        SignatureEnvelope envelope;
        try
        {
            envelope = SignatureEnvelope.FromBase64(
                SignatureAlgorithm.HmacSha256, integrity.HmacKeyId, integrity.HmacSignature);
        }
        catch (FormatException)
        {
            return false;
        }

        var input = BuildChainBindingInput(
            integrity.EventHash, integrity.PreviousEventHash, integrity.SequenceNumber, integrity.TrustedTimestamp);
        return await _hmacSigner.VerifyAsync(input, envelope, IntegrityKeyScope, cancellationToken);
    }

    /// <summary>
    /// Creates the RSA-PSS digital signature binding the event hash to its chain position via the
    /// RSA-PSS <see cref="ISigner"/>, returning the Base64 signature and the id of the signing key.
    /// Returns <c>(null, null)</c> when digital signatures are disabled.
    /// </summary>
    private async Task<(string? Signature, string? KeyId)> CreateDigitalSignatureAsync(
        string eventHash,
        string? previousEventHash,
        long sequenceNumber,
        DateTimeOffset trustedTimestamp,
        CancellationToken cancellationToken)
    {
        if (_signatureSigner is null)
        {
            return (null, null);
        }

        var input = BuildChainBindingInput(eventHash, previousEventHash, sequenceNumber, trustedTimestamp);
        var envelope = await _signatureSigner.SignAsync(input, IntegrityKeyScope, cancellationToken);
        return (envelope.ValueBase64, envelope.KeyId);
    }

    /// <summary>
    /// Verifies the RSA-PSS digital signature via the RSA-PSS <see cref="IVerifier"/>, reselecting the
    /// signing key by the id persisted on the row. When digital signatures are disabled there is no
    /// verifier to re-check a previously-written signature, so it is skipped (returns <c>true</c>),
    /// matching the pre-extraction behaviour. A signature with no recorded key id, or a non-Base64
    /// signature, fails closed.
    /// </summary>
    private async Task<bool> VerifyDigitalSignatureAsync(AuditIntegrityEntity integrity, CancellationToken cancellationToken)
    {
        if (_signatureSigner is null || string.IsNullOrEmpty(integrity.DigitalSignature))
        {
            return true;
        }

        if (string.IsNullOrEmpty(integrity.DigitalSignatureKeyId))
        {
            return false;
        }

        SignatureEnvelope envelope;
        try
        {
            envelope = SignatureEnvelope.FromBase64(
                SignatureAlgorithm.RsaPssSha256, integrity.DigitalSignatureKeyId, integrity.DigitalSignature);
        }
        catch (FormatException)
        {
            return false;
        }

        var input = BuildChainBindingInput(
            integrity.EventHash, integrity.PreviousEventHash, integrity.SequenceNumber, integrity.TrustedTimestamp);
        return await _signatureSigner.VerifyAsync(input, envelope, IntegrityKeyScope, cancellationToken);
    }

    /// <summary>
    /// Builds the length-prefixed byte buffer that binds an event to its chain position
    /// (eventHash, previousEventHash, sequenceNumber, normalized trusted timestamp). This framing is
    /// audit-domain logic; the signers compute the HMAC / RSA-PSS over the buffer. Length-prefixing
    /// prevents concatenation ambiguity (e.g. "A|B|C" vs "A|B" + "|C").
    /// </summary>
    private static byte[] BuildChainBindingInput(
        string eventHash,
        string? previousEventHash,
        long sequenceNumber,
        DateTimeOffset trustedTimestamp)
    {
        var timestampString = AuditCanonicalizer.NormalizeDate(trustedTimestamp);
        var previous = previousEventHash ?? string.Empty;

        var writer = new ArrayBufferWriter<byte>();
        AppendLengthPrefixed(writer, eventHash);
        AppendLengthPrefixed(writer, previous);
        AppendLengthPrefixed(writer, sequenceNumber.ToString(CultureInfo.InvariantCulture));
        AppendLengthPrefixed(writer, timestampString);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Appends the UTF-8 bytes of <paramref name="value"/> to <paramref name="writer"/>.
    /// </summary>
    private static void AppendUtf8(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount == 0)
        {
            return;
        }

        var span = writer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, span);
        writer.Advance(written);
    }

    /// <summary>
    /// Appends a length-prefixed string to <paramref name="writer"/>.
    /// Format: 4-byte big-endian length + UTF-8 bytes. Prevents concatenation ambiguity.
    /// </summary>
    private static void AppendLengthPrefixed(ArrayBufferWriter<byte> writer, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthBytes, byteCount);
        writer.Write(lengthBytes);

        if (byteCount == 0)
        {
            return;
        }

        var span = writer.GetSpan(byteCount);
        var written = Encoding.UTF8.GetBytes(value, span);
        writer.Advance(written);
    }

    /// <summary>
    /// Logs a tamper alert to the security event system
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="reason"></param>
    /// <param name="cancellationToken"></param>
    private async Task LogTamperAlertAsync(
        Guid eventId,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var securityEvent = new SecurityEventDto
            {
                EventType = SecurityEventType.AuditTamperAlert,
                Severity = SecurityEventSeverity.Critical,
                RelatedAuditEventId = eventId,
                Message = $"Tamper detected for audit event {eventId}: {reason}",
                Details = new Dictionary<string, object?>
                {
                    ["EventId"] = eventId,
                    ["Reason"] = reason,
                    ["DetectionMethod"] = "Integrity Verification",
                    ["Timestamp"] = _timeProvider.GetUtcNow()
                }
            };

            await _securityEventService.RecordEventAsync(securityEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log security event for tamper alert");
            // Don't throw - security event logging failure shouldn't break tamper detection
        }
    }
}
