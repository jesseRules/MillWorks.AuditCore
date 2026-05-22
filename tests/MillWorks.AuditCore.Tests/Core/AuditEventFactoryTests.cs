using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.AspNetCore.Services;
using MillWorks.AuditCore.Services.Options;
using System.Globalization;
using System.Security.Claims;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Tests.Core;

/// <summary>
/// AuditEventFactory tests
/// </summary>
[TestFixture]
public class AuditEventFactoryTests
{
    /// <summary>
    /// Mocked dependencies
    /// </summary>
    private Mock<IHttpContextAccessor> _mockHttpContextAccessor;

    /// <summary>
    /// Audit context
    /// </summary>
    private IAuditContext _auditContext; // Use real instance, not mock

    /// <summary>
    /// Audit options
    /// </summary>
    private AuditOptions _auditOptions;

    /// <summary>
    /// Mocked logger
    /// </summary>
    private Mock<ILogger<AuditEventFactory>> _mockLogger;

    /// <summary>
    /// HTTP context
    /// </summary>
    private DefaultHttpContext _httpContext;

    /// <summary>
    /// Factory under test
    /// </summary>
    private AuditEventFactory _factory;

    /// <summary>
    /// Setup before each test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockHttpContextAccessor = new Mock<IHttpContextAccessor>();
        _auditContext = new AuditContext(); // Real instance
        _auditOptions = new AuditOptions { ApplicationName = "TestApp" };
        _mockLogger = new Mock<ILogger<AuditEventFactory>>();

        _httpContext = new DefaultHttpContext();
        _mockHttpContextAccessor.Setup(static x => x.HttpContext).Returns(_httpContext);

