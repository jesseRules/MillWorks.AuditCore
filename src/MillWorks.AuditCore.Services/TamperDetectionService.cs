using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Canonicalization;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Services.TamperDetection;

/// <summary>
/// Tamper detection service for audit events. Cross-process serialization of the
/// hash-chain append happens via a SQL Server <c>sp_getapplock</c> bound to the
/// write transaction (see <see cref="IAuditIntegrityRepository.AcquireAppendLockAsync"/>).
/// </summary>
public sealed class TamperDetectionService : ITamperDetectionService
{
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
    /// Audit configuration options (HMAC key, digital-signature enable flag, environment).
    /// </summary>
    private readonly AuditOptions _auditOptions;

    /// <summary>
    /// Security configuration options (digital-signature key paths).
    /// </summary>
    private readonly SecurityOptions _securityOptions;

    /// <summary>
    /// Time provider for testable timestamps
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// HMAC key for integrity checks
    /// </summary>
    private readonly string _hmacKey;

    /// <summary>
    /// Lazily-generated default HMAC key shared across all instances when no key is configured.
    /// Static to ensure consistency across Scoped service lifetimes within the same process.
    /// </summary>
    private static readonly Lazy<string> _defaultHmacKey = new(GenerateDefaultHmacKey);

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
    /// Cached signing key parameters. Loaded lazily on first use.
    /// Assumes keys are static at runtime — no rotation while the process is running.
    /// </summary>
    private static RSAParameters? _cachedSigningKey;

    /// <summary>
    /// Cached verification key parameters. Loaded lazily on first use. Separate from signing key to allow
    /// </summary>
    private static RSAParameters? _cachedVerifyKey;

    /// <summary>
    /// Lock for synchronizing access to the signing key cache. Only used when digital signatures are enabled.
    /// </summary>
    private static readonly Lock _keyLoadLock = new();

