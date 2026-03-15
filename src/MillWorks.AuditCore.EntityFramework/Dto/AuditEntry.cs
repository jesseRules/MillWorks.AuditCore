using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace MillWorks.AuditCore.EntityFramework.Dto;

/// <summary>
/// Internal class representing an audit entry for tracking Entity Framework database changes.
/// </summary>
public class AuditEntry
{
    /// <summary>
    /// Name of the entity type being audited (e.g., table name or class name).
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Display(Name = "Entity Name")]
    [JsonPropertyName("entity_name")]
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// The actual entity instance that was modified.
    /// </summary>
    [Required]
    [Display(Name = "Entity")]
    [JsonPropertyName("entity")]
    public object Entity { get; set; } = null!;

    /// <summary>
    /// Entity Framework state indicating the type of change (Added, Modified, Deleted).
    /// </summary>
    [Required]
    [Display(Name = "State")]
    [JsonPropertyName("state")]
    public EntityState State { get; set; }

    /// <summary>
    /// Human-readable description of the action performed (e.g., "CREATE", "UPDATE", "DELETE").
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Display(Name = "Action")]
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Optional identifier of the internal user who performed this action.
    /// </summary>
    [Display(Name = "User ID")]
    [JsonPropertyName("user_id")]
    public Guid? UserId { get; set; }

    /// <summary>
    /// Optional ASP.NET Identity user identifier for the user who performed this action.
    /// </summary>
    [MaxLength(450)]
    [Display(Name = "ASP.NET User ID")]
    [JsonPropertyName("aspnet_user_id")]
    public string? AspNetUserId { get; set; }

    /// <summary>
    /// Dictionary containing the primary key values that identify this entity.
    /// </summary>
    [Display(Name = "Key Values")]
    [JsonPropertyName("key_values")]
    public Dictionary<string, object?> KeyValues { get; set; } = new();

    /// <summary>
    /// Dictionary containing the original property values before the change.
    /// </summary>
    [Display(Name = "Old Values")]
    [JsonPropertyName("old_values")]
    public Dictionary<string, object?> OldValues { get; } = new();

    /// <summary>
    /// Dictionary containing the new property values after the change.
    /// </summary>
    [Display(Name = "New Values")]
    [JsonPropertyName("new_values")]
    public Dictionary<string, object?> NewValues { get; } = new();

    /// <summary>
    /// List of property names that were modified in this change.
    /// </summary>
    [Display(Name = "Changed Properties")]
    [JsonPropertyName("changed_properties")]
    public List<string> ChangedProperties { get; } = new();

    /// <summary>
    /// List of Entity Framework property entries that have temporary values (e.g., auto-generated IDs).
    /// </summary>
    [Display(Name = "Temporary Properties")]
    [JsonPropertyName("temporary_properties")]
    public List<PropertyEntry> TemporaryProperties { get; } = new();

    /// <summary>
    /// Indicates whether this audit entry contains properties with temporary values that need resolution.
    /// </summary>
    [Required]
    [Display(Name = "Has Temporary Properties")]
    [JsonPropertyName("has_temporary_properties")]
    public bool HasTemporaryProperties { get; set; }

    /// <summary>
    /// Reference to the Entity Framework EntityEntry for accessing change tracking information.
    /// </summary>
    [Required]
    [Display(Name = "Entity Entry")]
    [JsonPropertyName("entity_entry")]
    public EntityEntry EntityEntry { get; set; } = null!;
}