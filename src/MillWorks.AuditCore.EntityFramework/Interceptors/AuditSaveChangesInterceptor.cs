using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Constants;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
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
        typeof(AuditSecurityEventEntity)
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

    private static bool HasNoAuditAttribute(Type type)
    {
        return _noAuditTypeCache.GetOrAdd(type, static t => t.GetCustomAttribute<NoAuditAttribute>() != null);
    }

    private static FERPAAttribute? GetFERPAAttribute(Type type)
    {
        return _ferpaAttributeCache.GetOrAdd(type, static t => t.GetCustomAttribute<FERPAAttribute>());
    }

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
    public AuditSaveChangesInterceptor(
        ILogger<AuditSaveChangesInterceptor> logger,
        ComplianceEnforcementMode? enforcementMode = null,
        IConsentVerificationService? consentService = null,
        IAuditDiagnostics? diagnostics = null,
        AuditFailureMode failureMode = AuditFailureMode.Permissive,
        IAuditFailurePolicy? failurePolicy = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enforcementMode = enforcementMode;
        _consentService = consentService;
        _diagnostics = diagnostics;
        _failureMode = failureMode;
        _failurePolicy = failurePolicy ?? new RegulatedEntityFailurePolicy();
    }

    // NOTE: No sync SavingChanges override. The sync path lacks provider dispatch
    // (CaptureForProviderDispatch + SavedChanges), so it would silently produce
    // partial audit records. Letting it fall through to base makes the gap obvious.

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
            // ProcessAuditableEntries' try/catch that swallows audit failures.
            EnforceConsentRequirements(eventData.Context!, entries);

            ProcessAuditableEntries(eventData.Context!, entries);
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
        if (eventData.Context is AuditApplicationDbContext auditCtx)
            auditCtx.PendingProviderDispatches = null;

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
    /// <item><term>NotFound + AuditOnly</term><description>Allow + log warning</description></item>
    /// <item><term>NotFound + Advisory</term><description>Allow + log warning</description></item>
    /// <item><term>Service error + Enforce</term><description>Throw ComplianceViolationException (fail-closed)</description></item>
    /// <item><term>Service error + AuditOnly/Advisory</term><description>Allow + log error</description></item>
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

        // Get user ID from the DbContext (set by middleware)
        var userId = (context as AuditApplicationDbContext)?.CurrentUserId;

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
                    _logger.LogWarning(
                        "FERPA consent not found for entity {EntityType} (user: {UserId}). " +
                        "Mode: AuditOnly — operation allowed, security event should be created. " +
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

    /// <summary>
    /// Materializes the filtered list of auditable change tracker entries once.
    /// Returns null if the context is null or doesn't include AuditLogEntity.
    /// </summary>
    private static List<EntityEntry>? GetAuditableEntries(DbContext? context)
    {
        if (context == null)
            return null;

        // Check if the context model includes AuditLogEntity
        if (context.Model.FindEntityType(typeof(AuditLogEntity)) == null)
            return null;

        return context.ChangeTracker.Entries()
            .Where(static e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(static e => !_auditEntityTypes.Contains(e.Entity.GetType()))
            .Where(static e => !HasNoAuditAttribute(e.Entity.GetType()))
            .ToList();
    }

    /// <summary>
    /// Processes auditable entries and creates audit log records.
    /// For entities decorated with [FERPA], uses FERPA-prefixed event types and
    /// adds consent metadata. This is additive — non-FERPA entities are unaffected.
    ///
    /// Known limitation: This interceptor hooks SaveChanges and can only audit write
    /// operations (Create, Update, Delete). FERPA §99.10 requires logging who accessed
    /// records, not just who modified them. Read auditing requires a separate mechanism
    /// (middleware, EF query interceptor, or application-level logging).
    /// </summary>
    private void ProcessAuditableEntries(DbContext context, List<EntityEntry> auditableEntries)
    {
        // Capture entity context up front so the fail-closed path can name
        // regulated entities even if audit-log materialization throws partway through.
        var entityContext = auditableEntries
            .Select(static entry => new AuditFailureEntity(
                entry.Entity.GetType(),
                MapAction(entry.State).ToString()))
            .ToList();

        var auditCtx = context as AuditApplicationDbContext;
        var correlationId = auditCtx?.CurrentCorrelationId;
        var ipAddress = auditCtx?.CurrentIpAddress;
        var userAgent = auditCtx?.CurrentUserAgent;

        try
        {
            var auditLogs = context.Set<AuditLogEntity>();

            foreach (var entry in auditableEntries)
            {
                var entityType = entry.Entity.GetType();
                var entityName = entityType.Name;
                var action = MapAction(entry.State);
                var entityId = GetPrimaryKeyValue(entry);

                // Check for [FERPA] attribute — cached per type
                var ferpaAttr = GetFERPAAttribute(entityType);

                _logger.LogDebug(
                    "Processing audit entry for {EntityType} with state {State}",
                    entityName,
                    entry.State);

                if (entry.State == EntityState.Modified)
                {
                    // One audit log per changed property for granular tracking
                    foreach (var prop in entry.Properties.Where(static p => p.IsModified))
                    {
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

                        var description = ferpaAttr is not null
                            ? $"[FERPA] Updated {prop.Metadata.Name} on {entityName}"
                            : $"Updated {prop.Metadata.Name} on {entityName}";

                        var logEntry = new AuditLogEntity
                        {
                            EntityName = entityName,
                            EntityId = entityId,
                            Action = action,
                            PropertyName = prop.Metadata.Name,
                            OldValue = Truncate(maskedOld, 4000),
                            NewValue = Truncate(maskedNew, 4000),
                            Description = description,
                            CorrelationId = correlationId,
                            IpAddress = ipAddress,
                            UserAgent = userAgent
                        };

                        // For FERPA entities, add consent metadata to AdditionalData
                        if (ferpaAttr is not null)
                        {
                            logEntry.AdditionalData = BuildFerpaAdditionalData(ferpaAttr, entityName, action);
                        }

                        auditLogs.Add(logEntry);
                    }
                }
                else
                {
                    // One audit log for Added/Deleted with property snapshot in AdditionalData
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

                    // For FERPA entities, add FERPA metadata to the snapshot
                    if (ferpaAttr is not null)
                    {
                        snapshot["_FerpaEventType"] = FerpaEventTypes.EventTypeBuilder.Build(entityName, entry.State.ToString());
                        snapshot["_ConsentRequired"] = ferpaAttr.RequiresConsent;
                        snapshot["_RecordType"] = ferpaAttr.RecordType;
                    }

                    string? additionalData = null;
                    try
                    {
                        additionalData = Truncate(JsonSerializer.Serialize(snapshot, _snapshotSerializerOptions), 4000);
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
                            additionalData = JsonSerializer.Serialize(fallback);
                        }
                        catch
                        {
                            _diagnostics?.Increment(AuditDiagnosticCounter.SnapshotSerializationTotalFailure);
                            // If even the fallback fails, additionalData stays null
                        }
                    }

                    var description = ferpaAttr is not null
                        ? $"[FERPA] {entry.State} {entityName}"
                        : $"{entry.State} {entityName}";

                    auditLogs.Add(new AuditLogEntity
                    {
                        EntityName = entityName,
                        EntityId = entityId,
                        Action = action,
                        Description = description,
                        AdditionalData = additionalData,
                        CorrelationId = correlationId,
                        IpAddress = ipAddress,
                        UserAgent = userAgent
                    });
                }
            }
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
        if (context is not AuditApplicationDbContext auditCtx || auditCtx.ScopedServiceProvider == null)
            return;

        var map = auditCtx.ScopedServiceProvider.GetService<AuditProviderTypeMap>();
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
            auditCtx.PendingProviderDispatches = dispatches;
    }

    /// <summary>
    /// Dispatches audit providers for entities that were just saved.
    /// Uses the IAuditProviderDispatcher interface to avoid circular deps.
    /// Note: EF Core DbContext is not thread-safe. PendingProviderDispatches and
    /// IsDispatchingProviders assume single-threaded access per context instance.
    /// </summary>
    private async Task DispatchProvidersAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is not AuditApplicationDbContext auditCtx)
            return;

        // Re-entrancy guard — prevents infinite recursion if a provider triggers another save
        if (auditCtx.IsDispatchingProviders) return;

        var dispatches = auditCtx.PendingProviderDispatches;
        auditCtx.PendingProviderDispatches = null;
        if (dispatches == null || dispatches.Count == 0) return;

        if (auditCtx.ScopedServiceProvider == null) return;

        var dispatcher = auditCtx.ScopedServiceProvider.GetService<IAuditProviderDispatcher>();
        if (dispatcher == null) return;

        auditCtx.IsDispatchingProviders = true;
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
            auditCtx.IsDispatchingProviders = false;
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
    /// Extracts the primary key value as a Guid from the entry.
    /// </summary>
    private static Guid? GetPrimaryKeyValue(EntityEntry entry)
    {
        var keyProperty = entry.Properties
            .FirstOrDefault(static p => p.Metadata.IsPrimaryKey());

        return keyProperty?.CurrentValue switch
        {
            Guid guid => guid,
            string s when Guid.TryParse(s, out var parsed) => parsed,
            _ => null
        };
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
    /// Truncates a string to the specified maximum length
    /// </summary>
    private static string? Truncate(string? value, int maxLength)
    {
        if (value == null || value.Length <= maxLength) return value;
        return value[..maxLength];
    }
}