        _factory = new AuditEventFactory(
            _mockHttpContextAccessor.Object,
            _auditContext,
            Options.Create(_auditOptions),
            _mockLogger.Object);
    }

    /// <summary>
    /// Tear down after each test
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        // Clear the context after each test
        _auditContext.Clear();
    }

    /// <summary>
    /// Creates an event with type and data, verifies properties
    /// </summary>
    [Test]
    public void CreateEvent_WithTypeAndData_CreatesEventWithProperties()
    {
        // Arrange
        var eventType = "User.Login";
        var data = new { UserId = "123", IpAddress = "192.168.1.1" };

        _auditContext.UserId = Guid.NewGuid();
        _auditContext.AspNetUserId = "aspnet-123";
        _auditContext.TenantId = Guid.NewGuid();
        _auditContext.UserEmail = "user@example.com";

        // Act
        var result = _factory.CreateEvent(eventType, data);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EventType, Is.EqualTo(eventType));
        Assert.That(result.StartDate, Is.Not.EqualTo(default(DateTimeOffset)));
        Assert.That(result.Target, Is.Not.Null);
        Assert.That(result.Target.New, Is.EqualTo(data));
        Assert.That(result.Environment, Is.Not.Null);
    }

    /// <summary>
    /// Creates an event and verifies custom fields are populated from context
    /// </summary>
    [Test]
    public void CreateEvent_PopulatesCustomFieldsFromContext()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        _auditContext.UserId = userId;
        _auditContext.AspNetUserId = "aspnet-123";
        _auditContext.TenantId = tenantId;
        _auditContext.UserEmail = "user@example.com";
        _auditContext.UserFullName = "Test User";
        _auditContext.CorrelationId = "corr-123";
        _auditContext.IpAddress = "192.168.1.1";
        _auditContext.UserAgent = "Mozilla/5.0";
        _auditContext.RequestPath = "/api/test";
        _auditContext.RequestMethod = "GET";

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.CustomFields.ContainsKey("UserId"), Is.True);
        Assert.That(result.CustomFields["UserId"], Is.EqualTo(userId));
        Assert.That(result.CustomFields["AspNetUserId"], Is.EqualTo("aspnet-123"));
        Assert.That(result.CustomFields["TenantId"], Is.EqualTo(tenantId));
        Assert.That(result.CustomFields["UserEmail"], Is.EqualTo("user@example.com"));
        Assert.That(result.CustomFields["UserFullName"], Is.EqualTo("Test User"));
        Assert.That(result.CustomFields["IpAddress"], Is.EqualTo("192.168.1.1"));
        Assert.That(result.CustomFields["UserAgent"], Is.EqualTo("Mozilla/5.0"));
        Assert.That(result.CustomFields["CorrelationId"], Is.EqualTo("corr-123"));
    }

    /// <summary>
    /// Creates an event with no audit context set, verifies it falls back to HTTP context
    /// </summary>
    [Test]
    public void CreateEvent_WithNoAuditContext_FallsBackToHttpContext()
    {
        // Arrange
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user123"),
            new Claim(ClaimTypes.Email, "user@example.com"),
            new Claim(ClaimTypes.Name, "Test User")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var principal = new ClaimsPrincipal(identity);
        _httpContext.User = principal;
        _httpContext.TraceIdentifier = "trace-123";
        _httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");
        _httpContext.Request.Headers["User-Agent"] = "Mozilla/5.0";
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "POST";

        // Leave audit context empty (default state)

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.CustomFields["AspNetUserId"], Is.EqualTo("user123"));
        Assert.That(result.CustomFields["UserEmail"], Is.EqualTo("user@example.com"));
        Assert.That(result.CustomFields["IpAddress"], Is.EqualTo("192.168.1.100"));
        Assert.That(result.CustomFields["CorrelationId"], Is.EqualTo("trace-123"));
    }

    /// <summary>
    /// Creates an entity event and verifies entity details are included
    /// </summary>
    [Test]
    public void CreateEntityEvent_CreatesEventWithEntityDetails()
    {
        // Arrange
        var entityType = "User";
        var action = "Update";
        var entity = new { Id = 123, Name = "Test User", Email = "test@example.com" };
        var oldValues = new { Id = 123, Name = "Old Name", Email = "old@example.com" };

        _auditContext.UserEmail = "admin@example.com";

        // Act
        var result = _factory.CreateEntityEvent(entityType, action, entity, oldValues);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EventType, Is.EqualTo("User.Update"));
        Assert.That(result.Target, Is.Not.Null);
        Assert.That(result.Target.Type, Is.EqualTo(entityType));
        Assert.That(result.Target.New, Is.EqualTo(entity));
        Assert.That(result.Target.Old, Is.EqualTo(oldValues));
        Assert.That(result.CustomFields["EntityType"], Is.EqualTo(entityType));
        Assert.That(result.CustomFields["EntityId"], Is.EqualTo("123"));
        Assert.That(result.CustomFields["Action"], Is.EqualTo(action));
    }

    /// <summary>
    /// Creates an operation event and verifies operation details are included
    /// </summary>
    [Test]
    public void CreateOperationEvent_CreatesEventWithOperationId()
    {
        // Arrange
        var operationType = "DataProcessing";
        var operationId = Guid.NewGuid();
        var status = "Started";
        var metadata = new { BatchSize = 100, RecordCount = 500 };

        _auditContext.UserEmail = "system@example.com";

        // Act
        var result = _factory.CreateOperationEvent(operationType, operationId, status, metadata);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EventType, Is.EqualTo($"{operationType}.{status}"));
        Assert.That(result.CustomFields.ContainsKey("OperationId"), Is.True);
        Assert.That(result.CustomFields["OperationId"], Is.EqualTo(operationId));
        Assert.That(result.CustomFields["OperationType"], Is.EqualTo(operationType));
        Assert.That(result.CustomFields["Status"], Is.EqualTo(status));
        Assert.That(result.Target, Is.Not.Null);
        Assert.That(result.Target.New, Is.EqualTo(metadata));
    }

    /// <summary>
    /// Creates an event with operation ID in context, verifies parent operation ID is added
    /// </summary>
    [Test]
    public void CreateEvent_WithOperationIdInContext_AddsParentOperationId()
    {
        // Arrange
        var operationId = Guid.NewGuid();

        _auditContext.OperationId = operationId;
        _auditContext.UserEmail = "user@example.com";

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.CustomFields.ContainsKey("ParentOperationId"), Is.True);
        Assert.That(result.CustomFields["ParentOperationId"], Is.EqualTo(operationId));
    }

    /// <summary>
    /// Creates an event with custom data in context, verifies they are added to custom fields
    /// </summary>
    [Test]
    public void CreateEvent_WithCustomDataInContext_AddsToCustomFields()
    {
        // Arrange
        _auditContext.UserEmail = "user@example.com";
        _auditContext.SetData("CustomField1", "Value1");
        _auditContext.SetData("CustomField2", 42);
        _auditContext.SetData("CustomField3", true);

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.CustomFields["CustomField1"], Is.EqualTo("Value1"));
        Assert.That(result.CustomFields["CustomField2"], Is.EqualTo(42));
        Assert.That(result.CustomFields["CustomField3"], Is.EqualTo(true));
    }

    /// <summary>
    /// Creates an event when there is no HTTP context, verifies event is still created
    /// </summary>
    [Test]
    public void CreateEvent_WithNoHttpContext_StillCreatesEvent()
    {
        // Arrange
        _mockHttpContextAccessor.Setup(static x => x.HttpContext).Returns(((HttpContext?)null)!);

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EventType, Is.EqualTo("Test.Event"));
        Assert.That(result.Environment, Is.Not.Null);
        Assert.That(result.Environment.UserName, Is.EqualTo("System"));
    }

    /// <summary>
    /// Creates an event with anonymous user, verifies username is set to "System"
    /// </summary>
    [Test]
    public void CreateEvent_WithAnonymousUser_UsesSystemAsUserName()
    {
        // Arrange
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity()); // Anonymous

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.Environment.UserName, Is.EqualTo("System"));
    }

    /// <summary>
    /// Creates an event and verifies environment properties are populated correctly
    /// </summary>
    [Test]
    public void CreateEvent_PopulatesEnvironmentCorrectly()
    {
        // Arrange
        _auditContext.UserEmail = "user@example.com";

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.Environment, Is.Not.Null);
        Assert.That(result.Environment.MachineName, Is.EqualTo(Environment.MachineName));
        Assert.That(result.Environment.DomainName, Is.EqualTo(Environment.UserDomainName));
        Assert.That(result.Environment.AssemblyName, Is.EqualTo("TestApp"));
        Assert.That(result.Environment.Culture, Is.Not.Null);
        Assert.That(result.Environment.CallingMethodName, Is.Not.Null);
        Assert.That(result.Environment.UserName, Is.EqualTo("user@example.com"));
    }

    /// <summary>
    /// Creates an entity event with an entity that lacks an Id property, verifies EntityId is set to "Unknown"
    /// </summary>
    [Test]
    public void CreateEntityEvent_WithEntityWithoutIdProperty_UsesUnknownAsEntityId()
    {
        // Arrange
        var entityType = "SimpleEntity";
        var action = "Create";
        var entity = new { Name = "Test", Value = 100 }; // No Id property

        _auditContext.UserEmail = "user@example.com";

        // Act
        var result = _factory.CreateEntityEvent(entityType, action, entity);

        // Assert
        Assert.That(result.CustomFields["EntityId"], Is.EqualTo("Unknown"));
    }

    /// <summary>
    /// Creates an event with null target, verifies event is created without target
    /// </summary>
    [Test]
    public void CreateEvent_WithNullTarget_CreatesEventWithoutTarget()
    {
        // Arrange
        _auditContext.UserEmail = "user@example.com";

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Target, Is.Null);
    }

    /// <summary>
    /// Creates an event with authenticated HTTP context user, verifies user info is enriched
    /// </summary>
    [Test]
    public void CreateEvent_WithAuthenticatedHttpContextUser_EnrichesWithUserInfo()
    {
        // Arrange
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "http-user-123"),
            new Claim(ClaimTypes.Email, "httpuser@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        _httpContext.User = new ClaimsPrincipal(identity);

        // Don't set audit context user info to trigger fallback

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.CustomFields["AspNetUserId"], Is.EqualTo("http-user-123"));
        Assert.That(result.CustomFields["UserEmail"], Is.EqualTo("httpuser@example.com"));
    }

    /// <summary>
    /// Creates an event when both audit context and HTTP context have user info, verifies audit context takes precedence
    /// </summary>
    [Test]
    public void CreateEvent_AuditContextTakesPrecedenceOverHttpContext()
    {
        // Arrange
        // Set up HTTP context
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "http-user"),
            new Claim(ClaimTypes.Email, "http@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        _httpContext.User = new ClaimsPrincipal(identity);
        _httpContext.TraceIdentifier = "http-trace";

        // Set up audit context (should take precedence)
        // IMPORTANT: Set UserId so the factory knows context is populated
        _auditContext.UserId = Guid.NewGuid(); // This is the key!
        _auditContext.AspNetUserId = "context-user";
        _auditContext.UserEmail = "context@example.com";
        _auditContext.CorrelationId = "context-correlation";

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.CustomFields["AspNetUserId"], Is.EqualTo("context-user"));
        Assert.That(result.CustomFields["UserEmail"], Is.EqualTo("context@example.com"));
        Assert.That(result.CustomFields["CorrelationId"], Is.EqualTo("context-correlation"));
    }

    /// <summary>
    /// Creates an event with populated audit context, verifies it does not fallback to HTTP context
    /// </summary>
    [Test]
    public void CreateEvent_WithPopulatedAuditContext_DoesNotFallbackToHttpContext()
    {
        // Arrange
        // Set up HTTP context with different values
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "http-user"),
            new Claim(ClaimTypes.Email, "http@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        _httpContext.User = new ClaimsPrincipal(identity);
        _httpContext.TraceIdentifier = "http-trace";

        // Set up audit context with UserId (triggers using context instead of fallback)
        _auditContext.UserId = Guid.NewGuid();
        _auditContext.AspNetUserId = "context-user";
        _auditContext.UserEmail = "context@example.com";
        _auditContext.CorrelationId = "context-correlation";

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert - Context values should be used, not HTTP context values
        Assert.That(result.CustomFields["AspNetUserId"], Is.EqualTo("context-user"),
            "Should use audit context value, not HTTP context");
        Assert.That(result.CustomFields["UserEmail"], Is.EqualTo("context@example.com"),
            "Should use audit context value, not HTTP context");
        Assert.That(result.CustomFields["CorrelationId"], Is.EqualTo("context-correlation"),
            "Should use audit context value, not HTTP context");
    }

    /// <summary>
    /// Creates an event with empty audit context, verifies it falls back to HTTP context
    /// </summary>
    [Test]
    public void CreateEvent_WithEmptyAuditContext_FallsBackToHttpContext()
    {
        // Arrange
        // Set up HTTP context
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "http-user"),
            new Claim(ClaimTypes.Email, "http@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        _httpContext.User = new ClaimsPrincipal(identity);
        _httpContext.TraceIdentifier = "http-trace";

        // Leave audit context empty (UserId is null)
        // This should trigger fallback to HTTP context

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert - Should use HTTP context values as fallback
        Assert.That(result.CustomFields["AspNetUserId"], Is.EqualTo("http-user"),
            "Should fallback to HTTP context when audit context is empty");
        Assert.That(result.CustomFields["UserEmail"], Is.EqualTo("http@example.com"),
            "Should fallback to HTTP context when audit context is empty");
    }

    /// <summary>
    /// Creates an entity event with null old values, verifies event is created without old values
    /// </summary>
    [Test]
    public void CreateEntityEvent_WithNullOldValues_CreatesEventWithoutOldValues()
    {
        // Arrange
        var entityType = "User";
        var action = "Create";
        var entity = new { Id = 123, Name = "New User" };

        _auditContext.UserEmail = "admin@example.com";

        // Act
        var result = _factory.CreateEntityEvent(entityType, action, entity);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Target, Is.Not.Null);
        Assert.That(result.Target.New, Is.EqualTo(entity));
        Assert.That(result.Target.Old, Is.Null);
    }

    /// <summary>
    /// Verifies that the fallback user enrichment does NOT set CustomFields["UserId"]
    /// because the ASP.NET Identity string ID cannot be mapped to Guid in persistence.
    /// </summary>
    [Test]
    public void CreateEvent_FallbackUserEnrichment_DoesNotSetUserIdAsString()
    {
        // Arrange - only HTTP context, no audit context user info
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "aspnet-string-id-123"),
            new Claim(ClaimTypes.Email, "user@example.com")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        _httpContext.User = new ClaimsPrincipal(identity);

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.CustomFields.ContainsKey("AspNetUserId"), Is.True,
            "AspNetUserId should be set from claims");
        Assert.That(result.CustomFields["AspNetUserId"], Is.EqualTo("aspnet-string-id-123"));

        Assert.That(result.CustomFields.ContainsKey("UserId"), Is.False,
            "UserId should NOT be set in fallback path - it requires a Guid, not string");
    }

    /// <summary>
    /// Verifies that when AuditContext has UserId (Guid), it IS set in CustomFields.
    /// </summary>
    [Test]
    public void CreateEvent_WithGuidUserId_SetsUserIdInCustomFields()
    {
        // Arrange
        var userId = Guid.NewGuid();
        _auditContext.UserId = userId;
        _auditContext.AspNetUserId = "aspnet-123";
        _auditContext.UserEmail = "user@example.com";

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.CustomFields.ContainsKey("UserId"), Is.True);
        Assert.That(result.CustomFields["UserId"], Is.EqualTo(userId));
        Assert.That(result.CustomFields["UserId"], Is.TypeOf<Guid>());
    }

    /// <summary>
    /// Verifies that partial AuditContext population still gets full data from HttpContext for missing fields.
    /// </summary>
    [Test]
    public void CreateEvent_PartialAuditContext_FallsBackToHttpContextForMissingFields()
    {
        // Arrange - AuditContext only has CorrelationId set
        _auditContext.CorrelationId = "context-correlation-id";
        _auditContext.UserEmail = "user@example.com";
        // Intentionally NOT setting IpAddress, UserAgent, etc.

        // Set up HTTP context with values for the missing fields
        _httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
        _httpContext.Request.Headers["User-Agent"] = "Mozilla/5.0 Test Browser";
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "PUT";

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert - CorrelationId from context, other fields from HTTP
        Assert.That(result.CustomFields["CorrelationId"], Is.EqualTo("context-correlation-id"),
            "Should use AuditContext value when present");
        Assert.That(result.CustomFields["IpAddress"], Is.EqualTo("10.0.0.1"),
            "Should fallback to HttpContext when AuditContext field is null");
        Assert.That(result.CustomFields["UserAgent"], Is.EqualTo("Mozilla/5.0 Test Browser"),
            "Should fallback to HttpContext when AuditContext field is null");
        Assert.That(result.CustomFields["RequestPath"], Is.EqualTo("/api/test"),
            "Should fallback to HttpContext when AuditContext field is null");
        Assert.That(result.CustomFields["RequestMethod"], Is.EqualTo("PUT"),
            "Should fallback to HttpContext when AuditContext field is null");
    }

    /// <summary>
    /// Verifies that AssemblyName uses the configured ApplicationName from options.
    /// </summary>
    [Test]
    public void CreateEvent_UsesConfiguredApplicationNameAsAssemblyName()
    {
        // Arrange - Create factory with custom app name
        var customOptions = new AuditOptions { ApplicationName = "MyCustomApplication" };
        var factory = new AuditEventFactory(
            _mockHttpContextAccessor.Object,
            _auditContext,
            Options.Create(customOptions),
            _mockLogger.Object);

        _auditContext.UserEmail = "user@example.com";

        // Act
        var result = factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.Environment.AssemblyName, Is.EqualTo("MyCustomApplication"));
    }

    /// <summary>
    /// Verifies that Culture is captured at event creation time, not at static init time.
    /// </summary>
    [Test]
    public void CreateEvent_CapturesCurrentThreadCulture()
    {
        // Arrange
        var originalCulture = Thread.CurrentThread.CurrentCulture;
        _auditContext.UserEmail = "user@example.com";

        try
        {
            // Set a specific culture for this thread
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");

            // Act
            var result = _factory.CreateEvent("Test.Event");

            // Assert
            Assert.That(result.Environment.Culture, Is.EqualTo("fr-FR"));

            // Change culture and create another event
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var result2 = _factory.CreateEvent("Test.Event2");

            // Should reflect the new culture
            Assert.That(result2.Environment.Culture, Is.EqualTo("de-DE"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// Verifies that request context handles empty HttpContext gracefully when AuditContext is also empty.
    /// </summary>
    [Test]
    public void CreateEvent_NoHttpContextOrAuditContext_HandlesGracefully()
    {
        // Arrange
        _mockHttpContextAccessor.Setup(static x => x.HttpContext).Returns((HttpContext?)null);
        // Leave audit context empty

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert - should not have request context fields
        Assert.That(result.CustomFields.ContainsKey("CorrelationId"), Is.False);
        Assert.That(result.CustomFields.ContainsKey("IpAddress"), Is.False);
        Assert.That(result.CustomFields.ContainsKey("UserAgent"), Is.False);
    }

    /// <summary>
    /// Verifies that AuditContext values take precedence over HttpContext when both are populated.
    /// </summary>
    [Test]
    public void CreateEvent_AuditContextRequestFieldsTakePrecedenceOverHttpContext()
    {
        // Arrange
        _auditContext.UserId = Guid.NewGuid();
        _auditContext.UserEmail = "context@example.com";
        _auditContext.CorrelationId = "context-corr";
        _auditContext.IpAddress = "192.168.1.100";
        _auditContext.UserAgent = "AuditContext Agent";
        _auditContext.RequestPath = "/context/path";
        _auditContext.RequestMethod = "DELETE";

        // Set up HTTP context with different values
        _httpContext.TraceIdentifier = "http-trace";
        _httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
        _httpContext.Request.Headers["User-Agent"] = "HttpContext Agent";
        _httpContext.Request.Path = "/http/path";
        _httpContext.Request.Method = "GET";

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert - AuditContext values should win
        Assert.That(result.CustomFields["CorrelationId"], Is.EqualTo("context-corr"));
        Assert.That(result.CustomFields["IpAddress"], Is.EqualTo("192.168.1.100"));
        Assert.That(result.CustomFields["UserAgent"], Is.EqualTo("AuditContext Agent"));
        Assert.That(result.CustomFields["RequestPath"], Is.EqualTo("/context/path"));
        Assert.That(result.CustomFields["RequestMethod"], Is.EqualTo("DELETE"));
    }

    /// <summary>
    /// Verifies that UserEmail fallback doesn't set the field when claim is missing.
    /// </summary>
    [Test]
    public void CreateEvent_FallbackUserEnrichment_DoesNotSetEmptyUserEmail()
    {
        // Arrange - authenticated user but no email claim
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-123")
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        _httpContext.User = new ClaimsPrincipal(identity);

        // Act
        var result = _factory.CreateEvent("Test.Event");

        // Assert
        Assert.That(result.CustomFields["AspNetUserId"], Is.EqualTo("user-123"));
        Assert.That(result.CustomFields.ContainsKey("UserEmail"), Is.False,
            "UserEmail should not be set when email claim is missing");
    }
}