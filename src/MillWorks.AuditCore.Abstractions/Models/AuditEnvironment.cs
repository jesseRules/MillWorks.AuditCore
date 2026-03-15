using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Models;

/// <summary>
/// DTO containing environment and context information for audit events.
/// </summary>
public sealed class AuditEnvironment
{
    /// <summary>
    /// Username of the person or service account that triggered this event.
    /// </summary>
    [MaxLength(255)]
    [Display(Name = "User Name")]
    [JsonPropertyName("user_name")]
    public string? UserName { get; set; }

    /// <summary>
    /// Name of the machine or server where this event occurred.
    /// </summary>
    [MaxLength(255)]
    [Display(Name = "Machine Name")]
    [JsonPropertyName("machine_name")]
    public string? MachineName { get; init; }

    /// <summary>
    /// Domain name of the user or machine context.
    /// </summary>
    [MaxLength(255)]
    [Display(Name = "Domain Name")]
    [JsonPropertyName("domain_name")]
    public string? DomainName { get; init; }

    /// <summary>
    /// Name of the method or function that initiated this audit event.
    /// </summary>
    [MaxLength(255)]
    [Display(Name = "Calling Method Name")]
    [JsonPropertyName("calling_method_name")]
    public string? CallingMethodName { get; init; }

    /// <summary>
    /// Name of the assembly or module that generated this audit event.
    /// </summary>
    [MaxLength(255)]
    [Display(Name = "Assembly Name")]
    [JsonPropertyName("assembly_name")]
    public string? AssemblyName { get; init; }

    /// <summary>
    /// Culture or locale information for the context in which this event occurred.
    /// </summary>
    [MaxLength(50)]
    [Display(Name = "Culture")]
    [JsonPropertyName("culture")]
    public string? Culture { get; init; }
}