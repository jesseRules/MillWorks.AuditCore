using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Models;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Middleware to populate audit context for each request.
/// </summary>
public sealed class AuditContextMiddleware(
    IAuditContext auditContext,
    IAuditEventFactory auditEventFactory,
    IRequestAuditDispatcher requestAuditDispatcher,
    IOptions<AuditMiddlewareOptions> options,
    ILogger<AuditContextMiddleware> logger)
    : IMiddleware
{
    private const string _correlationIdHeader = "X-Correlation-Id";
    private const int _maxCorrelationIdLength = 128;

    /// <summary>
    /// Invoke the middleware to populate audit context and create audit scope if needed.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="next"></param>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            PopulateAuditContext(context);

            // Pipe scoped request state to the DbContext so the singleton interceptor
            // can read it via eventData.Context in SavingChanges.
            var dbContext = context.RequestServices.GetService<AuditDbContext>();
            if (dbContext != null)
            {
                dbContext.CurrentCorrelationId = auditContext.CorrelationId;
                dbContext.CurrentIpAddress = auditContext.IpAddress;
                dbContext.CurrentUserAgent = auditContext.UserAgent;
                dbContext.ScopedServiceProvider = context.RequestServices;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error populating audit context for request {Path}", context.Request.Path);
        }

        AuditEvent? requestAuditEvent = null;

        if (ShouldAuditRequest(context))
        {
            try
            {
                requestAuditEvent = auditEventFactory.CreateEvent($"Http.{context.Request.Method}",
                    new RequestAuditTarget
                    {
                        Path = context.Request.Path.ToString(),
                        HasQueryString = context.Request.QueryString.HasValue,
                        UserAgent = context.Request.Headers["User-Agent"].ToString()
                    });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create request audit event for request {Path}", context.Request.Path);
            }
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            if (requestAuditEvent != null)
            {
                try
                {
                    requestAuditEvent.EndDate = DateTimeOffset.UtcNow;
                    requestAuditEvent.CalculateDuration();
                    requestAuditEvent.CustomFields["StatusCode"] = context.Response.StatusCode;
                    requestAuditEvent.CustomFields["ElapsedMs"] =
                        Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

                    await requestAuditDispatcher.DispatchAsync(requestAuditEvent, CancellationToken.None);
                }
                catch (OperationCanceledException ex)
                {
                    logger.LogWarning(ex,
                        "Request audit dispatch was canceled for request {Path} under overflow policy {OverflowPolicy} ({PolicyDetail})",
                        context.Request.Path,
                        options.Value.OverflowPolicy,
                        PolicyDetail(options.Value.OverflowPolicy));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Error dispatching request audit for request {Path} under overflow policy {OverflowPolicy} ({PolicyDetail})",
                        context.Request.Path,
                        options.Value.OverflowPolicy,
                        PolicyDetail(options.Value.OverflowPolicy));
                }
            }

            try
            {
                auditContext.Clear();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error clearing audit context for request {Path}", context.Request.Path);
            }
        }

        static string PolicyDetail(RequestAuditOverflowPolicy policy) => policy switch
        {
            RequestAuditOverflowPolicy.Throw => "swallowed; dispatcher raised overflow",
            RequestAuditOverflowPolicy.DropAndLog => "swallowed; event dropped per policy",
            RequestAuditOverflowPolicy.RouteToDeadLetter => "swallowed; dispatcher owns DLQ routing",
            _ => "swallowed; unknown policy"
        };
    }

    /// <summary>
    /// Populate audit context from HTTP context including user identity.
    /// IMPORTANT: Place UseMillWorksAudit() after UseAuthentication() in the pipeline
    /// to ensure context.User is populated when this runs.
    /// </summary>
    /// <param name="context"></param>
    private void PopulateAuditContext(HttpContext context)
    {
        auditContext.CorrelationId = ResolveCorrelationId(context);

        auditContext.IpAddress = context.Connection.RemoteIpAddress?.ToString();
        auditContext.UserAgent = context.Request.Headers["User-Agent"].ToString();
        auditContext.RequestPath = context.Request.Path.ToString();
        auditContext.RequestMethod = context.Request.Method;

        if (context.User.Identity?.IsAuthenticated == true)
        {
            Claim? userIdClaim = context.User.FindFirst(ClaimTypes.NameIdentifier);
            Claim? emailClaim = context.User.FindFirst(ClaimTypes.Email);
            Claim? nameClaim = context.User.FindFirst(ClaimTypes.Name);

            auditContext.AspNetUserId = userIdClaim?.Value;
            auditContext.UserEmail = emailClaim?.Value ?? context.User.Identity.Name;
            auditContext.UserFullName = nameClaim?.Value;

            Claim? appUserIdClaim = context.User.FindFirst("AppUserId");
            if (appUserIdClaim != null && Guid.TryParse(appUserIdClaim.Value, out Guid appUserId))
            {
                auditContext.UserId = appUserId;
            }

            Claim? tenantIdClaim = context.User.FindFirst("TenantId");
            if (tenantIdClaim != null && Guid.TryParse(tenantIdClaim.Value, out Guid tenantId))
            {
                auditContext.TenantId = tenantId;
            }

            logger.LogDebug("Audit context populated for authenticated user {UserId}", auditContext.AspNetUserId);
        }
    }

    private string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(_correlationIdHeader, out var headerValues))
        {
            foreach (var rawValue in headerValues)
            {
                if (string.IsNullOrWhiteSpace(rawValue))
                    continue;

                var candidate = rawValue.Trim();
                if (IsSafeCorrelationId(candidate))
                    return candidate;

                logger.LogWarning(
                    "Malformed {HeaderName} header on request {Path}. Falling back to trace identifier.",
                    _correlationIdHeader,
                    context.Request.Path);

                break;
            }
        }

        if (string.IsNullOrWhiteSpace(context.TraceIdentifier))
            context.TraceIdentifier = Guid.NewGuid().ToString("N");

        return context.TraceIdentifier;
    }

    private static bool IsSafeCorrelationId(string value)
    {
        return value.Length <= _maxCorrelationIdLength &&
               value.All(static c => !char.IsControl(c));
    }

    /// <summary>
    /// Should this request be audited?
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    private bool ShouldAuditRequest(HttpContext context)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
            return false;

        string path = context.Request.Path.ToString();

        if (options.Value.AuditWritesOnly &&
            (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            return false;
        }

        if (IsExcludedPath(path, options.Value.ExcludedPaths))
            return false;

        if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
            IsExcludedPath(path, options.Value.ExcludedReadPaths))
        {
            return false;
        }

        return true;
    }

    private static bool IsExcludedPath(string path, IEnumerable<string> excludedPrefixes)
    {
        foreach (var excluded in excludedPrefixes)
        {
            if (string.IsNullOrEmpty(excluded))
                continue;

            if (path.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                return true;

            if (path.StartsWith(excluded, StringComparison.OrdinalIgnoreCase) &&
                path.Length > excluded.Length &&
                path[excluded.Length] == '/')
            {
                return true;
            }
        }

        return false;
    }
}
