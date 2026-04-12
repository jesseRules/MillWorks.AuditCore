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
/// Edge case tests for AuditContextMiddleware.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditContextMiddlewareEdgeCaseTests
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

        _middleware = CreateMiddleware(_auditContext, _mockLogger.Object);

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

    private AuditContextMiddleware CreateMiddleware(
        IAuditContext auditContext,
        ILogger<AuditContextMiddleware> logger,
        AuditMiddlewareOptions? options = null)
    {
        return new AuditContextMiddleware(
            auditContext,
            _mockAuditEventFactory.Object,
            _mockRequestAuditDispatcher.Object,
            Options.Create(options ?? new AuditMiddlewareOptions()),
            logger);
    }

    [Test]
    public async Task Invoke_NoAuthenticatedUser_SetsAnonymousContext()
    {
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

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedAspNetUserId, Is.Null);
        Assert.That(capturedUserEmail, Is.Null);
        Assert.That(capturedUserFullName, Is.Null);
        Assert.That(capturedUserId, Is.Null);
        Assert.That(capturedTenantId, Is.Null);
        Assert.That(capturedCorrelationId, Is.EqualTo("anon-trace-id"));
    }

    [Test]
    public async Task Invoke_MissingHeaders_HandlesGracefully()
    {
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";
        _httpContext.Connection.RemoteIpAddress = null;

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

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(nextCalled, Is.True);
        Assert.That(capturedIpAddress, Is.Null.Or.Empty);
        Assert.That(capturedUserAgent, Is.Null.Or.Empty);
    }

    [Test]
    public async Task Invoke_EmptyTraceIdentifier_GeneratesNonEmptyCorrelationId()
    {
        _httpContext.TraceIdentifier = string.Empty;
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";

        string? capturedCorrelationId = null;

        Task Next(HttpContext ctx)
        {
            capturedCorrelationId = _auditContext.CorrelationId;
            return Task.CompletedTask;
        }

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedCorrelationId, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task Invoke_ValidCorrelationHeader_UsesHeaderValue()
    {
        _httpContext.TraceIdentifier = "trace-fallback";
        _httpContext.Request.Headers["X-Correlation-Id"] = "external-correlation-id";
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";

        string? capturedCorrelationId = null;

        Task Next(HttpContext ctx)
        {
            capturedCorrelationId = _auditContext.CorrelationId;
            return Task.CompletedTask;
        }

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedCorrelationId, Is.EqualTo("external-correlation-id"));
    }

    [Test]
    public async Task Invoke_MalformedCorrelationHeader_FallsBackAndLogsWarning()
    {
        _httpContext.TraceIdentifier = "trace-fallback";
        _httpContext.Request.Headers["X-Correlation-Id"] = "bad\nvalue";
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";

        string? capturedCorrelationId = null;

        Task Next(HttpContext ctx)
        {
            capturedCorrelationId = _auditContext.CorrelationId;
            return Task.CompletedTask;
        }

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedCorrelationId, Is.EqualTo("trace-fallback"));
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Malformed X-Correlation-Id")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public async Task Invoke_DuplicateCorrelationHeaders_UsesFirstValueDeterministically()
    {
        _httpContext.TraceIdentifier = "trace-fallback";
        _httpContext.Request.Headers.Append("X-Correlation-Id", "first-id");
        _httpContext.Request.Headers.Append("X-Correlation-Id", "second-id");
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";

        string? capturedCorrelationId = null;

        Task Next(HttpContext ctx)
        {
            capturedCorrelationId = _auditContext.CorrelationId;
            return Task.CompletedTask;
        }

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedCorrelationId, Is.EqualTo("first-id"));
    }

    [Test]
    public async Task Invoke_RequestPath_DoesNotIncludeQueryString()
    {
        _httpContext.TraceIdentifier = "query-trace";
        _httpContext.Request.Path = "/api/patients";
        _httpContext.Request.QueryString = new QueryString("?ssn=123-45-6789");
        _httpContext.Request.Method = "GET";

        string? capturedRequestPath = null;

        Task Next(HttpContext ctx)
        {
            capturedRequestPath = _auditContext.RequestPath;
            return Task.CompletedTask;
        }

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedRequestPath, Is.EqualTo("/api/patients"));
        Assert.That(capturedRequestPath, Does.Not.Contain("ssn"));
    }

    [Test]
    public async Task Invoke_InvalidAppUserIdClaim_DoesNotThrowAndLeavesUserIdNull()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-001"),
            new Claim("AppUserId", "not-a-guid")
        };

        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "GET";

        Guid? capturedUserId = null;

        Task Next(HttpContext ctx)
        {
            capturedUserId = _auditContext.UserId;
            return Task.CompletedTask;
        }

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedUserId, Is.Null);
    }

    [Test]
    public async Task Invoke_ExcludedPath_SkipsRequestAuditDispatch()
    {
        _httpContext.Request.Path = "/health";
        _httpContext.Request.Method = "GET";
        _httpContext.TraceIdentifier = "health-trace";

        string? capturedCorrelationId = null;

        Task Next(HttpContext ctx)
        {
            capturedCorrelationId = _auditContext.CorrelationId;
            return Task.CompletedTask;
        }

        await _middleware.InvokeAsync(_httpContext, Next);

        Assert.That(capturedCorrelationId, Is.EqualTo("health-trace"));
        _mockAuditEventFactory.Verify(
            x => x.CreateEvent(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>()),
            Times.Never);
        _mockRequestAuditDispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task Invoke_ExceptionInNext_StillCleansUpContext()
    {
        _httpContext.Request.Path = "/api/test";
        _httpContext.Request.Method = "POST";
        _httpContext.TraceIdentifier = "error-trace";
        _httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.5");
        _httpContext.Request.Headers["User-Agent"] = "TestAgent";

        RequestDelegate next = static _ => throw new InvalidOperationException("Pipeline failure");

        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _middleware.InvokeAsync(_httpContext, next));

        Assert.That(_auditContext.CorrelationId, Is.Null);
        Assert.That(_auditContext.IpAddress, Is.Null);
        Assert.That(_auditContext.UserAgent, Is.Null);
        Assert.That(_auditContext.RequestPath, Is.Null);
        Assert.That(_auditContext.RequestMethod, Is.Null);
    }

    [Test]
    public async Task Invoke_ConcurrentRequests_IsolatedContexts()
    {
        var auditContext1 = new AuditContext();
        var auditContext2 = new AuditContext();
        var mockLogger1 = new Mock<ILogger<AuditContextMiddleware>>();
        var mockLogger2 = new Mock<ILogger<AuditContextMiddleware>>();

        var middleware1 = CreateMiddleware(auditContext1, mockLogger1.Object);
        var middleware2 = CreateMiddleware(auditContext2, mockLogger2.Object);

        var httpContext1 = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        httpContext1.Request.Path = "/api/user1";
        httpContext1.Request.Method = "GET";
        httpContext1.TraceIdentifier = "trace-user1";
        httpContext1.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-001")],
            "TestAuth"));

        var httpContext2 = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        httpContext2.Request.Path = "/api/user2";
        httpContext2.Request.Method = "POST";
        httpContext2.TraceIdentifier = "trace-user2";
        httpContext2.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-002")],
            "TestAuth"));

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

        await Task.WhenAll(
            middleware1.InvokeAsync(httpContext1, Next1),
            middleware2.InvokeAsync(httpContext2, Next2));

        Assert.That(captured1CorrelationId, Is.EqualTo("trace-user1"));
        Assert.That(captured1AspNetUserId, Is.EqualTo("user-001"));
        Assert.That(captured2CorrelationId, Is.EqualTo("trace-user2"));
        Assert.That(captured2AspNetUserId, Is.EqualTo("user-002"));
    }
}
