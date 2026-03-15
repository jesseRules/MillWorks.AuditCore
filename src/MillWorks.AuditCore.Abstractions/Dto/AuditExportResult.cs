using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO representing the result of a user data export operation.
/// </summary>
public sealed class AuditExportResult
{
    /// <summary>
    /// Identifier of the user whose data was exported.
    /// </summary>
    [Required]
    [Display(Name = "User ID")]
    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    /// <summary>
    /// Indicates whether the export operation was successful.
    /// </summary>
    [Required]
    [Display(Name = "Success")]
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Message describing the result of the export operation.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Display(Name = "Message")]
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Binary data of the exported user information.
    /// </summary>
    [Required]
    [Display(Name = "Data")]
    [JsonPropertyName("data")]
    public byte[] Data { get; set; } = [];

    /// <summary>
    /// Suggested filename for the exported data.
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Display(Name = "File Name")]
    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Number of records included in the export.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Record Count")]
    [JsonPropertyName("record_count")]
    public int RecordCount { get; set; }
}