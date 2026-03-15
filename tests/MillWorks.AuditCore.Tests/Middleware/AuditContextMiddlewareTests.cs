using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;
using System.Security.Claims;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Tests.Middleware;

/// <summary>
/// AuditContextMiddleware tests
/// </summary>
[TestFixture]
public class AuditContextMiddlewareTests
{
    /// <summary>
    /// Audit context instance
    /// </summary>
    private IAuditContext _auditContext;

    /// <summary>
    /// Mock logger
    /// </summary>
    private Mock<ILogger<AuditContextMiddleware>> _mockLogger;

    /// <summary>
    /// Mock audit logger
    /// </summary>
    private Mock<IAuditLogger> _mockAuditLogger;

    /// <summary>
    /// Http context for testing
    /// </summary>
    private DefaultHttpContext _httpContext;

    /// <summary>
    /// Middleware instance under test
    /// </summary>
    private AuditContextMiddleware _middleware;

    /// <summary>
    /// Setup before each test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _auditContext = new AuditContext();
        _mockLogger = new Mock<ILogger<AuditContextMiddleware>>();
        _mockAuditLogger = new Mock<IAuditLogger>();

        _middleware = new AuditContextMiddleware(
            _auditContext,
            _mockLogger.Object);

        _httpContext = new DefaultHttpContext();

