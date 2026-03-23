using Microsoft.AspNetCore.Builder;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.AspNetCore.Extensions;

/// <summary>
/// ApplicationBuilderExtensions provides extension methods for IApplicationBuilder to integrate MillWorks Audit middleware.
/// Also includes migration helpers for web applications.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <param name="app"></param>
    extension(IApplicationBuilder app)
    {
        /// <summary>
        /// UseMillWorksAudit adds the AuditContextMiddleware to the ASP.NET Core middleware pipeline.
        /// </summary>
        /// <returns></returns>
        public void UseMillWorksAudit() => app.UseMiddleware<AuditContextMiddleware>();

        /// <summary>
        /// Runs database migrations for the audit system during application startup.
        /// Migrate() creates the database if it doesn't exist AND applies migrations.
        /// </summary>
        /// <returns>Application builder for chaining</returns>
        public IApplicationBuilder RunAuditMigrations()
        {
            app.ApplicationServices.RunAuditMigrations();
            return app;
        }

    }
}
