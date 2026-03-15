using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.AspNetCore.Configuration;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Database.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure audit using the fluent builder API
builder.Services.AddMillWorksAudit(auditBuilder =>
{
    // Set application options
    auditBuilder.Options.ApplicationName = "SampleApp";
    auditBuilder.Options.Environment = builder.Environment.EnvironmentName;

    // Configure Entity Framework
    auditBuilder.UseEntityFramework(ef =>
    {
        ef.ConnectionString = builder.Configuration
            .GetConnectionString("DefaultConnection")!;
        ef.MigrateOnStartup = false;
        ef.EnsureDatabaseCreated = true;
        ef.Schema = "audit";
    });

    // Configure security
    auditBuilder.UseSecurity(static security =>
    {
        security.EnableTamperDetection = true;
        security.UseRedisLocking = false;
    });

    // Configure resilience
    auditBuilder.UseResilience(static resilience =>
    {
        resilience.EnableDeadLetterQueue = true;
        resilience.DeadLetterProvider = DeadLetterProvider.FileSystem;
        resilience.EnableBackgroundProcessor = true;
    });

    // Configure compliance
    auditBuilder.UseCompliance(static compliance =>
    {
        compliance.Standards.Add(ComplianceStandard.GDPR);
        compliance.Standards.Add(ComplianceStandard.SOC2);
        compliance.EnableAutomaticValidation = true;
        compliance.DataRetentionDays = 730; // 2 years
    });

    // Configure archival (if you have Azure Blob Storage)
    var azureStorageConnection = builder.Configuration.GetConnectionString("AzureStorage");
    if (!string.IsNullOrEmpty(azureStorageConnection))
    {
        auditBuilder.UseArchival(archival =>
        {
            archival.Provider = ArchivalProvider.AzureBlob;
            archival.ConnectionString = azureStorageConnection;
            archival.EnableBackgroundArchival = true;
            archival.RetentionDays = 365;
            archival.ArchivalIntervalHours = 24;
            archival.ContainerName = "audit-archives";
        });
    }

    // Configure Field-Level Encryption
    // Option A: Azure Key Vault (recommended for cloud deployments)
    var keyVaultUrl = builder.Configuration["KeyVault:Url"];
    if (!string.IsNullOrEmpty(keyVaultUrl))
    {
        auditBuilder.UseFieldEncryption(keyVaultUrl);
    }
    // Option B: File-based encryption (for DMZ/air-gapped deployments)
    else if (builder.Configuration.GetValue<bool>("Encryption:UseFileStorage"))
    {
        var keyStorePath = builder.Configuration["Encryption:KeyStorePath"] 
            ?? "/secure/encryption-keys";
        var masterKey = builder.Configuration["Encryption:MasterKey"];
        
        if (!string.IsNullOrEmpty(masterKey))
        {
            auditBuilder.UseFieldEncryptionWithFileStorage(keyStorePath, masterKey);
        }
    }

    // Register custom providers
    auditBuilder.RegisterProviders(static registry =>
    {
        // Example: Add custom provider for a specific entity
        // registry.AddProvider<MyCustomAuditProvider>("MyEntity");
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseMillWorksAudit(); // Audit middleware
app.UseAuthorization();
app.MapControllers();

app.Run();