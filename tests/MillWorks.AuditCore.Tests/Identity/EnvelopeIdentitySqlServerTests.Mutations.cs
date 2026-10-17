using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Identity;

public sealed partial class EnvelopeIdentitySqlServerTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task GeneratedIntegerAndCompositeKeysUseRetainedMutationTokens(bool composite)
    {
        await using (var setup = Context())
            await setup.Database.ExecuteSqlRawAsync("""
                IF OBJECT_ID('dbo.MutationIntegerClaims') IS NULL
                    CREATE TABLE dbo.MutationIntegerClaims (Id int IDENTITY PRIMARY KEY, RoleId uniqueidentifier NOT NULL, Value nvarchar(100) NOT NULL);
                IF OBJECT_ID('dbo.MutationCompositeClaims') IS NULL
                    CREATE TABLE dbo.MutationCompositeClaims (Id int IDENTITY, RoleId uniqueidentifier NOT NULL, Value nvarchar(100) NOT NULL, PRIMARY KEY(RoleId,Id));
                """);
        await using var writerProvider = new ServiceCollection()
            .AddDbContext<AuditDbContext>(o => o.UseSqlServer(_connection)).BuildServiceProvider();
        var sink = new MutationSink(new AuditEntityBatchWriter(writerProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditEntityBatchWriter>.Instance));
        var services = new ServiceCollection();
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddSingleton<IAuditSink>(sink);
        await using var provider = services.BuildServiceProvider();
        var interceptor = new AuditSaveChangesInterceptor(NullLogger<AuditSaveChangesInterceptor>.Instance,
            failureMode: AuditFailureMode.FailClosedAlways, scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());
        var options = new DbContextOptionsBuilder<MutationConsumer>().UseSqlServer(_connection)
            .AddInterceptors(interceptor).Options;
        var roleId = Guid.NewGuid();
        AuditMutationToken token;
        await using (var first = new MutationConsumer(options))
        {
            var row = Add(first, composite, roleId, -123);
            token = AuditMutationIdentity.Capture(first, row);
            await Assert.ThrowsAsync<AuditIntegrityException>(() => first.SaveChangesAsync());
        }
        await using (var retry = new MutationConsumer(options))
        {
            var row = Add(retry, composite, roleId, -456);
            AuditMutationIdentity.Restore(retry, row, token);
            await retry.SaveChangesAsync();
            Assert.That((int)retry.Entry(row).Property("Id").CurrentValue!, Is.GreaterThan(0));
        }
        await using (var next = new MutationConsumer(options))
        {
            Add(next, composite, roleId, -123);
            await next.SaveChangesAsync();
        }
        Assert.Multiple(() =>
        {
            Assert.That(sink.Inputs, Has.Count.EqualTo(3));
            Assert.That(sink.Inputs[1].EnvelopeId, Is.EqualTo(sink.Inputs[0].EnvelopeId));
            Assert.That(sink.Inputs[2].EnvelopeId, Is.Not.EqualTo(sink.Inputs[0].EnvelopeId));
            Assert.That(sink.Inputs.All(static e => e.EntityIdString is null), Is.True);
            Assert.That(sink.Duplicates, Is.EqualTo(1));
        });
        await using var verify = Context();
        Assert.That(await verify.AuditLogs.CountAsync(), Is.EqualTo(2));
        Assert.That(await verify.Set<MillWorks.AuditCore.EntityFramework.Entities.AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(2));
    }

    private static object Add(MutationConsumer db, bool composite, Guid roleId, int temporaryKey)
    {
        object row = composite ? new CompositeClaim { RoleId = roleId, Value = "permission" }
            : new IntegerClaim { RoleId = roleId, Value = "permission" };
        var entry = db.Add(row);
        entry.Property("Id").CurrentValue = temporaryKey;
        entry.Property("Id").IsTemporary = true;
        return row;
    }

    private sealed class MutationConsumer(DbContextOptions<MutationConsumer> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<IntegerClaim>().ToTable("MutationIntegerClaims", "dbo").HasKey(x => x.Id);
            model.Entity<CompositeClaim>().ToTable("MutationCompositeClaims", "dbo").HasKey(x => new { x.RoleId, x.Id });
            model.Entity<CompositeClaim>().Property(x => x.Id).ValueGeneratedOnAdd();
        }
    }
    private sealed class IntegerClaim
    {
        public int Id { get; set; }
        public Guid RoleId { get; set; }
        public string Value { get; set; } = "";
    }
    private sealed class CompositeClaim
    {
        public int Id { get; set; }
        public Guid RoleId { get; set; }
        public string Value { get; set; } = "";
    }
    private sealed class MutationSink(AuditEntityBatchWriter writer) : IAuditSink
    {
        private bool _loseAcknowledgement = true;
        internal List<AuditEnvelope> Inputs { get; } = [];
        internal int Duplicates { get; private set; }
        public Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default) =>
            PublishBatchAsync([envelope], cancellationToken);
        public async Task PublishBatchAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken cancellationToken = default)
        {
            Inputs.AddRange(envelopes);
            var results = await writer.WriteBatchAsync(envelopes, cancellationToken);
            Assert.That(results.All(static r => r.Succeeded), Is.True);
            Duplicates += results.Count(static r => r.IsDuplicate);
            if (_loseAcknowledgement)
            {
                _loseAcknowledgement = false;
                throw new InvalidOperationException("Lost acknowledgement after durable audit commit.");
            }
        }
    }
}
