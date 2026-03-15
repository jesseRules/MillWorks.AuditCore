using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Canonicalization;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.DistributedLocking.Implementations;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Services.TamperDetection;

/// <summary>
/// Tamper detection service for audit events with Redis distributed locking for multi-instance coordination
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
    /// Distributed lock service
    /// </summary>
    private readonly IAuditDistributedLockService _distributedLockService;

    /// <summary>
    /// Security event service
    /// </summary>
    private readonly IAuditSecurityEventService _securityEventService;

    /// <summary>
    /// Logger
    /// </summary>
    private readonly ILogger<TamperDetectionService> _logger;

    /// <summary>
    /// Configuration
    /// </summary>
    private readonly IConfiguration _configuration;

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
    private static readonly Lazy<string> DefaultHmacKey = new(GenerateDefaultHmacKey);
    
    /// <summary>
    /// Local fallback lock for single-instance scenarios.
    /// Static because TamperDetectionService is Scoped — an instance lock would be
    /// private to each request, providing zero mutual exclusion across concurrent saves.
    /// </summary>
    private static readonly SemaphoreSlim LocalFallbackLock = new(1, 1);

    /// <summary>
    /// Use distributed locking flag
    /// </summary>
    private readonly bool _useDistributedLocking;

    /// <summary>
    /// Cached signing key parameters. Loaded lazily on first use.
    /// Assumes keys are static at runtime — no rotation while the process is running.
    /// </summary>
    private static RSAParameters? _cachedSigningKey;
    private static RSAParameters? _cachedVerifyKey;
    private static readonly Lock KeyLoadLock = new();

    /// <summary>
    /// Tamper detection service for audit events with distributed locking support
    /// </summary>
    public TamperDetectionService(
        IAuditEventRepository auditEventRepository,
        IAuditIntegrityRepository auditIntegrityRepository,
        IAuditSecurityEventService securityEventService,
        ILogger<TamperDetectionService> logger,
        IConfiguration configuration,
        IAuditDistributedLockService? distributedLockService = null,
        TimeProvider? timeProvider = null)
    {
        _auditEventRepository = auditEventRepository;
        _auditIntegrityRepository = auditIntegrityRepository;
        _securityEventService = securityEventService;
        _distributedLockService = distributedLockService ?? new NullDistributedLockService();
        _logger = logger;
        _configuration = configuration;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var configuredKey = configuration["Audit:HmacKey"];
        if (string.IsNullOrEmpty(configuredKey))
        {
            var env = configuration["ASPNETCORE_ENVIRONMENT"];
            if (string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Audit:HmacKey must be configured in Production. " +
                    "A transient key would cause false tamper alerts across instances or after restarts.");
            }

            _logger.LogWarning(
                "Audit:HmacKey is not configured. Using a process-scoped generated key. " +
                "HMAC signatures will not survive application restarts. Configure Audit:HmacKey for production use");
            _hmacKey = DefaultHmacKey.Value;
        }
        else
        {
            _hmacKey = configuredKey;
        }

        _useDistributedLocking = distributedLockService != null &&
                                 configuration.GetValue("Audit:UseDistributedLocking", true);
    }

    /// <summary>
    /// Creates an integrity record for a given audit event with distributed locking for multi-instance coordination.
    /// </summary>
    public async Task<AuditIntegrityDto> CreateIntegrityRecordAsync(
        AuditIntegrityDto auditEvent,
        CancellationToken cancellationToken = default)
    {
        const int maxRetries = 10;
        int retryCount = 0;
        var baseDelay = TimeSpan.FromMilliseconds(100);
        var random = new Random();

        while (retryCount < maxRetries)
        {
            IDisposable? distributedLock = null;
            bool localLockAcquired = false;

            try
            {
                // Try to acquire distributed lock if available
                if (_useDistributedLocking)
                {
                    try
                    {
                        const string lockKey = "audit:integrity:sequence";
                        var lockTimeout = TimeSpan.FromSeconds(5);

                        distributedLock = await _distributedLockService.AcquireLockAsync(
                            lockKey,
                            lockTimeout,
                            cancellationToken);

                        _logger.LogDebug("Acquired distributed lock for audit integrity sequence");
                    }
                    catch (TimeoutException)
                    {
                        // If we can't get distributed lock, fall back to local lock
                        _logger.LogWarning("Failed to acquire distributed lock, falling back to local lock");
                        await LocalFallbackLock.WaitAsync(cancellationToken);
                        localLockAcquired = true;
                    }
                }
                else
                {
                    // Use local lock if distributed locking is disabled
                    await LocalFallbackLock.WaitAsync(cancellationToken);
                    localLockAcquired = true;
                }

                // Critical section - protected by distributed or local lock
                // Get the latest sequence number and previous hash
                var previousIntegrity = await _auditIntegrityRepository.GetLatestBySequenceAsync(cancellationToken);

                var auditEventEntity = await _auditEventRepository.GetByIdAsync(auditEvent.EventId, cancellationToken);
                if (auditEventEntity == null)
                {
                    throw new InvalidOperationException(
                        $"Audit event {auditEvent.EventId} not found. Cannot create integrity record for a non-existent event.");
                }

                var integrity = new AuditIntegrityEntity
                {
                    EventId = auditEvent.EventId,
                    EventHash = ComputeEventHash(auditEventEntity),
                    PreviousEventHash = previousIntegrity?.EventHash,
                    TrustedTimestamp = _timeProvider.GetUtcNow(),
                    HmacSignature = ComputeHmac(auditEventEntity),
                    Checksum = ComputeChecksum(auditEventEntity),
                    AlgorithmVersion = AuditCanonicalizer.CurrentVersion
                };

                // Optionally add digital signature if PKI is configured
                if (_configuration.GetValue<bool>("Audit:EnableDigitalSignatures"))
                {
                    integrity.DigitalSignature = await CreateDigitalSignatureAsync(integrity.EventHash);
                }

                await _auditIntegrityRepository.AddAsync(integrity, cancellationToken);
                await _auditIntegrityRepository.SaveChangesAsync(cancellationToken);

                _logger.LogDebug(
                    "Created integrity record for event {EventId} with sequence {SequenceNumber} (attempt {Attempt})",
                    auditEvent.EventId, integrity.SequenceNumber, retryCount + 1);

                AuditIntegrityDto result = new()
                {
                    EventId = integrity.EventId,
                };

                return result;
            }
            catch (DbUpdateException ex) when (IsDuplicateKeyException(ex))
            {
                retryCount++;

                if (retryCount >= maxRetries)
                {
                    _logger.LogError(ex,
                        "Failed to create integrity record for event {EventId} after {MaxRetries} attempts",
                        auditEvent.EventId, maxRetries);
                    throw new InvalidOperationException(
                        $"Failed to create integrity record for event {auditEvent.EventId} after {maxRetries} attempts. " +
                        "This may indicate high concurrency or a systemic issue.", ex);
                }

                // Clear the change tracker to remove the failed entity
                await _auditIntegrityRepository.ClearChangeTrackerAsync(cancellationToken);

                // Calculate exponential backoff with jitter
                var exponentialDelay = baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(retryCount - 1, 5));
                var jitterAmount = random.Next(0, (int)(exponentialDelay * 0.3));
                var delay = TimeSpan.FromMilliseconds(exponentialDelay + jitterAmount);

                _logger.LogWarning(
                    "Duplicate sequence number detected for event {EventId}. Retry {RetryCount}/{MaxRetries} after {DelayMs}ms",
                    auditEvent.EventId, retryCount, maxRetries, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error creating integrity record for event {EventId}",
                    auditEvent.EventId);
                throw;
            }
            finally
            {
                // CRITICAL: Always release locks in reverse order of acquisition
                if (localLockAcquired)
                {
                    try
                    {
                        LocalFallbackLock.Release();
                    }
                    catch (SemaphoreFullException ex)
                    {
                        _logger.LogWarning(ex, "Attempted to release local lock that was not held");
                    }
                }

                distributedLock?.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"Failed to create integrity record for event {auditEvent.EventId} after {maxRetries} retries");
    }

    /// <summary>
    /// Checks if the exception is a duplicate key violation
    /// </summary>
    private static bool IsDuplicateKeyException(DbUpdateException ex)
    {
        // SQL Server error 2601 is "Cannot insert duplicate key row"
        // SQL Server error 2627 is "Violation of UNIQUE KEY constraint"
        if (ex.InnerException is Microsoft.Data.SqlClient.SqlException sqlEx)
        {
            return sqlEx.Number is 2601 or 2627;
        }

        // For other database providers, check the message
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unique", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("IX_AuditIntegrity_SequenceNumber", StringComparison.OrdinalIgnoreCase);
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
    private static string ComputeEventHash(AuditEventEntity auditEvent)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(auditEvent.EventId.ToString()));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(auditEvent.EventType ?? string.Empty));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(auditEvent.User ?? string.Empty));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(AuditCanonicalizer.NormalizeDate(auditEvent.InsertedDate)));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(AuditCanonicalizer.Canonicalize(auditEvent.JsonData)));

        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    /// <summary>
    /// Computes an HMAC signature for critical fields of the audit event.
    /// Uses canonical UTC date format for deterministic signing.
    /// </summary>
    private string ComputeHmac(AuditEventEntity auditEvent)
    {
        var dateString = AuditCanonicalizer.NormalizeDate(auditEvent.InsertedDate);
        var dataToSign = $"{auditEvent.EventId}|{auditEvent.EventType}|{dateString}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_hmacKey));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataToSign));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Computes a checksum for critical fields of the audit event using SHA-256.
    /// </summary>
    private static string ComputeChecksum(AuditEventEntity auditEvent)
    {
        var criticalFields = $"{auditEvent.EventId}{auditEvent.EventType}{auditEvent.UserId}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(criticalFields));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Creates a digital signature for the given data using the configured private key.
    /// Key material is cached on first use to avoid reading the PEM file on every call.
    /// </summary>
    private Task<string> CreateDigitalSignatureAsync(string data)
    {
        var enableSignatures = _configuration.GetValue<bool>("Audit:EnableDigitalSignatures");
        if (!enableSignatures)
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
        var enableSignatures = _configuration.GetValue<bool>("Audit:EnableDigitalSignatures");
        if (!enableSignatures)
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

        lock (KeyLoadLock)
        {
            if (_cachedSigningKey.HasValue) return _cachedSigningKey.Value;

            var keyPath = _configuration["Audit:DigitalSignaturePrivateKeyPath"];
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

        lock (KeyLoadLock)
        {
            if (_cachedVerifyKey.HasValue) return _cachedVerifyKey.Value;

            var publicKeyPath = _configuration["Audit:DigitalSignaturePublicKeyPath"];
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