    /// <summary>
    /// Tamper detection service for audit events. Serializes hash-chain appends cross-process
    /// via a SQL Server <c>sp_getapplock</c> bound to the write transaction.
    /// HMAC key, digital-signature enable flag, and Production detection come from
    /// <see cref="AuditOptions"/>; digital-signature key paths come from <see cref="SecurityOptions"/>.
    /// <paramref name="hostEnvironment"/> is authoritative for Production detection when registered;
    /// otherwise falls back to <see cref="AuditOptions.Environment"/>.
    /// </summary>
    public TamperDetectionService(
        IAuditEventRepository auditEventRepository,
        IAuditIntegrityRepository auditIntegrityRepository,
        IAuditSecurityEventService securityEventService,
        ILogger<TamperDetectionService> logger,
        IOptions<AuditOptions> auditOptions,
        IOptions<SecurityOptions> securityOptions,
        TimeProvider? timeProvider = null,
        IHostEnvironment? hostEnvironment = null)
    {
        _auditEventRepository = auditEventRepository;
        _auditIntegrityRepository = auditIntegrityRepository;
        _securityEventService = securityEventService;
        _logger = logger;
        _auditOptions = auditOptions.Value;
        _securityOptions = securityOptions.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (string.IsNullOrEmpty(_auditOptions.HmacKey))
        {
            // IHostEnvironment wins when registered; otherwise AuditOptions.Environment.
            var isProduction = hostEnvironment?.IsProduction()
                               ?? string.Equals(_auditOptions.Environment, "Production",
                                   StringComparison.OrdinalIgnoreCase);

            if (isProduction)
            {
                throw new InvalidOperationException(
                    "Audit:HmacKey must be configured in Production. " +
                    "A transient key would cause false tamper alerts across instances or after restarts.");
            }

            _logger.LogWarning(
                "Audit:HmacKey is not configured. Using a process-scoped generated key. " +
                "HMAC signatures will not survive application restarts. Configure Audit:HmacKey for production use");
            _hmacKey = _defaultHmacKey.Value;
        }
        else
        {
            _hmacKey = _auditOptions.HmacKey;
        }
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

        // Pre-compute hashes OUTSIDE the append lock — these depend only on event data,
        // not on chain state (PreviousEventHash). Hashing stays off the critical path.
        var eventHash = ComputeEventHash(auditEvent);
        var hmacSignature = ComputeHmac(auditEvent);
        var checksum = ComputeChecksum(auditEvent);
        var algorithmVersion = AuditCanonicalizer.CurrentVersion;
        var timestamp = _timeProvider.GetUtcNow();

        // Pre-compute digital signature if enabled (RSA is CPU-intensive)
        string? digitalSignature = null;
        if (_auditOptions.EnableDigitalSignatures)
        {
            digitalSignature = await CreateDigitalSignatureAsync(eventHash);
        }

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

                        pendingIntegrity = new AuditIntegrityEntity
                        {
                            EventId = auditEvent.EventId,
                            EventHash = eventHash,
                            PreviousEventHash = previousHash,
                            TrustedTimestamp = timestamp,
                            SequenceNumber = nextSequence,
                            HmacSignature = hmacSignature,
                            Checksum = checksum,
                            AlgorithmVersion = algorithmVersion,
                            DigitalSignature = digitalSignature
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
        var timestamp = _timeProvider.GetUtcNow();
        var algorithmVersion = AuditCanonicalizer.CurrentVersion;
        var enableDigitalSignatures = _auditOptions.EnableDigitalSignatures;

        // Pre-compute all per-event hashes OUTSIDE the lock
        var precomputed =
            new (string EventHash, string Hmac, string Checksum, string? DigitalSignature)[auditEvents.Count];
        for (int i = 0; i < auditEvents.Count; i++)
        {
            var evt = auditEvents[i];
            var eventHash = ComputeEventHash(evt);
            var hmac = ComputeHmac(evt);
            var checksum = ComputeChecksum(evt);
            string? digitalSignature = enableDigitalSignatures
                ? await CreateDigitalSignatureAsync(eventHash)
                : null;
            precomputed[i] = (eventHash, hmac, checksum, digitalSignature);
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

                        var entities = new List<AuditIntegrityEntity>(auditEvents.Count);
                        for (int i = 0; i < auditEvents.Count; i++)
                        {
                            var entity = new AuditIntegrityEntity
                            {
                                EventId = auditEvents[i].EventId,
                                EventHash = precomputed[i].EventHash,
                                PreviousEventHash = previousHash,
                                TrustedTimestamp = timestamp,
                                SequenceNumber = baseSequence + 1 + i,
                                HmacSignature = precomputed[i].Hmac,
                                Checksum = precomputed[i].Checksum,
                                AlgorithmVersion = algorithmVersion,
                                DigitalSignature = precomputed[i].DigitalSignature
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

        // Verify event hash
        var currentHash = ComputeEventHash(auditEvent);
        if (currentHash != integrity.EventHash)
        {
            _logger.LogError("Hash mismatch for event {EventId}. Expected: {Expected}, Actual: {Actual}",
                eventId, integrity.EventHash, currentHash);
            await LogTamperAlertAsync(eventId, "Hash mismatch detected", cancellationToken);
            return false;
        }

        // Verify HMAC
        if (!string.IsNullOrEmpty(integrity.HmacSignature))
        {
            var currentHmac = ComputeHmac(auditEvent);
            if (currentHmac != integrity.HmacSignature)
            {
                _logger.LogError("HMAC mismatch for event {EventId}", eventId);
                await LogTamperAlertAsync(eventId, "HMAC verification failed", cancellationToken);
                return false;
            }
        }

        // Verify checksum (version-independent — uses only immutable fields)
        var currentChecksum = ComputeChecksum(auditEvent);
        if (currentChecksum != integrity.Checksum)
        {
            _logger.LogError("Checksum mismatch for event {EventId}", eventId);
            await LogTamperAlertAsync(eventId, "Checksum verification failed", cancellationToken);
            return false;
        }

        // Verify digital signature if present
        if (string.IsNullOrEmpty(integrity.DigitalSignature)) return true;
        var signatureValid = await VerifyDigitalSignatureAsync(
            integrity.EventHash,
            integrity.DigitalSignature);

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
        int skip = 0;

        while (true)
        {
            var page = await _auditIntegrityRepository.GetWithAuditEventsPagedAsync(
                startDate, endDate, skip, pageSize, cancellationToken);

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

                previousHash = integrity.EventHash;
            }

            if (page.Count < pageSize)
                break;

            skip += pageSize;
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
    /// Computes the event hash by feeding each field incrementally into SHA-256,
    /// avoiding a single large concatenated string that could land on the LOH when JsonData is large.
    /// Canonicalizes JsonData and normalizes dates for deterministic hashing.
    /// </summary>
    private static string ComputeEventHash(Guid eventId, string? eventType, string? user,
        DateTimeOffset? insertedDate, string? jsonData)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(eventId.ToString()));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(eventType ?? string.Empty));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(user ?? string.Empty));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(AuditCanonicalizer.NormalizeDate(insertedDate)));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(AuditCanonicalizer.Canonicalize(jsonData)));

        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    private static string ComputeEventHash(AuditIntegrityDto e) =>
        ComputeEventHash(e.EventId, e.EventType, e.User, e.InsertedDate, e.JsonData);

    private static string ComputeEventHash(AuditEventEntity e) =>
        ComputeEventHash(e.EventId, e.EventType, e.User, e.InsertedDate, e.JsonData);

