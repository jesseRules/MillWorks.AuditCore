using System.Reflection;
using MillWorks.AuditCore.Services.Administration;
using MillWorks.AuditCore.Services.Sinks.Recovery;
using MillWorks.AuditCore.EntityFramework.Data;
if (typeof(IAuditOutboxRecovery).GetMethod("CleanupCompletedAsync") is null) throw new Exception("Missing guarded cleanup API");
var scope = new AuditAdministrationScope(Guid.NewGuid(), Guid.NewGuid(), "dev12-package-smoke");
if (scope.TenantId == Guid.Empty || !typeof(IAuditAdministrationAuthority).IsInterface || !typeof(IAuditAdministrationService).IsInterface || typeof(AuditDbContext).Name != "AuditDbContext") throw new Exception("Missing dev.12 API");
var operation = Guid.NewGuid();
var conflict = new AuditOutboxOperationConflictException(operation);
if (conflict.OperationId != operation || conflict is not InvalidOperationException) throw new Exception("Missing typed operation conflict");
foreach (var name in new[] { "Abstractions", "EntityFramework", "Providers", "Services", "AspNetCore" })
{
 var assembly = Assembly.Load("MillWorks.AuditCore." + name);
 var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
 if (!version.StartsWith("1.15.4-dev.12+", StringComparison.Ordinal)) throw new Exception($"Unexpected version: {name} {version}");
 Console.WriteLine($"{assembly.GetName().Name}: {version}");
}
Console.WriteLine("Package-only consumer passed; no database opened.");
