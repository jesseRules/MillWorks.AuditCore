using System.Text.Json;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// <see cref="IAuditSink"/> implementation that writes audit envelopes to a
/// transactional outbox table inside the consumer's transaction. A background
/// <c>AuditOutboxDrainer</c> reads pending rows and publishes them through
/// <see cref="ImmediateSink"/> to the audit DbContext.
/// </summary>
/// <remarks>
/// This sink is used when <c>SecurityOptions.AuditSinkMode</c> is set to
/// <c>TransactionalOutbox</c>. It provides atomic commit of business + audit
/// data for regulated/zero-loss-durability deployments.
/// </remarks>
internal sealed class TransactionalOutboxSink : IAuditSink
{
    /// <summary>
    /// Current envelope serialization format version. Increment when the
    /// <see cref="AuditEnvelope"/> schema changes incompatibly to allow the
    /// drainer to detect version skew.
    /// </summary>
    internal const int CurrentEnvelopeVersion = 1;

    private readonly IAuditOutboxWriter _outboxWriter;
    private readonly ILogger<TransactionalOutboxSink> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public TransactionalOutboxSink(
        IAuditOutboxWriter outboxWriter,
        ILogger<TransactionalOutboxSink> logger)
    {
        _outboxWriter = outboxWriter;
        _logger = logger;
    }

    public async Task PublishAsync(
        AuditEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var json = JsonSerializer.Serialize(envelope, _jsonOptions);
        await _outboxWriter.WriteAsync(json, CurrentEnvelopeVersion, cancellationToken);

        _logger.LogDebug(
            "Wrote outbox row for {Kind} envelope, entity {EntityName}",
            envelope.Kind,
            envelope.EntityName);
    }
}
