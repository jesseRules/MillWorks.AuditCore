BEGIN TRANSACTION;
ALTER TABLE [audit].[AuditOutbox] ADD [FailureCycleId] uniqueidentifier NULL;

ALTER TABLE [audit].[AuditOutbox] ADD [FailureTenantId] uniqueidentifier NULL;

ALTER TABLE [audit].[AuditOutbox] ADD [ResolutionId] uniqueidentifier NULL;

CREATE TABLE [audit].[AuditOutboxFailureHandoffs] (
    [Id] uniqueidentifier NOT NULL,
    [OutboxRowId] uniqueidentifier NOT NULL,
    [FailureCycleId] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NULL,
    [EnvelopeJson] nvarchar(max) NOT NULL,
    [ReasonCode] nvarchar(80) NOT NULL,
    [Status] int NOT NULL,
    [CreatedAt] datetimeoffset NOT NULL,
    [CompletedAt] datetimeoffset NULL,
    [NextRetryAt] datetimeoffset NULL,
    [ClaimToken] nvarchar(100) NULL,
    [LeaseExpiresAt] datetimeoffset NULL,
    [AttemptCount] int NOT NULL,
    CONSTRAINT [PK_AuditOutboxFailureHandoffs] PRIMARY KEY ([Id])
);

CREATE TABLE [audit].[AuditOutboxOperations] (
    [Id] uniqueidentifier NOT NULL,
    [RequestHash] nvarchar(64) NOT NULL,
    [Kind] nvarchar(40) NOT NULL,
    [ResultJson] nvarchar(max) NOT NULL,
    [CreatedAt] datetimeoffset NOT NULL,
    [VerifyUntil] datetimeoffset NOT NULL,
    CONSTRAINT [PK_AuditOutboxOperations] PRIMARY KEY ([Id])
);

CREATE TABLE [audit].[AuditOutboxResolutions] (
    [Id] uniqueidentifier NOT NULL,
    [OutboxRowId] uniqueidentifier NOT NULL,
    [FailureCycleId] uniqueidentifier NOT NULL,
    [TenantId] uniqueidentifier NULL,
    [ActorId] nvarchar(256) NOT NULL,
    [Reason] nvarchar(2000) NOT NULL,
    [Action] nvarchar(16) NOT NULL,
    [CreatedAt] datetimeoffset NOT NULL,
    CONSTRAINT [PK_AuditOutboxResolutions] PRIMARY KEY ([Id])
);

CREATE UNIQUE INDEX [IX_AuditOutboxFailureHandoffs_OutboxRowId_FailureCycleId] ON [audit].[AuditOutboxFailureHandoffs] ([OutboxRowId], [FailureCycleId]);

CREATE INDEX [IX_AuditOutboxFailureHandoffs_Status_NextRetryAt_LeaseExpiresAt_CreatedAt] ON [audit].[AuditOutboxFailureHandoffs] ([Status], [NextRetryAt], [LeaseExpiresAt], [CreatedAt]);

CREATE INDEX [IX_AuditOutboxOperations_VerifyUntil] ON [audit].[AuditOutboxOperations] ([VerifyUntil]);

CREATE INDEX [IX_AuditOutboxResolutions_OutboxRowId_FailureCycleId] ON [audit].[AuditOutboxResolutions] ([OutboxRowId], [FailureCycleId]);

INSERT INTO [audit].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261016210852_AddOutboxOwnershipEvidence', N'10.0.13');

COMMIT;
GO

BEGIN TRANSACTION;
ALTER TABLE [audit].[AuditOutboxFailureHandoffs] ADD CONSTRAINT [FK_AuditOutboxFailureHandoffs_AuditOutbox_OutboxRowId] FOREIGN KEY ([OutboxRowId]) REFERENCES [audit].[AuditOutbox] ([Id]) ON DELETE NO ACTION;

INSERT INTO [audit].[__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20261016211640_RetainOutboxFailureSources', N'10.0.13');

COMMIT;
GO

