using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO defining data retention policies for different types of audit events.
/// </summary>
public class AuditRetentionPolicy
{
    /// <summary>
    /// Unique identifier for this policy
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Type of event this retention policy applies to.
    /// Supports wildcards: "User.*" matches "User.Login", "User.Logout", etc.
    /// Use "*" to match all event types.
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Event Type")]
    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Number of days to retain events of this type before archival or deletion.
    /// </summary>
    [Required]
    [Range(1, 7300, ErrorMessage = "Retention days must be between 1 and 7300 (20 years)")]
    [Display(Name = "Retention Days")]
    [JsonPropertyName("retention_days")]
    public int RetentionDays { get; set; }

    /// <summary>
    /// Indicates whether events should be archived before deletion.
    /// If true, events are moved to archive storage before being deleted from active storage.
    /// If false, events are permanently deleted.
    /// </summary>
    [Required]
    [Display(Name = "Archive Before Delete")]
    [JsonPropertyName("archive_before_delete")]
    public bool ArchiveBeforeDelete { get; set; }

    /// <summary>
    /// Priority for applying policies when multiple policies match an event type.
    /// Higher values are applied first. Default is 0.
    /// </summary>
    [Range(0, 100)]
    [Display(Name = "Priority")]
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 0;

    /// <summary>
    /// Whether this policy is currently active and should be applied.
    /// </summary>
    [Display(Name = "Is Active")]
    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional description of this policy's purpose
    /// </summary>
    [MaxLength(500)]
    [Display(Name = "Description")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// When this policy was created
    /// </summary>
    [Display(Name = "Created At")]
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this policy was last modified
    /// </summary>
    [Display(Name = "Last Modified At")]
    [JsonPropertyName("last_modified_at")]
    public DateTimeOffset? LastModifiedAt { get; set; }

    /// <summary>
    /// User who created this policy
    /// </summary>
    [MaxLength(100)]
    [Display(Name = "Created By")]
    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// Validates the policy configuration
    /// </summary>
    public bool IsValid(out List<string> errors)
    {
        errors = new List<string>();

        if (string.IsNullOrWhiteSpace(EventType))
        {
            errors.Add("Event type cannot be empty");
        }

        if (EventType.Length > 100)
        {
            errors.Add("Event type cannot exceed 100 characters");
        }

        if (RetentionDays < 1 || RetentionDays > 7300)
        {
            errors.Add("Retention days must be between 1 and 7300");
        }

        if (Priority < 0 || Priority > 100)
        {
            errors.Add("Priority must be between 0 and 100");
        }

        if (!string.IsNullOrEmpty(Description) && Description.Length > 500)
        {
            errors.Add("Description cannot exceed 500 characters");
        }

        return errors.Count == 0;
    }

    /// <summary>
    /// Checks if this policy matches the given event type
    /// </summary>
    public bool MatchesEventType(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return false;

        // Exact match
        if (EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase))
            return true;

        // Wildcard match all
        if (EventType == "*")
            return true;

        // Prefix wildcard: "User.*" matches "User.Login", "User.Logout", etc.
        if (EventType.EndsWith(".*"))
        {
            var prefix = EventType[..^2]; // Remove ".*"
            return eventType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Suffix wildcard: "*.Login" matches "User.Login", "Admin.Login", etc.
        if (EventType.StartsWith("*."))
        {
            var suffix = EventType[2..]; // Remove "*."
            return eventType.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Returns a string representation of this policy
    /// </summary>
    public override string ToString()
    {
        return $"RetentionPolicy: {EventType} - {RetentionDays} days " +
               $"(Archive: {ArchiveBeforeDelete}, Active: {IsActive}, Priority: {Priority})";
    }
}