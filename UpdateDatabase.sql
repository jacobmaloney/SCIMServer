-- Script to update existing SCIMServer database to latest schema

-- 1. Update Groups table to add Owner
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Groups]') AND name = 'OwnerId')
BEGIN
    ALTER TABLE [dbo].[Groups] ADD [OwnerId] UNIQUEIDENTIFIER NULL;
    ALTER TABLE [dbo].[Groups] ADD CONSTRAINT [FK_Groups_Owner] FOREIGN KEY ([OwnerId]) REFERENCES [Users]([Id]);
    CREATE INDEX [IX_Groups_OwnerId] ON [Groups]([OwnerId]);
END

-- 2. Update Groups table to add Type column if it doesn't exist
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[Groups]') AND name = 'Type')
BEGIN
    ALTER TABLE [dbo].[Groups] ADD [Type] NVARCHAR(50) NULL;
END

-- 3. Create or update ApiTokens table
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'ApiTokens')
BEGIN
    -- Drop the old table
    DROP TABLE [dbo].[ApiTokens];
END

-- Create new ApiTokens table with correct schema
CREATE TABLE [dbo].[ApiTokens] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Name] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [TokenHash] NVARCHAR(500) NOT NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastUsedAt] DATETIME2 NULL,
    [ExpiresAt] DATETIME2 NULL,
    [IsActive] BIT NOT NULL DEFAULT 1,
    CONSTRAINT [PK_ApiTokens] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_ApiTokens_TokenHash] UNIQUE ([TokenHash])
);

CREATE INDEX [IX_ApiTokens_TokenHash] ON [ApiTokens]([TokenHash]);
CREATE INDEX [IX_ApiTokens_IsActive] ON [ApiTokens]([IsActive]);

-- 4. Create or update AuditLogs table
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'AuditLogs')
BEGIN
    -- Drop the old table
    DROP TABLE [dbo].[AuditLogs];
END

-- Create new AuditLogs table with correct schema
CREATE TABLE [dbo].[AuditLogs] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Timestamp] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Action] NVARCHAR(50) NOT NULL,
    [ResourceType] NVARCHAR(50) NOT NULL,
    [ResourceId] NVARCHAR(255) NULL,
    [UserId] NVARCHAR(255) NULL,
    [UserName] NVARCHAR(255) NULL,
    [IpAddress] NVARCHAR(50) NULL,
    [UserAgent] NVARCHAR(500) NULL,
    [StatusCode] INT NULL,
    [Details] NVARCHAR(MAX) NULL,
    [OldValue] NVARCHAR(MAX) NULL,
    [NewValue] NVARCHAR(MAX) NULL,
    [Duration] TIME NULL,
    CONSTRAINT [PK_AuditLogs] PRIMARY KEY CLUSTERED ([Id])
);

CREATE INDEX [IX_AuditLogs_Timestamp] ON [AuditLogs]([Timestamp]);
CREATE INDEX [IX_AuditLogs_ResourceType] ON [AuditLogs]([ResourceType]);
CREATE INDEX [IX_AuditLogs_Action] ON [AuditLogs]([Action]);
CREATE INDEX [IX_AuditLogs_UserId] ON [AuditLogs]([UserId]);

PRINT 'Database update completed successfully!';