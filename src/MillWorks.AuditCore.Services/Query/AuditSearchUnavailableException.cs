namespace MillWorks.AuditCore.Services.Query;

/// <summary>Text search cannot run until the database full-text index is ready.</summary>
public sealed class AuditSearchUnavailableException() : InvalidOperationException(
    "Audit word search is unavailable until its full-text index is installed and populated. Exact identifier and metadata filters remain available.");
