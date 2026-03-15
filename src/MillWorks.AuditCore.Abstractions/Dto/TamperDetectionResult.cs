using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;


/// <summary>
/// DTO containing the results of tamper detection analysis on audit events.
/// </summary>
public sealed class TamperDetectionResult
{
    /// <summary>
    /// Indicates whether the audit chain is valid and untampered.
    /// </summary>
    [Required]
    [Display(Name = "Is Valid")]
    [JsonPropertyName("is_valid")]
    public bool IsValid { get; set; }

    /// <summary>
    /// Indicates whether the cryptographic chain of events has been broken.
    /// </summary>
    [Required]
    [Display(Name = "Chain Broken")]
    [JsonPropertyName("chain_broken")]
    public bool ChainBroken { get; set; }

    /// <summary>
    /// Total number of events in the audit chain.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Total Events")]
    [JsonPropertyName("total_events")]
    public int TotalEvents { get; set; }

    /// <summary>
    /// Number of events that were checked for tampering.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Events Checked")]
    [JsonPropertyName("events_checked")]
    public int EventsChecked { get; set; }

    /// <summary>
    /// List of events that were detected as tampered or suspicious.
    /// </summary>
    [Display(Name = "Tampered Events")]
    [JsonPropertyName("tampered_events")]
    public List<TamperedEvent> TamperedEvents { get; set; } = new();

    /// <summary>
    /// Start date of the verification period.
    /// </summary>
    [Required]
    [Display(Name = "Start Date")]
    [JsonPropertyName("start_date")]
    public DateTimeOffset StartDate { get; set; }

    /// <summary>
    /// End date of the verification period.
    /// </summary>
    [Required]
    [Display(Name = "End Date")]
    [JsonPropertyName("end_date")]
    public DateTimeOffset EndDate { get; set; }

    /// <summary>
    /// UTC timestamp when the tamper verification was performed.
    /// </summary>
    [Required]
    [Display(Name = "Verification Time")]
    [JsonPropertyName("verification_time")]
    public DateTimeOffset VerificationTime { get; set; }
}