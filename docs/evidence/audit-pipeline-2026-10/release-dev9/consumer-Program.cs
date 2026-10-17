using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using System.Reflection;
using System.Text.Json;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks.Identity;

const string expected = "1.15.4-dev.9+18090b6977a46ff76eac8d34a14843e9b685f1c3";
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
using var assets = JsonDocument.Parse(File.ReadAllText("/tmp/auditcore-dev9-consumer/obj/project.assets.json"));
var resolved = assets.RootElement.GetProperty("libraries").EnumerateObject()
    .Where(x => x.Name.StartsWith("MillWorks.AuditCore", StringComparison.Ordinal)).Select(x => x.Name).ToArray();
if (resolved.Length != 5 || resolved.Any(x => !x.EndsWith("/1.15.4-dev.9", StringComparison.Ordinal)))
    throw new InvalidOperationException("Unexpected package graph: " + string.Join(", ", resolved));
Console.WriteLine("Package-only consumer verified all five dev.9 dependencies and new public contracts.");

var options = new DbContextOptionsBuilder<TokenContext>().UseSqlServer(
    "Server=unused;Database=unused;Integrated Security=true;TrustServerCertificate=true").Options;
AuditMutationToken original;
using (var first = new TokenContext(options))
{
    var entity = new TokenRow(); first.Add(entity);
    original = AuditMutationIdentity.Capture(first, entity);
    if (!AuditMutationIdentity.IsUnresolvedKey(first, entity, nameof(TokenRow.Id))) throw new Exception("Missing key provenance");
}
original = JsonSerializer.Deserialize<AuditMutationToken>(JsonSerializer.Serialize(original))!;
using (var second = new TokenContext(options))
{
    var entity = new TokenRow(); second.Add(entity);
    AuditMutationIdentity.Restore(second, entity, original);
    if (AuditMutationIdentity.Capture(second, entity).Id != original.Id) throw new Exception("Token replay failed");
}
if (typeof(AuditEnvelope).GetMethod("ComputeDeterministicId") is not null) throw new Exception("Obsolete hash identity remains");
Console.WriteLine("Mutation token capture/serialization/restoration verified with packaged SQL model metadata; no database opened.");
sealed class TokenContext(DbContextOptions<TokenContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder) => builder.Entity<TokenRow>().HasKey(x => x.Id);
}
sealed class TokenRow { public int Id { get; set; } }
