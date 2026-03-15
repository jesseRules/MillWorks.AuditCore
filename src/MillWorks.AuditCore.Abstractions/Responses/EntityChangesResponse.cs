using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
/// Represents entity changes tracked by EF Core
/// </summary>
public sealed class EntityChangesResponse
{
    /// <summary>
    /// Entity type name
    /// </summary>
    [JsonPropertyName("entity_type")]
    [DisplayName("Entity Type")]
    public string? EntityType { get; set; }

    /// <summary>
    /// Entity ID
    /// </summary>
    [JsonPropertyName("entity_id")]
    [DisplayName("Entity ID")]
    public string? EntityId { get; set; }

    /// <summary>
    /// Action performed (Created, Updated, Deleted)
    /// </summary>
    [JsonPropertyName("action")]
    [DisplayName("Action")]
    public string? Action { get; set; }

    /// <summary>
    /// Entity state from EF Core
    /// </summary>
    [JsonPropertyName("entity_state")]
    [DisplayName("Entity State")]
    public string? EntityState { get; set; }

    /// <summary>
    /// Key values of the entity
    /// </summary>
    [JsonPropertyName("key_values")]
    [DisplayName("Key Values")]
    public Dictionary<string, object?>? KeyValues { get; set; }

    /// <summary>
    /// Properties that changed
    /// </summary>
    [JsonPropertyName("changed_properties")]
    [DisplayName("Changed Properties")]
    public List<string>? ChangedProperties { get; set; }

    /// <summary>
    /// Old values of changed properties
    /// </summary>
    [JsonPropertyName("old_values")]
    [DisplayName("Old Values")]
    public Dictionary<string, object?>? OldValues { get; set; }

    /// <summary>
    /// New values of changed properties
    /// </summary>
    [JsonPropertyName("new_values")]
    [DisplayName("New Values")]
    public Dictionary<string, object?>? NewValues { get; set; }
}