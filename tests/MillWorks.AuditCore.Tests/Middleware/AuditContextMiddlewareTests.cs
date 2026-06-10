using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.Tests.Middleware;

/// <summary>
/// AuditContextMiddleware tests.
/// </summary>
[TestFixture]
public class AuditContextMiddlewareTests
{
    private IAuditContext _auditContext = null!;
    private Mock<ILogger<AuditContextMiddleware>> _mockLogger = null!;
    private Mock<IRequestAuditDispatcher> _mockRequestAuditDispatcher = null!;
    private Mock<IAuditEventFactory> _mockAuditEventFactory = null!;
    private DefaultHttpContext _httpContext = null!;
    private AuditContextMiddleware _middleware = null!;

    [SetUp]
    public void Setup()
    {
        _auditContext = new AuditContext();
        _mockLogger = new Mock<ILogger<AuditContextMiddleware>>();
        _mockRequestAuditDispatcher = new Mock<IRequestAuditDispatcher>();
        _mockAuditEventFactory = new Mock<IAuditEventFactory>();
        _mockAuditEventFactory
            .Setup(x => x.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()))
            .Returns((string eventType, object? target, string _) => new AuditEvent
            {
                EventType = eventType,
                Target = target != null ? new AuditTarget { New = target } : null
            });

        _middleware = CreateMiddleware();

