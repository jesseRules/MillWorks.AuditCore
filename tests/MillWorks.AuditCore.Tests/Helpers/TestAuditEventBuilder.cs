using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Tests.Helpers;

/// <summary>
/// Fluent builder for creating test AuditEvent instances with sensible defaults.
/// Reduces boilerplate across unit and integration test files.
/// </summary>
public sealed class TestAuditEventBuilder
{
    private readonly AuditEvent _event = new();

    private TestAuditEventBuilder() { }

    /// <summary>
    /// Creates a new builder with sensible defaults already applied.
    /// </summary>
    public static TestAuditEventBuilder Create()
    {
        var builder = new TestAuditEventBuilder();
        builder._event.EventType = "Test.Default";
        builder._event.Action = AuditAction.Created;
        builder._event.EntityName = "TestEntity";
        builder._event.Success = true;
        return builder;
    }

    public TestAuditEventBuilder WithEventType(string eventType)
    {
        _event.EventType = eventType;
        return this;
    }

    public TestAuditEventBuilder WithAction(AuditAction action)
    {
        _event.Action = action;
        return this;
    }

    public TestAuditEventBuilder WithUserId(Guid userId)
    {
        _event.UserId = userId;
        return this;
    }

    public TestAuditEventBuilder WithAspNetUserId(string aspNetUserId)
    {
        _event.AspNetUserId = aspNetUserId;
        return this;
    }

    public TestAuditEventBuilder WithEntityName(string entityName)
    {
        _event.EntityName = entityName;
        return this;
    }

    public TestAuditEventBuilder WithCorrelationId(string correlationId)
    {
        _event.CorrelationId = correlationId;
        return this;
    }

    public TestAuditEventBuilder WithIpAddress(string ipAddress)
    {
        _event.IpAddress = ipAddress;
        return this;
    }

    public TestAuditEventBuilder WithUserAgent(string userAgent)
    {
        _event.UserAgent = userAgent;
        return this;
    }

    public TestAuditEventBuilder WithSuccess(bool success)
    {
        _event.Success = success;
        return this;
    }

    public TestAuditEventBuilder WithError(string errorMessage)
    {
        _event.Success = false;
        _event.ErrorMessage = errorMessage;
        return this;
    }

    public TestAuditEventBuilder WithCustomField(string key, object? value)
    {
        _event.CustomFields[key] = value;
        return this;
    }

    public TestAuditEventBuilder WithSystemField(string key, object? value)
    {
        _event.SystemFields[key] = value;
        return this;
    }

    public TestAuditEventBuilder WithKeyValue(string key, object? value)
    {
        _event.KeyValues[key] = value;
        return this;
    }

    public TestAuditEventBuilder WithOldValue(string key, object? value)
    {
        _event.OldValues[key] = value;
        return this;
    }

    public TestAuditEventBuilder WithNewValue(string key, object? value)
    {
        _event.NewValues[key] = value;
        return this;
    }

    public TestAuditEventBuilder WithChangedProperty(string propertyName)
    {
        _event.ChangedProperties.Add(propertyName);
        return this;
    }

    public TestAuditEventBuilder WithEndDate(DateTimeOffset endDate)
    {
        _event.EndDate = endDate;
        return this;
    }

    /// <summary>
    /// Builds the configured AuditEvent instance.
    /// </summary>
    public AuditEvent Build() => _event;
}
