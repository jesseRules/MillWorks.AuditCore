using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;
using System.Security.Claims;

namespace MillWorks.AuditCore.Tests.Middleware;

/// <summary>
/// Edge case tests for AuditContextMiddleware
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditContextMiddlewareEdgeCaseTests
{
    private IAuditContext _auditContext;
    private Mock<ILogger<AuditContextMiddleware>> _mockLogger;
    private Mock<IAuditLogger> _mockAuditLogger;
    private DefaultHttpContext _httpContext;
    private AuditContextMiddleware _middleware;

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

    [TearDown]
    public void TearDown()
    {
        _auditContext.Clear();
    }

    /// <summary>
    /// InvokeAsync with no authenticated user sets default context values (no user info populated)
    /// </summary>
    [Test]
    public async Task Invoke_NoAuthenticatedUser_SetsAnonymousContext()
    {
        // Arrange - unauthenticated user (no auth type = not authenticated)
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        _httpContext.Request.Path = "/api/data";
        _httpContext.Request.Method = "GET";
        _httpContext.TraceIdentifier = "anon-trace-id";

        string? capturedAspNetUserId = null;
        string? capturedUserEmail = null;
        string? capturedUserFullName = null;
        Guid? capturedUserId = null;
        Guid? capturedTenantId = null;
        string? capturedCorrelationId = null;

        Task Next(HttpContext ctx)
        {
            capturedAspNetUserId = _auditContext.AspNetUserId;
            capturedUserEmail = _auditContext.UserEmail;
            capturedUserFullName = _auditContext.UserFullName;
            capturedUserId = _auditContext.UserId;
            capturedTenantId = _auditContext.TenantId;
            capturedCorrelationId = _auditContext.CorrelationId;
            return Task.CompletedTask;
        }

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert - user fields should be null (anonymous), but correlation ID should be set
        Assert.That(capturedAspNetUserId, Is.Null);
        Assert.That(capturedUserEmail, Is.Null);
        Assert.That(capturedUserFullName, Is.Null);
        Assert.That(capturedUserId, Is.Null);
        Assert.That(capturedTenantId, Is.Null);
        Assert.That(capturedCorrelationId, Is.EqualTo("anon-trace-id"));
    }

    /// <summary>
    /// InvokeAsync with missing IP and User-Agent headers does not throw
    /// </summary>
    [Test]
    public async Task Invoke_MissingHeaders_HandlesGracefully()
    {
        // Arrange - no RemoteIpAddress, no User-Agent header
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";
        _httpContext.Connection.RemoteIpAddress = null;
        // Do NOT set User-Agent header

        string? capturedIpAddress = null;
        string? capturedUserAgent = null;
        bool nextCalled = false;

        Task Next(HttpContext ctx)
        {
            capturedIpAddress = _auditContext.IpAddress;
            capturedUserAgent = _auditContext.UserAgent;
            nextCalled = true;
            return Task.CompletedTask;
        }

        // Act - should not throw
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert
        Assert.That(nextCalled, Is.True);
        Assert.That(capturedIpAddress, Is.Null.Or.Empty);
        Assert.That(capturedUserAgent, Is.Null.Or.Empty);
    }

    /// <summary>
    /// InvokeAsync with excluded path (/health) does not create audit scope
    /// </summary>
    [Test]
    public async Task Invoke_ExcludedPath_SkipsContextPopulation()
    {
        // Arrange - health check path is in ExcludedPaths
        _httpContext.Request.Path = "/health";
        _httpContext.Request.Method = "GET";
        _httpContext.TraceIdentifier = "health-trace";

        string? capturedCorrelationId = null;

        Task Next(HttpContext ctx)
        {
            // Context is still populated (PopulateAuditContext runs before ShouldAuditRequest),
            // but audit scope creation is skipped.
            capturedCorrelationId = _auditContext.CorrelationId;
            return Task.CompletedTask;
        }

        // Act
        await _middleware.InvokeAsync(_httpContext, Next);

        // Assert - context is populated but no audit scope was created
        Assert.That(capturedCorrelationId, Is.EqualTo("health-trace"));
        _mockAuditLogger.Verify(
            static x => x.CreateScope(It.IsAny<string>(), It.IsAny<object>()),
            Times.Never);
    }

    /// <summary>
    /// InvokeAsync clears context even when next delegate throws an exception
    /// </summary>
    [Test]
    public async Task Invoke_ExceptionInNext_StillCleansUpContext()
    {
        // Arrange
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "POST";
        _httpContext.TraceIdentifier = "error-trace";
        _httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.5");
        _httpContext.Request.Headers["User-Agent"] = "TestAgent";

        RequestDelegate next = static _ => throw new InvalidOperationException("Pipeline failure");

        // Act & Assert - the exception should propagate
        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _middleware.InvokeAsync(_httpContext, next));

        // Assert - context should be cleared after exception
        Assert.That(_auditContext.CorrelationId, Is.Null);
        Assert.That(_auditContext.IpAddress, Is.Null);
        Assert.That(_auditContext.UserAgent, Is.Null);
        Assert.That(_auditContext.RequestPath, Is.Null);
        Assert.That(_auditContext.RequestMethod, Is.Null);
    }

    /// <summary>
    /// Concurrent requests with different users get isolated contexts when using scoped services
    /// </summary>
    [Test]
    public async Task Invoke_ConcurrentRequests_IsolatedContexts()
    {
        // Arrange - two separate middleware instances with separate IAuditContext instances
        // (simulating scoped DI where each request gets its own IAuditContext)
        var auditContext1 = new AuditContext();
        var auditContext2 = new AuditContext();
        var mockLogger1 = new Mock<ILogger<AuditContextMiddleware>>();
        var mockLogger2 = new Mock<ILogger<AuditContextMiddleware>>();

        var middleware1 = new AuditContextMiddleware(auditContext1, mockLogger1.Object);
        var middleware2 = new AuditContextMiddleware(auditContext2, mockLogger2.Object);

        var httpContext1 = new DefaultHttpContext();
        httpContext1.Request.Path = "/api/user1";
        httpContext1.Request.Method = "GET";
        httpContext1.TraceIdentifier = "trace-user1";
        var user1Claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user-001") };
        httpContext1.User = new ClaimsPrincipal(new ClaimsIdentity(user1Claims, "TestAuth"));
        var services1 = new ServiceCollection();
        services1.AddSingleton(_mockAuditLogger.Object);
        httpContext1.RequestServices = services1.BuildServiceProvider();

        var httpContext2 = new DefaultHttpContext();
        httpContext2.Request.Path = "/api/user2";
        httpContext2.Request.Method = "POST";
        httpContext2.TraceIdentifier = "trace-user2";
        var user2Claims = new[] { new Claim(ClaimTypes.NameIdentifier, "user-002") };
        httpContext2.User = new ClaimsPrincipal(new ClaimsIdentity(user2Claims, "TestAuth"));
        var services2 = new ServiceCollection();
        services2.AddSingleton(_mockAuditLogger.Object);
        httpContext2.RequestServices = services2.BuildServiceProvider();

        string? captured1CorrelationId = null;
        string? captured1AspNetUserId = null;
        string? captured2CorrelationId = null;
        string? captured2AspNetUserId = null;

        Task Next1(HttpContext ctx)
        {
            captured1CorrelationId = auditContext1.CorrelationId;
            captured1AspNetUserId = auditContext1.AspNetUserId;
            return Task.CompletedTask;
        }

        Task Next2(HttpContext ctx)
        {
            captured2CorrelationId = auditContext2.CorrelationId;
            captured2AspNetUserId = auditContext2.AspNetUserId;
            return Task.CompletedTask;
        }

        // Act - invoke both concurrently
        await Task.WhenAll(
            middleware1.InvokeAsync(httpContext1, Next1),
            middleware2.InvokeAsync(httpContext2, Next2));

        // Assert - each context should have captured its own user's data
        Assert.That(captured1CorrelationId, Is.EqualTo("trace-user1"));
        Assert.That(captured1AspNetUserId, Is.EqualTo("user-001"));
        Assert.That(captured2CorrelationId, Is.EqualTo("trace-user2"));
        Assert.That(captured2AspNetUserId, Is.EqualTo("user-002"));
    }
}