        _httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
    }

    [TearDown]
    public void TearDown()
    {
        _auditContext.Clear();
    }

    private AuditContextMiddleware CreateMiddleware(AuditMiddlewareOptions? options = null)
    {
        return new AuditContextMiddleware(
            _auditContext,
            _mockAuditEventFactory.Object,
            _mockRequestAuditDispatcher.Object,
            Options.Create(options ?? new AuditMiddlewareOptions()),
            _mockLogger.Object);
    }

    [Test]
    public async Task InvokeAsync_PopulatesAuditContext()
    {
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

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedCorrelationId, Is.EqualTo("test-trace-id"));
        Assert.That(capturedIpAddress, Is.EqualTo("192.168.1.100"));
        Assert.That(capturedUserAgent, Is.EqualTo("Mozilla/5.0"));
        Assert.That(capturedRequestPath, Is.EqualTo("/api/test"));
        Assert.That(capturedRequestMethod, Is.EqualTo("GET"));
    }

    [Test]
    public async Task InvokeAsync_WithAuthenticatedUser_PopulatesUserInfo()
    {
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

        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuthType"));
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

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedAspNetUserId, Is.EqualTo("user123"));
        Assert.That(capturedUserEmail, Is.EqualTo("user@example.com"));
        Assert.That(capturedUserFullName, Is.EqualTo("Test User"));
        Assert.That(capturedUserId, Is.EqualTo(userId));
        Assert.That(capturedTenantId, Is.EqualTo(tenantId));
    }

    [Test]
    public async Task InvokeAsync_WithAnonymousUser_DoesNotSetUserInfo()
    {
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

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedAspNetUserId, Is.Null);
        Assert.That(capturedUserEmail, Is.Null);
        Assert.That(capturedUserId, Is.Null);
    }

    [Test]
    public async Task InvokeAsync_WithExcludedPath_DoesNotCreateRequestAuditEvent()
    {
        _httpContext.Request.Path = "/health";
        _httpContext.Request.Method = "GET";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        await _middleware.InvokeAsync(_httpContext, Next);

        _mockAuditEventFactory.Verify(
            x => x.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()),
            Times.Never);
        _mockRequestAuditDispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task InvokeAsync_ExcludedPathMatchesSegmentBoundary_NotBarePrefix()
    {
        _middleware = CreateMiddleware(new AuditMiddlewareOptions
        {
            ExcludedPaths = ["/test"]
        });

        _httpContext.Request.Path = "/testimonials";
        _httpContext.Request.Method = "POST";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        await _middleware.InvokeAsync(_httpContext, Next);

        _mockAuditEventFactory.Verify(
            x => x.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()),
            Times.Once);
    }

    [Test]
    public async Task InvokeAsync_ExcludedPathWithSubpath_IsExcluded()
    {
        _middleware = CreateMiddleware(new AuditMiddlewareOptions
        {
            ExcludedPaths = ["/test"]
        });

        _httpContext.Request.Path = "/test/subpath";
        _httpContext.Request.Method = "POST";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        await _middleware.InvokeAsync(_httpContext, Next);

        _mockAuditEventFactory.Verify(
            x => x.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task InvokeAsync_WithConfiguredExcludedReadPath_DoesNotCreateRequestAuditEvent()
    {
        _middleware = CreateMiddleware(new AuditMiddlewareOptions
        {
            ExcludedReadPaths = ["/api/v1/feedback/dashboard"]
        });

        _httpContext.Request.Path = "/api/v1/feedback/dashboard/top-pages";
        _httpContext.Request.Method = "GET";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        await _middleware.InvokeAsync(_httpContext, Next);

        _mockAuditEventFactory.Verify(
            x => x.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()),
            Times.Never);
        _mockRequestAuditDispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task InvokeAsync_WithOptionsRequest_DoesNotCreateRequestAuditEvent()
    {
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "OPTIONS";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        await _middleware.InvokeAsync(_httpContext, Next);

        _mockAuditEventFactory.Verify(
            x => x.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task InvokeAsync_WithAuditWritesOnly_SkipsGetRequests()
    {
        _middleware = CreateMiddleware(new AuditMiddlewareOptions
        {
            AuditWritesOnly = true
        });

        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        await _middleware.InvokeAsync(_httpContext, Next);

        _mockAuditEventFactory.Verify(
            x => x.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()),
            Times.Never);
        _mockRequestAuditDispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task InvokeAsync_WithAuditedRequest_DispatchesCompletedAuditEvent()
    {
        _httpContext.Request.Path = "/api/orders/123";
        _httpContext.Request.Method = "POST";
        _httpContext.Response.StatusCode = StatusCodes.Status201Created;

        AuditEvent? dispatchedEvent = null;
        _mockRequestAuditDispatcher
            .Setup(x => x.DispatchAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback((AuditEvent evt, CancellationToken _) => dispatchedEvent = evt)
            .Returns(ValueTask.CompletedTask);

        Task Next(HttpContext ctx)
        {
            ctx.Response.StatusCode = StatusCodes.Status201Created;
            return Task.CompletedTask;
        }

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(dispatchedEvent, Is.Not.Null);
        Assert.That(dispatchedEvent!.EventType, Is.EqualTo("Http.POST"));
        Assert.That(dispatchedEvent.EndDate, Is.Not.Null);
        Assert.That(dispatchedEvent.Duration, Is.Not.Null);
        Assert.That(dispatchedEvent.CustomFields["StatusCode"], Is.EqualTo(StatusCodes.Status201Created));
        Assert.That(dispatchedEvent.CustomFields.ContainsKey("ElapsedMs"), Is.True);
    }

    [Test]
    public async Task InvokeAsync_ClearsContextAfterRequest()
    {
        _httpContext.TraceIdentifier = "test-trace-id";
        _httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.168.1.100");
        _httpContext.Request.Path = "/api/test";

        Task Next(HttpContext ctx) => Task.CompletedTask;

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(_auditContext.CorrelationId, Is.Null);
        Assert.That(_auditContext.IpAddress, Is.Null);
        Assert.That(_auditContext.UserAgent, Is.Null);
        Assert.That(_auditContext.RequestPath, Is.Null);
        Assert.That(_auditContext.RequestMethod, Is.Null);
    }

    [Test]
    public Task InvokeAsync_WhenExceptionOccurs_StillClearsContext()
    {
        _httpContext.TraceIdentifier = "test-trace-id";
        _httpContext.Request.Path = "/api/test";

        RequestDelegate next = static _ => throw new Exception("Test exception");

        Assert.ThrowsAsync<Exception>(async () =>
            await _middleware.InvokeAsync(_httpContext, next));

        Assert.That(_auditContext.CorrelationId, Is.Null);
        return Task.CompletedTask;
    }
}