        var services = new ServiceCollection();
        services.AddSingleton(_mockAuditLogger.Object);
        _httpContext.RequestServices = services.BuildServiceProvider();
    }

    /// <summary>
    /// Tear down after each test
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _auditContext.Clear();
    }

    /// <summary>
    /// InvokeAsync populates audit context correctly
    /// </summary>
    [Test]
    public async Task InvokeAsync_PopulatesAuditContext()
    {
        // Arrange
        _httpContext.TraceIdentifier = "test-trace-id";
        _httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");
        _httpContext.Request.Headers["User-Agent"] = "Mozilla/5.0";
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";

        string? capturedCorrelationId = null;
        string? capturedIpAddress = null;
        string? capturedUserAgent = null;
        string? capturedRequestPath = null;
        string? capturedRequestMethod = null;

        Task Next(HttpContext ctx)
        {
            capturedCorrelationId = _auditContext.CorrelationId;
            capturedIpAddress = _auditContext.IpAddress;
            capturedUserAgent = _auditContext.UserAgent;
            capturedRequestPath = _auditContext.RequestPath;
            capturedRequestMethod = _auditContext.RequestMethod;
            return Task.CompletedTask;
        }

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        Assert.That(capturedCorrelationId, Is.EqualTo("test-trace-id"));
        Assert.That(capturedIpAddress, Is.EqualTo("192.168.1.100"));
        Assert.That(capturedUserAgent, Is.EqualTo("Mozilla/5.0"));
        Assert.That(capturedRequestPath, Is.EqualTo("/api/test"));
        Assert.That(capturedRequestMethod, Is.EqualTo("GET"));
    }

    /// <summary>
    /// InvokeAsync with authenticated user populates user info
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithAuthenticatedUser_PopulatesUserInfo()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user123"),
            new Claim(ClaimTypes.Email, "user@example.com"),
            new Claim(ClaimTypes.Name, "Test User"),
            new Claim("AppUserId", userId.ToString()),
            new Claim("TenantId", tenantId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuthType");
        var principal = new ClaimsPrincipal(identity);
        _httpContext.User = principal;
        _httpContext.Request.Path = "/api/test";

        string? capturedAspNetUserId = null;
        string? capturedUserEmail = null;
        string? capturedUserFullName = null;
        Guid? capturedUserId = null;
        Guid? capturedTenantId = null;

        Task Next(HttpContext ctx)
        {
            capturedAspNetUserId = _auditContext.AspNetUserId;
            capturedUserEmail = _auditContext.UserEmail;
            capturedUserFullName = _auditContext.UserFullName;
            capturedUserId = _auditContext.UserId;
            capturedTenantId = _auditContext.TenantId;
            return Task.CompletedTask;
        }

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        Assert.That(capturedAspNetUserId, Is.EqualTo("user123"));
        Assert.That(capturedUserEmail, Is.EqualTo("user@example.com"));
        Assert.That(capturedUserFullName, Is.EqualTo("Test User"));
        Assert.That(capturedUserId, Is.EqualTo(userId));
        Assert.That(capturedTenantId, Is.EqualTo(tenantId));
    }

    /// <summary>
    /// InvokeAsync with anonymous user does not set user info
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithAnonymousUser_DoesNotSetUserInfo()
    {
        // Arrange
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        _httpContext.Request.Path = "/api/test";

        string? capturedAspNetUserId = null;
        string? capturedUserEmail = null;
        Guid? capturedUserId = null;

        Task Next(HttpContext ctx)
        {
            capturedAspNetUserId = _auditContext.AspNetUserId;
            capturedUserEmail = _auditContext.UserEmail;
            capturedUserId = _auditContext.UserId;
            return Task.CompletedTask;
        }

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        Assert.That(capturedAspNetUserId, Is.Null);
        Assert.That(capturedUserEmail, Is.Null);
        Assert.That(capturedUserId, Is.Null);
    }

    /// <summary>
    /// InvokeAsync with excluded path does not create audit scope
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithExcludedPath_DoesNotCreateAuditScope()
    {
        // Arrange
        _httpContext.Request.Path = "/health";
        _httpContext.Request.Method = "GET";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        _mockAuditLogger.Verify(static x => x.CreateScope(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    /// <summary>
    /// InvokeAsync with OPTIONS request does not create audit scope
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithOptionsRequest_DoesNotCreateAuditScope()
    {
        // Arrange
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "OPTIONS";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        _mockAuditLogger.Verify(static x => x.CreateScope(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    /// <summary>
    /// InvokeAsync clears context after request
    /// </summary>
    [Test]
    public async Task InvokeAsync_ClearsContextAfterRequest()
    {
        // Arrange
        _httpContext.TraceIdentifier = "test-trace-id";
        _httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");
        _httpContext.Request.Path = "/api/test";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert - Context should be cleared after request completes
        Assert.That(_auditContext.CorrelationId, Is.Null);
        Assert.That(_auditContext.IpAddress, Is.Null);
        Assert.That(_auditContext.UserAgent, Is.Null);
        Assert.That(_auditContext.RequestPath, Is.Null);
        Assert.That(_auditContext.RequestMethod, Is.Null);
    }

    /// <summary>
    /// InvokeAsync when exception occurs still clears context
    /// </summary>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    [Test]
    public Task InvokeAsync_WhenExceptionOccurs_StillClearsContext()
    {
        // Arrange
        _httpContext.TraceIdentifier = "test-trace-id";
        _httpContext.Request.Path = "/api/test";

        RequestDelegate next = static _ => throw new Exception("Test exception");

        // Act & Assert
        Assert.ThrowsAsync<Exception>(async () =>
            await _middleware.InvokeAsync(_httpContext, next));

        // Context should be cleared even after exception
        Assert.That(_auditContext.CorrelationId, Is.Null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// InvokeAsync with swagger path does not create audit scope
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithSwaggerPath_DoesNotCreateAuditScope()
    {
        // Arrange
        _httpContext.Request.Path = "/swagger/index.html";
        _httpContext.Request.Method = "GET";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        _mockAuditLogger.Verify(static x => x.CreateScope(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    /// <summary>
    /// InvokeAsync with metrics path does not create audit scope
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithMetricsPath_DoesNotCreateAuditScope()
    {
        // Arrange
        _httpContext.Request.Path = "/metrics";
        _httpContext.Request.Method = "GET";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        _mockAuditLogger.Verify(static x => x.CreateScope(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    /// <summary>
    /// InvokeAsync with API path populates context correctly
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithApiPath_PopulatesContextCorrectly()
    {
        // Arrange
        _httpContext.Request.Path = "/api/users/123";
        _httpContext.Request.Method = "POST";
        _httpContext.TraceIdentifier = "correlation-123";
        _httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.1");
        _httpContext.Request.Headers["User-Agent"] = "Test Agent";

        string? capturedRequestPath = null;
        string? capturedRequestMethod = null;
        string? capturedCorrelationId = null;
        string? capturedIpAddress = null;
        string? capturedUserAgent = null;

        Task Next(HttpContext ctx)
        {
            capturedRequestPath = _auditContext.RequestPath;
            capturedRequestMethod = _auditContext.RequestMethod;
            capturedCorrelationId = _auditContext.CorrelationId;
            capturedIpAddress = _auditContext.IpAddress;
            capturedUserAgent = _auditContext.UserAgent;
            return Task.CompletedTask;
        }

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        Assert.That(capturedRequestPath, Is.EqualTo("/api/users/123"));
        Assert.That(capturedRequestMethod, Is.EqualTo("POST"));
        Assert.That(capturedCorrelationId, Is.EqualTo("correlation-123"));
        Assert.That(capturedIpAddress, Is.EqualTo("10.0.0.1"));
        Assert.That(capturedUserAgent, Is.EqualTo("Test Agent"));
    }

    /// <summary>
    /// InvokeAsync with missing User-Agent header handles gracefully
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithMissingUserAgent_HandlesGracefully()
    {
        // Arrange
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";
        // Don't set User-Agent header

        string? capturedUserAgent = null;

        Task Next(HttpContext ctx)
        {
            capturedUserAgent = _auditContext.UserAgent;
            return Task.CompletedTask;
        }

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert - Should handle missing header gracefully
        Assert.That(capturedUserAgent, Is.Null.Or.Empty);
    }

    /// <summary>
    /// InvokeAsync with null RemoteIpAddress handles gracefully
    /// </summary>
    [Test]
    public async Task InvokeAsync_WithNullRemoteIpAddress_HandlesGracefully()
    {
        // Arrange
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";
        _httpContext.Connection.RemoteIpAddress = null;

        string? capturedIpAddress = null;

        Task Next(HttpContext ctx)
        {
            capturedIpAddress = _auditContext.IpAddress;
            return Task.CompletedTask;
        }

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert - Should handle null IP gracefully
        Assert.That(capturedIpAddress, Is.Null.Or.Empty);
    }

    /// <summary>
    /// InvokeAsync calls next delegate
    /// </summary>
    [Test]
    public async Task InvokeAsync_CallsNextDelegate()
    {
        // Arrange
        _httpContext.Request.Path = "/api/test";
        var nextCalled = false;

        Task Next(HttpContext ctx)
        {
            nextCalled = true;
            return Task.CompletedTask;
        }

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        Assert.That(nextCalled, Is.True);
    }
}