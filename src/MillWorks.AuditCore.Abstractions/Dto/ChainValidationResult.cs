namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Result of validating an integrity chain range.
/// Distinguishes between "valid", "invalid", and "no data" states.
/// </summary>
public sealed class ChainValidationResult
{
    /// <summary>
    /// Whether the chain is valid. False if empty, truncated, or broken.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// True if the requested range contained no records.
    /// Distinct from a broken chain — the caller may want to handle this differently.
    /// </summary>
    public bool IsEmpty { get; init; }

    /// <summary>
    /// Number of records validated (0 if empty).
    /// </summary>
    public int RecordCount { get; init; }

    /// <summary>
    /// Human-readable description of the validation outcome.
    /// Set when IsValid is false to explain why.
    /// </summary>
    public string? Message { get; init; }
}
