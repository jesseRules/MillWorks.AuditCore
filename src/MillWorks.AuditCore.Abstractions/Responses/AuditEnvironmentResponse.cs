using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
///     Entity Response
/// </summary>
public sealed class AuditEnvironmentResponse
{
    /// <summary>
    ///     Username
    /// </summary>
    [JsonPropertyName("user_name")]
    [DisplayName("User Name")]
    public string? UserName { get; set; }

    /// <summary>
    ///     User Domain Name
    /// </summary>
    [JsonPropertyName("user_domain_name")]
    [DisplayName("User Domain Name")]
    public string? UserDomainName { get; set; }

    /// <summary>
    ///     Machine Name
    /// </summary>
    [JsonPropertyName("machine_name")]
    [DisplayName("Machine Name")]
    public string? MachineName { get; set; }

    /// <summary>
    ///     Domain Name
    /// </summary>
    [JsonPropertyName("domain_name")]
    [DisplayName("Domain Name")]
    public string? DomainName { get; set; }

    /// <summary>
    ///     Calling Method Name
    /// </summary>
    [JsonPropertyName("calling_method_name")]
    [DisplayName("Calling Method Name")]
    public string? CallingMethodName { get; set; }

    /// <summary>
    ///     Assembly Name
    /// </summary>
    [JsonPropertyName("assembly_name")]
    [DisplayName("Assembly Name")]
    public string? AssemblyName { get; set; }

    /// <summary>
    ///     Culture
    /// </summary>
    [JsonPropertyName("culture")]
    [DisplayName("Culture")]
    public string? Culture { get; set; }
}