    /// <summary>
    /// Computes an HMAC signature for critical fields of the audit event.
    /// Uses canonical UTC date format for deterministic signing.
    /// </summary>
    private string ComputeHmac(Guid eventId, string? eventType, DateTimeOffset? insertedDate)
    {
        var dateString = AuditCanonicalizer.NormalizeDate(insertedDate);
        var dataToSign = $"{eventId}|{eventType}|{dateString}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_hmacKey));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataToSign));
        return Convert.ToBase64String(hashBytes);
    }

    private string ComputeHmac(AuditIntegrityDto e) =>
        ComputeHmac(e.EventId, e.EventType, e.InsertedDate);

    private string ComputeHmac(AuditEventEntity e) =>
        ComputeHmac(e.EventId, e.EventType, e.InsertedDate);

    /// <summary>
    /// Computes a checksum for critical fields of the audit event using SHA-256.
    /// </summary>
    private static string ComputeChecksum(Guid eventId, string? eventType, Guid? userId)
    {
        var criticalFields = $"{eventId}{eventType}{userId}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(criticalFields));
        return Convert.ToBase64String(hashBytes);
    }

    private static string ComputeChecksum(AuditIntegrityDto e) =>
        ComputeChecksum(e.EventId, e.EventType, e.UserId);

    private static string ComputeChecksum(AuditEventEntity e) =>
        ComputeChecksum(e.EventId, e.EventType, e.UserId);

    /// <summary>
    /// Creates a digital signature for the given data using the configured private key.
    /// Key material is cached on first use to avoid reading the PEM file on every call.
    /// </summary>
    private Task<string> CreateDigitalSignatureAsync(string data)
    {
        if (!_auditOptions.EnableDigitalSignatures)
        {
            return Task.FromResult(string.Empty);
        }

        var keyParams = GetOrLoadSigningKey();
        using var rsa = RSA.Create();
        rsa.ImportParameters(keyParams);
        var signatureBytes =
            rsa.SignData(Encoding.UTF8.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Task.FromResult(Convert.ToBase64String(signatureBytes));
    }

    /// <summary>
    /// Verifies a digital signature using the configured public key.
    /// Key material is cached on first use.
    /// </summary>
    private Task<bool> VerifyDigitalSignatureAsync(string data, string signature)
    {
        if (!_auditOptions.EnableDigitalSignatures)
        {
            return Task.FromResult(true);
        }

        var keyParams = GetOrLoadVerifyKey();
        using var rsa = RSA.Create();
        rsa.ImportParameters(keyParams);
        var signatureBytes = Convert.FromBase64String(signature);
        return Task.FromResult(rsa.VerifyData(Encoding.UTF8.GetBytes(data), signatureBytes,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    /// <summary>
    /// Loads and caches the private signing key from the PEM file.
    /// Thread-safe — only reads the file once across all instances.
    /// </summary>
    private RSAParameters GetOrLoadSigningKey()
    {
        if (_cachedSigningKey.HasValue) return _cachedSigningKey.Value;

        lock (_keyLoadLock)
        {
            if (_cachedSigningKey.HasValue) return _cachedSigningKey.Value;

            var keyPath = _securityOptions.DigitalSignaturePrivateKeyPath;
            if (string.IsNullOrEmpty(keyPath) || !File.Exists(keyPath))
            {
                throw new InvalidOperationException(
                    "Digital signature private key path is not configured or file does not exist.");
            }

            var privateKeyPem = File.ReadAllText(keyPath);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem.ToCharArray());
            _cachedSigningKey = rsa.ExportParameters(true);
            return _cachedSigningKey.Value;
        }
    }

    /// <summary>
    /// Loads and caches the public verification key from the PEM file.
    /// Thread-safe — only reads the file once across all instances.
    /// </summary>
    private RSAParameters GetOrLoadVerifyKey()
    {
        if (_cachedVerifyKey.HasValue) return _cachedVerifyKey.Value;

        lock (_keyLoadLock)
        {
            if (_cachedVerifyKey.HasValue) return _cachedVerifyKey.Value;

            var publicKeyPath = _securityOptions.DigitalSignaturePublicKeyPath;
            if (string.IsNullOrEmpty(publicKeyPath) || !File.Exists(publicKeyPath))
            {
                throw new InvalidOperationException(
                    "Digital signature public key path is not configured or file does not exist.");
            }

            var publicKeyPem = File.ReadAllText(publicKeyPath);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem.ToCharArray());
            _cachedVerifyKey = rsa.ExportParameters(false);
            return _cachedVerifyKey.Value;
        }
    }

    /// <summary>
    /// Generates a default HMAC key if none is configured
    /// </summary>
    /// <returns></returns>
    private static string GenerateDefaultHmacKey()
    {
        // Generate a secure random key if not configured
        using var rng = RandomNumberGenerator.Create();
        var keyBytes = new byte[32];
        rng.GetBytes(keyBytes);
        return Convert.ToBase64String(keyBytes);
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
