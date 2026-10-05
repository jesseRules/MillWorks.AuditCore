using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Models;

/// <summary>A stable subject link extracted from typed entity metadata before payload redaction.</summary>
public sealed record AuditSubjectReference
{
    /// <summary>Creates a validated subject reference.</summary>
    [JsonConstructor]
    public AuditSubjectReference(Guid subjectId, string relationship)
    {
        if (subjectId == Guid.Empty)
            throw new ArgumentException("Subject identity must be non-empty.", nameof(subjectId));
        if (string.IsNullOrWhiteSpace(relationship))
            throw new ArgumentException("Subject relationship is required.", nameof(relationship));

        string normalized = relationship.Trim();
        if (normalized.Length > 128)
            throw new ArgumentException("Subject relationship cannot exceed 128 characters.", nameof(relationship));

        SubjectId = subjectId;
        Relationship = normalized;
    }

    /// <summary>Canonical host subject identifier.</summary>
    [JsonPropertyName("subject_id")]
    public Guid SubjectId { get; }

    /// <summary>Host-defined typed relationship, normally the mapped EF property name.</summary>
    [JsonPropertyName("relationship")]
    public string Relationship { get; }
}
