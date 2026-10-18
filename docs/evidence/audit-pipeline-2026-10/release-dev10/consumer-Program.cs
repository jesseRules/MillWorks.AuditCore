using System.Reflection;
using MillWorks.AuditCore.Services.Administration;
using MillWorks.AuditCore.EntityFramework.Data;
var scope = new AuditAdministrationScope(Guid.NewGuid(), Guid.NewGuid(), "dev10-package-smoke");
if (scope.TenantId == Guid.Empty || !typeof(IAuditAdministrationAuthority).IsInterface || !typeof(IAuditAdministrationService).IsInterface || typeof(AuditDbContext).Name != "AuditDbContext") throw new Exception("Missing dev.10 API");
foreach (var name in new[] { "Abstractions", "EntityFramework", "Providers", "Services", "AspNetCore" })
{
 var assembly = Assembly.Load("MillWorks.AuditCore." + name);
 var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
 if (!version.StartsWith("1.15.4-dev.10+", StringComparison.Ordinal)) throw new Exception($"Unexpected version: {name} {version}");
 Console.WriteLine($"{assembly.GetName().Name}: {version}");
}
Console.WriteLine("Package-only consumer passed; no database opened.");
