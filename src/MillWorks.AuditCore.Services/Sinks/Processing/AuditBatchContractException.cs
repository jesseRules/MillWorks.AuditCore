namespace MillWorks.AuditCore.Services.Sinks.Processing;

/// <summary>A malformed internal result supplies no evidence for row transitions.</summary>
public sealed class AuditBatchContractException(string message) : InvalidOperationException(message);
