using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Constants;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.EntityFramework.Interceptors;

/// <summary>
/// Interceptor to automatically audit entity changes with circular dependency prevention
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<AuditSaveChangesInterceptor> _logger;

    /// <summary>
    /// Compliance enforcement mode and options. Null when UseCompliance() is not configured.
    /// </summary>
    private readonly ComplianceEnforcementMode? _enforcementMode;

    /// <summary>
    /// Cache-backed consent verification. Null when UseCompliance() is not configured.
    /// All reads are synchronous (IMemoryCache) — safe for interceptor paths.
    /// </summary>
    private readonly IConsentVerificationService? _consentService;

    /// <summary>
    /// Aggregate diagnostic counters for snapshot serialization fallback visibility.
    /// Null when diagnostics are not registered.
    /// </summary>
    private readonly IAuditDiagnostics? _diagnostics;

    /// <summary>
    /// Configured audit failure mode — drives whether audit build failures rethrow
    /// (fail-closed) or are logged and swallowed (permissive).
    /// </summary>
    private readonly AuditFailureMode _failureMode;

    /// <summary>
    /// Policy that decides, given an <see cref="AuditFailureContext"/>, whether a
    /// failure should propagate. Non-null: factory passes the registered implementation
    /// (default <see cref="RegulatedEntityFailurePolicy"/>); direct test construction
    /// falls back to a local <see cref="RegulatedEntityFailurePolicy"/> instance.
    /// </summary>
    private readonly IAuditFailurePolicy _failurePolicy;

    /// <summary>
    /// Scope factory used to resolve a per-save <see cref="IAuditSink"/>. The
    /// interceptor itself is registered as a singleton; the sink is scoped, so
    /// each <see cref="SavingChangesAsync"/> opens a fresh scope to publish into.
    /// Null only when constructed without DI (e.g., a guard test that exercises
    /// <see cref="ArgumentNullException"/> on the logger); reaching the publish
    /// path with a null factory is a wiring bug and throws.
    /// </summary>
    private readonly IServiceScopeFactory? _scopeFactory;

    // Use HashSet for O(1) lookups instead of multiple 'or' checks
    /// <summary>
    /// Audit entity types to exclude from auditing
    /// </summary>
    private static readonly HashSet<Type> _auditEntityTypes =
    [
        typeof(AuditEventEntity),
        typeof(AuditIntegrityEntity),
        typeof(AuditLogEntity),
        typeof(AuditArchiveRecordEntity),
        typeof(AuditSecurityEventEntity),
        typeof(AuditOutboxEntity),
        typeof(AuditIntegrityWorkItemEntity)
    ];

    private static readonly ConcurrentDictionary<Type, bool> _noAuditTypeCache = new();
    private static readonly ConcurrentDictionary<Type, FERPAAttribute?> _ferpaAttributeCache = new();
    private static readonly ConcurrentDictionary<PropertyInfo, PropertyAuditMetadata> _propertyMetadataCache = new();

    private static readonly JsonSerializerOptions _snapshotSerializerOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 8,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static bool HasNoAuditAttribute(Type type) => _noAuditTypeCache.GetOrAdd(type, static t => t.GetCustomAttribute<NoAuditAttribute>() != null);

    private static FERPAAttribute? GetFERPAAttribute(Type type) => _ferpaAttributeCache.GetOrAdd(type, static t => t.GetCustomAttribute<FERPAAttribute>());

    private static PropertyAuditMetadata GetPropertyMetadata(PropertyInfo? propertyInfo)
    {
        if (propertyInfo == null) return default;
        return _propertyMetadataCache.GetOrAdd(propertyInfo, static p =>
        {
            var encrypted = p.GetCustomAttribute<EncryptedFieldAttribute>();
            var sensitive = p.GetCustomAttribute<SensitiveDataAttribute>();
            return new PropertyAuditMetadata
            {
                IsNoAudit = p.GetCustomAttribute<NoAuditAttribute>() != null,
                IsEncrypted = encrypted is { EncryptInAuditLog: true },
                IsSensitive = sensitive is { MaskInLogs: true },
                MaskPattern = sensitive?.MaskPattern
            };
        });
    }

    private readonly record struct PropertyAuditMetadata
    {
        public bool IsNoAudit { get; init; }
        public bool IsEncrypted { get; init; }
        public bool IsSensitive { get; init; }
        public string? MaskPattern { get; init; }
    }

    /// <summary>
    /// Creates a new instance of the audit save changes interceptor.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="enforcementMode">Compliance enforcement mode. Null when compliance is not configured.</param>
    /// <param name="consentService">Cache-backed consent verification. Null when compliance is not configured.</param>
    /// <param name="diagnostics">Aggregate diagnostic counters. Null when diagnostics are not registered.</param>
    /// <param name="failureMode">Audit failure mode snapshot from <c>AuditOptions.FailureMode</c>. Defaults to <see cref="AuditFailureMode.Permissive"/>.</param>
    /// <param name="failurePolicy">
    /// Policy consulted on audit build failure. Null falls back to a local
    /// <see cref="RegulatedEntityFailurePolicy"/> so direct test construction
    /// still exercises the default regulated-entity detection.
    /// </param>
    /// <param name="scopeFactory">
    /// Scope factory used to resolve a per-save <see cref="IAuditSink"/>. Required
    /// for the audit publish path; passing null is only acceptable for tests that
    /// exercise the logger-null guard before any save runs.
    /// </param>
    public AuditSaveChangesInterceptor(
        ILogger<AuditSaveChangesInterceptor> logger,
        ComplianceEnforcementMode? enforcementMode = null,
        IConsentVerificationService? consentService = null,
        IAuditDiagnostics? diagnostics = null,
        AuditFailureMode failureMode = AuditFailureMode.Permissive,
        IAuditFailurePolicy? failurePolicy = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enforcementMode = enforcementMode;
        _consentService = consentService;
        _diagnostics = diagnostics;
        _failureMode = failureMode;
        _failurePolicy = failurePolicy ?? new RegulatedEntityFailurePolicy();
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Sync SaveChanges is not supported for audited contexts. The interceptor requires
    /// async I/O (sink publish, provider dispatch). Sync saves on contexts with auditable
    /// entities throw to prevent silent audit gaps.
    /// </summary>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (ShouldBypass(eventData))
            return base.SavingChanges(eventData, result);

        var entries = GetAuditableEntries(eventData.Context);
        if (entries is { Count: > 0 })
        {
            _diagnostics?.Increment(AuditDiagnosticCounter.SyncSaveChangesBlocked);

            throw new NotSupportedException(
                $"Synchronous SaveChanges is not supported when auditable entities are being saved. " +
                $"Use SaveChangesAsync instead. Entities: {string.Join(", ", entries.Select(e => e.Entity.GetType().Name).Distinct().Take(5))}");
        }

        return base.SavingChanges(eventData, result);
    }

    /// <summary>
    /// Intercepts SaveChangesAsync to audit entity changes
    /// </summary>
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (ShouldBypass(eventData))
        {
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        // Materialize once — both methods need the same filtered entries
        var entries = GetAuditableEntries(eventData.Context);
        if (entries is { Count: > 0 })
        {
            // Consent enforcement runs BEFORE audit logging.
            // ComplianceViolationException propagates out — it is NOT inside
            // ProcessAuditableEntriesAsync' try/catch that swallows audit failures.
            EnforceConsentRequirements(eventData.Context!, entries);

            await ProcessAuditableEntriesAsync(eventData.Context!, entries, cancellationToken);
            CaptureForProviderDispatch(eventData.Context!, entries);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// Post-save hook: dispatches audit providers for entities that were just saved.
    /// Runs after commit so entity IDs (auto-generated keys) are available.
    /// </summary>
    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await DispatchProvidersAsync(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// Clears pending provider dispatches on save failure to prevent stale entries
    /// from firing on the next successful save of the same context instance.
    /// </summary>
    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is IAuditProviderDispatchSource dispatchSource)
            dispatchSource.PendingProviderDispatches = null;

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    /// <summary>
    /// Determines if the interceptor should be bypassed to prevent circular dependencies
    /// </summary>
    /// <param name="eventData">Event data</param>
    /// <returns>True if should bypass</returns>
    private bool ShouldBypass(DbContextEventData eventData)
    {
        // Nothing to audit if context is null
        if (eventData.Context is not { } context)
        {
            return true;
        }

        // Check if this context implements IAuditBypassable and has bypass flag set
        if (context is IAuditBypassable { BypassAuditInterceptor: true })
        {
            _logger.LogTrace(
                "Bypassing audit interceptor due to bypass flag for context {ContextId}",
                context.ContextId);
            return true;
        }

        // Audit entity filtering is handled in ProcessAuditableEntries via the
        // .Where(!AuditEntityTypes.Contains(...)) clause. Do NOT bypass here —
        // that would skip auditing for regular entities in mixed batches.
        return false;
    }

    /// <summary>
    /// Enforces FERPA consent requirements for entities decorated with [FERPA(RequiresConsent = true)].
    /// Runs BEFORE audit logging so ComplianceViolationException propagates without being caught
    /// by ProcessAuditableEntries' generic exception handler.
    /// <para>
    /// Behavior matrix:
    /// <list type="table">
    /// <item><term>Granted + any mode</term><description>Allow + log</description></item>
    /// <item><term>NotFound + Enforce</term><description>Throw ComplianceViolationException (fail-closed)</description></item>
    /// <item><term>NotFound + AuditOnly</term><description>Allow + log warning + security event</description></item>
    /// <item><term>NotFound + Advisory</term><description>Allow + log warning</description></item>
    /// <item><term>Service error + Enforce</term><description>Throw ComplianceViolationException (fail-closed)</description></item>
    /// <item><term>Service error + AuditOnly</term><description>Allow + log error + security event</description></item>
    /// <item><term>Service error + Advisory</term><description>Allow + log error</description></item>
    /// </list>
    /// </para>
    /// Non-FERPA entities and [FERPA(RequiresConsent = false)] entities are completely unaffected.
    /// </summary>
    private void EnforceConsentRequirements(DbContext context, List<EntityEntry> entries)
    {
        // No enforcement configured — skip entirely
        if (_enforcementMode is null || _consentService is null)
            return;

        var mode = _enforcementMode.Value;

        // Get user ID from the DbContext via IAuditContextSource (set by middleware
        // on AuditDbContext, or computed by any consumer DbContext that
        // implements the interface).
        var userId = (context as IAuditContextSource)?.CurrentUserId;

        foreach (var entry in entries)
        {
            var entityType = entry.Entity.GetType();
            var ferpaAttr = GetFERPAAttribute(entityType);

            // Skip: not FERPA, or doesn't require consent
            if (ferpaAttr is not { RequiresConsent: true })
                continue;

            var entityTypeName = entityType.Name;

            ConsentStatus status;
            try
            {
                // Synchronous read from IMemoryCache — safe for interceptor path.
                // userId can be null when middleware hasn't set it; treat as NotFound.
                status = userId is not null
                    ? _consentService.HasActiveConsent(userId, entityTypeName)
                    : ConsentStatus.NotFound;
            }
            catch (Exception ex)
            {
                // Consent service threw an unexpected exception.
                // Enforce mode: fail-closed — treat as no consent.
                if (mode == ComplianceEnforcementMode.Enforce)
                {
                    throw new ComplianceViolationException(
                        "FERPA",
                        entityTypeName,
                        userId,
                        "34 CFR §99.30",
                        $"FERPA consent verification failed for entity '{entityTypeName}' " +
                        $"(user: {userId ?? "unknown"}). Consent service error in Enforce mode — " +
                        "operation blocked (fail-closed). Regulation: 34 CFR §99.30.",
                        ex);
                }

                if (mode == ComplianceEnforcementMode.AuditOnly)
                {
                    AddComplianceSecurityEvent(
                        context,
                        entityTypeName,
                        userId,
                        "FERPA consent verification service failed for entity " +
                        $"'{entityTypeName}' (user: {userId ?? "unknown"}). " +
                        "Operation allowed under AuditOnly mode.",
                        "ConsentServiceError",
                        ex.GetType().Name);
                }

                _logger.LogError(ex,
                    "FERPA consent verification error for entity {EntityType} (user: {UserId}). " +
                    "Mode: {Mode} — allowing operation to proceed",
                    entityTypeName, userId ?? "unknown", mode);
                continue;
            }

            if (status == ConsentStatus.Granted)
            {
                _logger.LogDebug(
                    "FERPA consent verified for entity {EntityType} (user: {UserId})",
                    entityTypeName, userId);
                continue;
            }

            // ConsentStatus.NotFound
            switch (mode)
            {
                case ComplianceEnforcementMode.Enforce:
                    throw new ComplianceViolationException(
                        "FERPA",
                        entityTypeName,
                        userId,
                        "34 CFR §99.30",
                        $"FERPA consent not found for entity '{entityTypeName}' " +
                        $"(user: {userId ?? "unknown"}). Prior written consent is required under " +
                        "34 CFR §99.30 before disclosing personally identifiable information " +
                        "from education records. Operation blocked.");

                case ComplianceEnforcementMode.AuditOnly:
                    AddComplianceSecurityEvent(
                        context,
                        entityTypeName,
                        userId,
                        $"FERPA consent not found for entity '{entityTypeName}' " +
                        $"(user: {userId ?? "unknown"}). Operation allowed under AuditOnly mode.",
                        "ConsentNotFound");

                    _logger.LogWarning(
                        "FERPA consent not found for entity {EntityType} (user: {UserId}). " +
                        "Mode: AuditOnly — operation allowed and security event created. " +
                        "Regulation: 34 CFR §99.30",
                        entityTypeName, userId ?? "unknown");
                    break;

                default: // Advisory
                    _logger.LogWarning(
                        "FERPA consent not found for entity {EntityType} (user: {UserId}). " +
                        "Mode: Advisory — operation allowed. Regulation: 34 CFR §99.30",
                        entityTypeName, userId ?? "unknown");
                    break;
            }
        }
    }

    private void AddComplianceSecurityEvent(
        DbContext context,
        string entityTypeName,
        string? userId,
        string message,
        string reason,
        string? errorType = null)
    {
        var contextSource = context as IAuditContextSource;
        var details = new Dictionary<string, object?>
        {
            ["standard"] = "FERPA",
            ["regulation"] = "34 CFR §99.30",
            ["entityType"] = entityTypeName,
            ["userId"] = userId,
            ["mode"] = ComplianceEnforcementMode.AuditOnly.ToString(),
            ["reason"] = reason
        };

        if (!string.IsNullOrWhiteSpace(errorType))
        {
            details["errorType"] = errorType;
        }

        var securityEventEntity = context.Model.FindEntityType(typeof(AuditSecurityEventEntity));
        if (securityEventEntity is null)
        {
            _logger.LogWarning(
                "Cannot add compliance security event: AuditSecurityEventEntity is not in the " +
                "consumer context's model. Entity: {EntityType}, User: {UserId}, Reason: {Reason}. " +
                "Consider deriving from AuditDbContext for full compliance event tracking.",
                entityTypeName, userId ?? "unknown", reason);
            return;
        }

        context.Set<AuditSecurityEventEntity>().Add(new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.ComplianceViolation,
            Severity = SecurityEventSeverity.High,
            Message = TruncateSafe(message, 500) ?? message,
            DetailsJson = TruncateSafe(JsonSerializer.Serialize(details, _snapshotSerializerOptions), 4000),
            DetectedAt = DateTimeOffset.UtcNow,
            DetectedBy = nameof(AuditSaveChangesInterceptor),
            IpAddress = contextSource?.CurrentIpAddress,
            Status = SecurityEventStatus.Open
        });
    }

    /// <summary>
    /// Materializes the filtered list of auditable change tracker entries once.
    /// Returns null if the context is null.
    /// </summary>
    private static List<EntityEntry>? GetAuditableEntries(DbContext? context)
    {
        if (context == null)
            return null;

        return context.ChangeTracker.Entries()
            .Where(static e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(static e => !_auditEntityTypes.Contains(e.Entity.GetType()))
            .Where(static e => !HasNoAuditAttribute(e.Entity.GetType()))
            .ToList();
    }

    /// <summary>
    /// Processes auditable entries by building one <see cref="AuditEnvelope"/> per
    /// entity entry and publishing it through the registered <see cref="IAuditSink"/>.
    /// For entities decorated with [FERPA], the envelope description carries the
    /// <c>[FERPA]</c> prefix and FERPA metadata rides on <see cref="AuditEnvelope.AdditionalData"/>;
    /// the writer fans these into one row per changed property for Modified entries.
    /// Non-FERPA entries are unaffected.
    ///
    /// Known limitation: This interceptor hooks SaveChanges and can only audit write
    /// operations (Create, Update, Delete). FERPA §99.10 requires logging who accessed
    /// records, not just who modified them. Read auditing requires a separate mechanism
    /// (middleware, EF query interceptor, or application-level logging).
    /// </summary>
    private async Task ProcessAuditableEntriesAsync(
        DbContext context,
        List<EntityEntry> auditableEntries,
        CancellationToken cancellationToken)
    {
        // Capture entity context up front so the fail-closed path can name
        // regulated entities even if envelope materialization throws partway through.
        var entityContext = auditableEntries
            .Select(static entry => new AuditFailureEntity(
                entry.Entity.GetType(),
                MapAction(entry.State).ToString()))
            .ToList();

        var contextSource = context as IAuditContextSource;
        var correlationId = contextSource?.CurrentCorrelationId;

        try
        {
            if (_scopeFactory is null)
            {
                throw new InvalidOperationException(
                    $"{nameof(AuditSaveChangesInterceptor)} was constructed without an " +
                    $"{nameof(IServiceScopeFactory)}. The audit publish path requires one " +
                    "to resolve a scoped IAuditSink. Register the interceptor through " +
                    "MillWorksAuditBuilder.UseEntityFramework() or pass a scope factory " +
                    "to the constructor in tests.");
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var sink = scope.ServiceProvider.GetRequiredService<IAuditSink>();
            var accessor = scope.ServiceProvider.GetRequiredService<IConsumerDbContextAccessor>();

            using (accessor.SetCurrent(context))
            {
                var envelopes = new List<AuditEnvelope>(auditableEntries.Count);
                foreach (var entry in auditableEntries)
                {
                    var envelope = BuildEnvelope(entry, contextSource);
                    if (envelope is not null)
                        envelopes.Add(envelope);
                }

                if (envelopes.Count > 0)
                    await sink.PublishBatchAsync(envelopes, cancellationToken);
            }
        }
        catch (Exception ex) when (ContainsAuditOutboxAtomicityException(ex))
        {
            // A transactional-outbox atomicity violation is a fatal configuration/contract
            // error, not a transient audit-write failure. It must propagate regardless of
            // AuditFailureMode — swallowing it in permissive mode would let the business write
            // commit with no durable audit row, the exact false-evidence outcome that the
            // TransactionalOutbox sink mode exists to prevent.
            throw;
        }
        catch (Exception ex)
        {
            _diagnostics?.Increment(AuditDiagnosticCounter.InterceptorAuditFailure);

            var failureContext = new AuditFailureContext(_failureMode, entityContext);
            var shouldFailClosed = _failurePolicy.ShouldFailClosed(failureContext);

            if (shouldFailClosed)
            {
                // For FailClosedForRegulated, name the first regulated entity so the
                // exception points to the actual trigger in mixed batches. Otherwise
                // (FailClosedAlways), name the first entity.
                var failureEntity = _failureMode == AuditFailureMode.FailClosedForRegulated
                    ? entityContext.FirstOrDefault(e => _failurePolicy.ShouldFailClosed(
                        new AuditFailureContext(AuditFailureMode.FailClosedForRegulated, [e])))
                    : entityContext.FirstOrDefault();

                _logger.LogError(ex,
                    "Error processing auditable entries; failing closed for entity {EntityName} action {Action} under failure mode {FailureMode} with correlation id {CorrelationId}",
                    failureEntity?.EntityType.Name ?? "Unknown",
                    failureEntity?.Action ?? "Unknown",
                    _failureMode,
                    correlationId);

                throw new AuditIntegrityException(
                    failureEntity?.EntityType.Name ?? "Unknown",
                    failureEntity?.Action ?? "Unknown",
                    "Failed to build audit log records during SaveChanges.",
                    ex);
            }

            _logger.LogError(ex,
                "Error processing auditable entries; permissive mode swallowed audit failure under failure mode {FailureMode} with correlation id {CorrelationId}",
                _failureMode,
                correlationId);
            // Permissive: log and swallow.
        }
    }

    private static bool ContainsAuditOutboxAtomicityException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AuditOutboxAtomicityException)
                return true;
        }

        return exception is AggregateException aggregate &&
               aggregate.InnerExceptions.Any(ContainsAuditOutboxAtomicityException);
    }

    /// <summary>
    /// Constructs the <see cref="AuditEnvelope"/> for a single change-tracker entry.
    /// One envelope per entry: Modified entries carry the per-property diff list on
    /// <see cref="AuditEnvelope.PropertyChanges"/>; Added/Deleted entries carry the
    /// snapshot JSON on <see cref="AuditEnvelope.AdditionalData"/>. Returns null
    /// when a Modified entry has no surviving property diffs (all properties either
    /// <c>[NoAudit]</c> or unchanged) — preserving the prior behavior of emitting
    /// zero rows for that entry.
    /// </summary>
    private AuditEnvelope? BuildEnvelope(EntityEntry entry, IAuditContextSource? contextSource)
    {
        var entityType = entry.Entity.GetType();
        var entityName = entityType.Name;
        var action = MapAction(entry.State);
        var (entityId, entityIdString) = GetPrimaryKeyValue(entry);
        var ferpaAttr = GetFERPAAttribute(entityType);

        _logger.LogDebug(
            "Processing audit entry for {EntityType} with state {State}",
            entityName,
            entry.State);

        var correlationId = contextSource?.CurrentCorrelationId;
        var ipAddress = contextSource?.CurrentIpAddress;
        var userAgent = contextSource?.CurrentUserAgent;
        var userId = contextSource?.CurrentUserId;

        if (entry.State == EntityState.Modified)
        {
            // One change record per modified property for granular tracking; the
            // writer fans these out into one AuditLogEntity row per change.
            var changes = new List<AuditEnvelopePropertyChange>();
            var hasAnyModifiedProperty = false;

            foreach (var prop in entry.Properties.Where(static p => p.IsModified))
            {
                hasAnyModifiedProperty = true;

                var meta = GetPropertyMetadata(prop.Metadata.PropertyInfo);
                if (meta.IsNoAudit)
                    continue;

                // Skip unchanged values (EF sometimes marks properties as modified even if value didn't change).
                // Use type-aware comparison: byte[] needs SequenceEqual since Equals
                // only checks reference equality for arrays, producing false-positive diffs.
                if (AreValuesEqual(prop.OriginalValue, prop.CurrentValue))
                    continue;

                var maskedOld = MaskOrRedact(meta, prop.OriginalValue);
                var maskedNew = MaskOrRedact(meta, prop.CurrentValue);

                changes.Add(new AuditEnvelopePropertyChange(
                    prop.Metadata.Name,
                    TruncateSafe(maskedOld, 4000),
                    TruncateSafe(maskedNew, 4000)));
            }

            if (changes.Count > 0)
            {
                var description = ferpaAttr is not null
                    ? $"[FERPA] Updated {entityName}"
                    : $"Updated {entityName}";

                var additionalData = ferpaAttr is not null
                    ? BuildFerpaAdditionalData(ferpaAttr, entityName, action)
                    : null;

                return new AuditEnvelope
                {
                    EnvelopeId = AuditEnvelope.ComputeDeterministicId(entityName, entityId, entityIdString, action, changes, additionalData),
                    Kind = AuditEnvelopeKind.EntityChange,
                    EntityName = entityName,
                    EntityId = entityId,
                    EntityIdString = entityIdString,
                    Action = action,
                    UserId = userId,
                    CorrelationId = correlationId,
                    IpAddress = ipAddress,
                    UserAgent = userAgent,
                    Description = description,
                    PropertyChanges = changes,
                    AdditionalData = additionalData,
                };
            }

            // Modified entity with no detectable property changes. This can occur when:
            // 1. State was manually set to Modified but values didn't change
            // 2. Disconnected update via Update() where EF seeds OriginalValue=CurrentValue
            // Both cases are indistinguishable here. Log and count for visibility, but don't
            // create a potentially misleading audit record.
            if (hasAnyModifiedProperty)
            {
                _diagnostics?.Increment(AuditDiagnosticCounter.DisconnectedUpdateFallback);
                _logger.LogDebug(
                    "Modified entity {EntityName} has properties marked as modified but no value " +
                    "changes detected (possible disconnected update pattern). No audit record created.",
                    entityName);
            }
            return null;
        }

        // Added / Deleted: snapshot the property values into AdditionalData.
        var properties = entry.Properties
            .Where(static p => !GetPropertyMetadata(p.Metadata.PropertyInfo).IsNoAudit);

        var snapshot = new Dictionary<string, object?>();
        foreach (var prop in properties)
        {
            var rawValue = entry.State == EntityState.Deleted
                ? prop.OriginalValue
                : prop.CurrentValue;
            var meta = GetPropertyMetadata(prop.Metadata.PropertyInfo);
            snapshot[prop.Metadata.Name] = MaskOrRedact(meta, rawValue);
        }

        if (ferpaAttr is not null)
        {
            snapshot["_FerpaEventType"] = FerpaEventTypes.EventTypeBuilder.Build(entityName, entry.State.ToString());
            snapshot["_ConsentRequired"] = ferpaAttr.RequiresConsent;
            snapshot["_RecordType"] = ferpaAttr.RecordType;
        }

        string? snapshotJson = null;
        try
        {
            snapshotJson = TruncateSafe(JsonSerializer.Serialize(snapshot, _snapshotSerializerOptions), 4000);
        }
        catch (Exception ex)
        {
            _diagnostics?.Increment(AuditDiagnosticCounter.SnapshotSerializationFallback);
            if (_diagnostics is not null)
            {
                _logger.LogWarning(ex,
                    "Failed to serialize audit snapshot for {EntityName}. " +
                    "FallbackCount: {FallbackCount}",
                    entityName,
                    _diagnostics.SnapshotSerializationFallbackCount);
            }
            else
            {
                _logger.LogWarning(ex,
                    "Failed to serialize audit snapshot for {EntityName}",
                    entityName);
            }

            // Fall back to a minimal representation so the audit record
            // is never hollow — it always carries at least enough data
            // to identify what changed, even if values couldn't be serialized.
            try
            {
                var fallback = new Dictionary<string, object?>
                {
                    ["_serializationError"] = true,
                    ["_entityName"] = entityName,
                    ["_action"] = entry.State.ToString(),
                    ["_propertyNames"] = snapshot.Keys.ToList()
                };
                snapshotJson = JsonSerializer.Serialize(fallback);
            }
            catch
            {
                _diagnostics?.Increment(AuditDiagnosticCounter.SnapshotSerializationTotalFailure);
                // If even the fallback fails, snapshotJson stays null
            }
        }

        var addedDeletedDescription = ferpaAttr is not null
            ? $"[FERPA] {entry.State} {entityName}"
            : $"{entry.State} {entityName}";

        return new AuditEnvelope
        {
            EnvelopeId = AuditEnvelope.ComputeDeterministicId(entityName, entityId, entityIdString, action, null, snapshotJson),
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = entityName,
            EntityId = entityId,
            EntityIdString = entityIdString,
            Action = action,
            UserId = userId,
            CorrelationId = correlationId,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            Description = addedDeletedDescription,
            AdditionalData = snapshotJson,
        };
    }

    /// <summary>
    /// Builds FERPA-specific additional data JSON for Modified entity audit logs.
    /// Includes the FERPA event type, consent requirement, and record type.
    /// </summary>
    private string? BuildFerpaAdditionalData(FERPAAttribute ferpaAttr, string entityName, AuditAction action)
    {
        try
        {
            var ferpaData = new Dictionary<string, object?>
            {
                ["_FerpaEventType"] = FerpaEventTypes.EventTypeBuilder.Build(entityName, action.ToString()),
                ["_ConsentRequired"] = ferpaAttr.RequiresConsent,
                ["_RecordType"] = ferpaAttr.RecordType
            };
            return JsonSerializer.Serialize(ferpaData, _snapshotSerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to build FERPA additional data for {EntityName} ({Action})",
                entityName, action);
            return null;
        }
    }

    /// <summary>
    /// Captures auditable entries that have registered providers for post-save dispatch.
    /// Must run before save so we can capture old values from the change tracker.
    /// Reuses the already-materialized entry list from GetAuditableEntries.
    /// </summary>
    private static void CaptureForProviderDispatch(DbContext context, List<EntityEntry> auditableEntries)
    {
        if (context is not IAuditProviderDispatchSource dispatchSource || dispatchSource.ScopedServiceProvider == null)
            return;

        var map = dispatchSource.ScopedServiceProvider.GetService<AuditProviderTypeMap>();
        if (map == null) return;

        var dispatches = new List<PendingProviderDispatch>();
        foreach (var entry in auditableEntries)
        {
            var entityTypeName = entry.Entity.GetType().Name;

            if (!map.HasProvider(entityTypeName))
                continue;

            var oldValues = entry.State == EntityState.Modified
                ? entry.Properties.Where(static p => p.IsModified)
                    .ToDictionary(static p => p.Metadata.Name, static p => p.OriginalValue)
                : null;

            dispatches.Add(new PendingProviderDispatch(
                entityTypeName,
                MapAction(entry.State).ToString(),
                entry.Entity,
                oldValues));
        }

        if (dispatches.Count > 0)
            dispatchSource.PendingProviderDispatches = dispatches;
    }

    /// <summary>
    /// Dispatches audit providers for entities that were just saved.
    /// Uses the IAuditProviderDispatcher interface to avoid circular deps.
    /// Note: EF Core DbContext is not thread-safe. PendingProviderDispatches and
    /// IsDispatchingProviders assume single-threaded access per context instance.
    /// </summary>
    private async Task DispatchProvidersAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is not IAuditProviderDispatchSource dispatchSource)
            return;

        // Claim dispatches before the guard check to prevent stale entries from leaking
        // to the next save if we hit re-entrancy.
        var dispatches = dispatchSource.PendingProviderDispatches;
        dispatchSource.PendingProviderDispatches = null;

        // Re-entrancy guard — prevents infinite recursion if a provider triggers another save
        if (dispatchSource.IsDispatchingProviders) return;

        if (dispatches == null || dispatches.Count == 0) return;

        if (dispatchSource.ScopedServiceProvider == null) return;

        var dispatcher = dispatchSource.ScopedServiceProvider.GetService<IAuditProviderDispatcher>();
        if (dispatcher == null) return;

        dispatchSource.IsDispatchingProviders = true;
        try
        {
            await dispatcher.DispatchAsync(dispatches, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error dispatching audit providers");
        }
        finally
        {
            dispatchSource.IsDispatchingProviders = false;
        }
    }

    /// <summary>
    /// Maps EF Core EntityState to AuditAction enum
    /// </summary>
    private static AuditAction MapAction(EntityState state) => state switch
    {
        EntityState.Added => AuditAction.Created,
        EntityState.Modified => AuditAction.Updated,
        EntityState.Deleted => AuditAction.Deleted,
        _ => AuditAction.Unknown
    };

    /// <summary>
    /// Extracts the primary key value from the entry. Returns a Guid when possible,
    /// or a string representation for non-Guid keys (int, long, composite).
    /// </summary>
    private static (Guid? GuidId, string? StringId) GetPrimaryKeyValue(EntityEntry entry)
    {
        var keyProperties = entry.Properties
            .Where(static p => p.Metadata.IsPrimaryKey())
            .ToList();

        if (keyProperties.Count == 0)
            return (null, null);

        if (keyProperties.Count == 1)
        {
            var value = keyProperties[0].CurrentValue;
            return value switch
            {
                Guid guid => (guid, null),
                string s when Guid.TryParse(s, out var parsed) => (parsed, null),
                null => (null, null),
                _ => (null, value.ToString())
            };
        }

        // Composite key: serialize all values as JSON array
        var compositeValues = keyProperties.Select(p => p.CurrentValue).ToArray();
        return (null, JsonSerializer.Serialize(compositeValues));
    }

    /// <summary>
    /// Type-aware equality check that handles byte[] (SequenceEqual) and other reference
    /// types correctly. Standard Equals() uses reference equality for arrays, which produces
    /// false-positive "Modified" audit entries for unchanged byte[] properties.
    /// </summary>
    private static bool AreValuesEqual(object? original, object? current)
    {
        if (ReferenceEquals(original, current)) return true;
        if (original is null || current is null) return false;

        if (original is byte[] originalBytes && current is byte[] currentBytes)
            return originalBytes.AsSpan().SequenceEqual(currentBytes);

        return Equals(original, current);
    }

    /// <summary>
    /// Applies attribute-driven masking to entity property values for the EF interceptor path.
    /// This is deliberately separate from <see cref="IAuditFieldRedactor"/>, which operates on
    /// the audit logger pipeline. Both systems must agree on which fields are sensitive —
    /// see InterceptorRedactionBoundaryTests for the integration test that enforces this.
    /// </summary>
    private static string? MaskOrRedact(PropertyAuditMetadata meta, object? rawValue)
    {
        if (rawValue is null)
            return null;

        if (meta.IsEncrypted)
            return "[ENCRYPTED]";

        if (meta.IsSensitive)
            return meta.MaskPattern ?? "***";

        return rawValue.ToString();
    }

    /// <summary>
    /// Truncates a string to the specified maximum length without splitting surrogate pairs.
    /// </summary>
    private static string? TruncateSafe(string? value, int maxLength)
    {
        if (value == null || value.Length <= maxLength) return value;
        if (maxLength <= 0) return string.Empty;

        var truncated = value[..maxLength];
        if (char.IsHighSurrogate(truncated[^1]))
            truncated = truncated[..^1];

        return truncated;
    }
}
