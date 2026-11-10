using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.Services.Query;
using System.Text.Json;
var eventId = Guid.NewGuid();
var request = new AuditSearchRequest { EventId = eventId, EntityId = "contract-smoke" };
if (request.EventId != eventId || request.EntityId != "contract-smoke") throw new Exception("Query contract mismatch");
var json = JsonSerializer.Serialize(request);
if (!json.Contains("event_id") || !json.Contains("entity_id")) throw new Exception("Wire contract mismatch");
try { throw new AuditSearchUnavailableException(); }
catch (AuditSearchUnavailableException) { Console.WriteLine("Verified EventId, EntityId and AuditSearchUnavailableException from dev.13 packages."); }
foreach(var name in new[]{"MillWorks.AuditCore.Abstractions", "MillWorks.AuditCore.EntityFramework", "MillWorks.AuditCore.Providers", "MillWorks.AuditCore.Services", "MillWorks.AuditCore.AspNetCore"}) {
 var asm=System.Reflection.Assembly.Load(name);
 var version=asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute),false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
 if (!version.StartsWith("1.15.4-dev.13")) throw new Exception(name+": "+version);
 Console.WriteLine(name+" "+version);
}
