using System.Reflection;
using System.Text.Json;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks.Identity;

const string expected = "1.15.4-dev.8+6f560a17dd4ced07ff19d410ecc79beb56eda068";
Type[] types = [typeof(AuditEnvelope), typeof(AuditDbContext), typeof(AuditLogger),
    typeof(MillWorks.AuditCore.Providers.Implementations.UserAuditProvider),
    typeof(MillWorks.AuditCore.AspNetCore.Extensions.ServiceCollectionExtensions)];
foreach (var type in types)
{
    var version = type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    if (version != expected) throw new InvalidOperationException($"Unexpected assembly: {type.Assembly.GetName().Name} {version}");
    Console.WriteLine($"{type.Assembly.GetName().Name}: {version}");
}
if (!typeof(IAuditEnvelopeBatchLogger).IsAssignableFrom(typeof(AuditLogger))) throw new InvalidOperationException("Missing envelope capability");
if (AuditEnvelopeContent.Version != 1) throw new InvalidOperationException("Wrong comparison version");
_ = new AuditEventOutcome(Guid.NewGuid(), AuditEventWriteStatus.Duplicate);
_ = new AuditEnvelopeIdentity();
using var assets = JsonDocument.Parse(File.ReadAllText("/tmp/auditcore-dev8-consumer/obj/project.assets.json"));
var resolved = assets.RootElement.GetProperty("libraries").EnumerateObject()
    .Where(x => x.Name.StartsWith("MillWorks.AuditCore", StringComparison.Ordinal)).Select(x => x.Name).ToArray();
if (resolved.Length != 5 || resolved.Any(x => !x.EndsWith("/1.15.4-dev.8", StringComparison.Ordinal)))
    throw new InvalidOperationException("Unexpected package graph: " + string.Join(", ", resolved));
Console.WriteLine("Package-only consumer verified all five dev.8 dependencies and new public contracts.");
