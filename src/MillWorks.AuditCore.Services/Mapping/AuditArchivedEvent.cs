using System.Text.Json.Serialization;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Mapping;

/// <summary>Private archive transport retaining event content and ownership. Never returned by query or disclosure APIs.</summary>
internal sealed class AuditArchivedEvent
{
    [JsonPropertyName("event_id")]
    public Guid EventId { get; set; }
    [JsonPropertyName("entity_id")]
    public string? EntityId { get; set; }
    [JsonPropertyName("inserted_date")]
    public DateTimeOffset? InsertedDate { get; set; }
    [JsonPropertyName("last_updated_date")]
    public DateTimeOffset? LastUpdatedDate { get; set; }
    [JsonPropertyName("json_data")]
    public string? JsonData { get; set; }
    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }
    [JsonPropertyName("user")]
    public string? User { get; set; }
    [JsonPropertyName("user_env_name")]
    public string? UserEnvName { get; set; }
    [JsonPropertyName("user_id")]
    public Guid? UserId { get; set; }
    [JsonPropertyName("asp_net_user_id")]
    public string? AspNetUserId { get; set; }
    [JsonPropertyName("user_full_name")]
    public string? UserFullName { get; set; }
    [JsonPropertyName("start_date")]
    public DateTimeOffset? StartDate { get; set; }
    [JsonPropertyName("end_date")]
    public DateTimeOffset? EndDate { get; set; }
    [JsonPropertyName("duration")]
    public int? Duration { get; set; }
    [JsonPropertyName("entity_type")]
    public string? EntityType { get; set; }
    [JsonPropertyName("action")]
    public string? Action { get; set; }
    [JsonPropertyName("additional_data")]
    public string? AdditionalData { get; set; }
    [JsonPropertyName("environment")]
    public string? Environment { get; set; }
    [JsonPropertyName("machine_name")]
    public string? MachineName { get; set; }
    [JsonPropertyName("calling_method_name")]
    public string? CallingMethodName { get; set; }
    [JsonPropertyName("assembly_name")]
    public string? AssemblyName { get; set; }
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }
    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }
    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; set; }
    [JsonPropertyName("request_path")]
    public string? RequestPath { get; set; }
    [JsonPropertyName("request_method")]
    public string? RequestMethod { get; set; }
    [JsonPropertyName("tenant_id")]
    public Guid? TenantId { get; set; }
    [JsonPropertyName("integrity_status")]
    public IntegrityStatus IntegrityStatus { get; set; }

    public static AuditArchivedEvent From(AuditEventEntity entity) => new()
    {
        EventId = entity.EventId,
        EntityId = entity.EntityId,
        InsertedDate = entity.InsertedDate,
        LastUpdatedDate = entity.LastUpdatedDate,
        JsonData = entity.JsonData,
        EventType = entity.EventType,
        User = entity.User,
        UserEnvName = entity.UserEnvName,
        UserId = entity.UserId,
        AspNetUserId = entity.AspNetUserId,
        UserFullName = entity.UserFullName,
        StartDate = entity.StartDate,
        EndDate = entity.EndDate,
        Duration = entity.Duration,
        EntityType = entity.EntityType,
        Action = entity.Action,
        AdditionalData = entity.AdditionalData,
        Environment = entity.Environment,
        MachineName = entity.MachineName,
        CallingMethodName = entity.CallingMethodName,
        AssemblyName = entity.AssemblyName,
        CorrelationId = entity.CorrelationId,
        IpAddress = entity.IpAddress,
        UserAgent = entity.UserAgent,
        RequestPath = entity.RequestPath,
        RequestMethod = entity.RequestMethod,
        TenantId = entity.TenantId,
        IntegrityStatus = entity.IntegrityStatus,
    };

    public AuditEventEntity ToEntity() => new()
    {
        EventId = EventId,
        EntityId = EntityId,
        InsertedDate = InsertedDate,
        LastUpdatedDate = LastUpdatedDate,
        JsonData = JsonData,
        EventType = EventType,
        User = User,
        UserEnvName = UserEnvName,
        UserId = UserId,
        AspNetUserId = AspNetUserId,
        UserFullName = UserFullName,
        StartDate = StartDate,
        EndDate = EndDate,
        Duration = Duration,
        EntityType = EntityType,
        Action = Action,
        AdditionalData = AdditionalData,
        Environment = Environment,
        MachineName = MachineName,
        CallingMethodName = CallingMethodName,
        AssemblyName = AssemblyName,
        CorrelationId = CorrelationId,
        IpAddress = IpAddress,
        UserAgent = UserAgent,
        RequestPath = RequestPath,
        RequestMethod = RequestMethod,
        TenantId = TenantId,
        IntegrityStatus = IntegrityStatus,
    };
}
