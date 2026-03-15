using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Middleware to populate audit context for each request.
/// </summary>
public sealed class AuditContextMiddleware(
    IAuditContext auditContext,
    ILogger<AuditContextMiddleware> logger)
    : IMiddleware
{
    private static readonly string[] ExcludedPaths =
    [
        "/health",
        "/metrics",
        "/_framework",
        "/swagger",
        "/hangfire",
        "/cdn",
        "/test"
    ];

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
            var dbContext = context.RequestServices.GetService<AuditApplicationDbContext>();
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

        ICustomAuditScope? scope = null;

        if (ShouldAuditRequest(context))
        {
            try
            {
                scope = context.RequestServices.GetRequiredService<IAuditLogger>()
                    .CreateScope($"Http.{context.Request.Method}", new
                    {
                        Path = context.Request.Path.ToString(),
                        HasQueryString = context.Request.QueryString.HasValue,
                        UserAgent = context.Request.Headers["User-Agent"].ToString()
                    });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create audit scope for request {Path}", context.Request.Path);
            }
        }

        long startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            if (scope != null)
            {
                try
                {
                    scope.SetCustomField("StatusCode", context.Response.StatusCode);
                    scope.SetCustomField("ElapsedMs", Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
                    await scope.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error saving audit scope for request {Path}", context.Request.Path);
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
    }

    /// <summary>
    /// Populate audit context from HTTP context
    /// </summary>
    /// <param name="context"></param>
    private void PopulateAuditContext(HttpContext context)
    {
        auditContext.CorrelationId = context.TraceIdentifier;

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

    /// <summary>
    /// Should this request be audited?
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    private static bool ShouldAuditRequest(HttpContext context)
    {
        if (context.Request.Method == "OPTIONS")
            return false;

        string path = context.Request.Path.ToString();

        foreach (string excluded in ExcludedPaths)
        {
            if (path.StartsWith(excluded, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}