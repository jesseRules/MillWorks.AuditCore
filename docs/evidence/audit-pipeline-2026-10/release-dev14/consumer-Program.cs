using System.Reflection;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Query;
var request = new AuditSearchRequest { EventId=Guid.NewGuid(), EntityId="dev14-smoke" };
var ef=typeof(AuditEventPaging).Assembly;
if (ef.GetType("MillWorks.AuditCore.EntityFramework.Migrations.AddAuditEventWordSearch") is null) throw new Exception("Missing migration");
if (!ef.GetManifestResourceNames().Contains("MillWorks.AuditCore.EntityFramework.Sql.InstallAuditEventFullText.sql")) throw new Exception("Missing installer");
foreach (var name in new[]{"MillWorks.AuditCore.Abstractions","MillWorks.AuditCore.EntityFramework","MillWorks.AuditCore.Providers","MillWorks.AuditCore.Services","MillWorks.AuditCore.AspNetCore"}) {
 var a=Assembly.Load(name); var version=a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
 if (!version.StartsWith("1.15.4-dev.14")) throw new Exception(name+" wrong version");
 Console.WriteLine(name+" "+version);
}
try { throw new AuditSearchUnavailableException(); } catch(AuditSearchUnavailableException) { }
Console.WriteLine("PASS: packaged paging helper, migration, embedded installer and all five versions.");
