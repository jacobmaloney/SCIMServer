/*
 * SCIM Server Database Schema
 * 
 * This script creates the database schema for the SCIM Server application.
 * It includes tables for users, groups, roles, custom attributes, and organizational structures.
 * 
 * Run this script against SQL Server to set up the database.
 */

-- Create database (uncomment if needed)
-- CREATE DATABASE SCIMServer;
-- GO
-- USE SCIMServer;
-- GO

-- =============================================
-- Users Table
-- =============================================
CREATE TABLE [dbo].[Users] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [ExternalId] NVARCHAR(255) NULL,
    [UserName] NVARCHAR(255) NOT NULL,
    [Active] BIT NOT NULL DEFAULT 1,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Version] INT NOT NULL DEFAULT 1,
    -- Name attributes
    [FormattedName] NVARCHAR(500) NULL,
    [FamilyName] NVARCHAR(255) NULL,
    [GivenName] NVARCHAR(255) NULL,
    [MiddleName] NVARCHAR(255) NULL,
    [HonorificPrefix] NVARCHAR(50) NULL,
    [HonorificSuffix] NVARCHAR(50) NULL,
    -- Profile attributes
    [DisplayName] NVARCHAR(255) NULL,
    [NickName] NVARCHAR(255) NULL,
    [ProfileUrl] NVARCHAR(500) NULL,
    [Title] NVARCHAR(255) NULL,
    [UserType] NVARCHAR(255) NULL,
    [PreferredLanguage] NVARCHAR(10) NULL,
    [Locale] NVARCHAR(10) NULL,
    [Timezone] NVARCHAR(50) NULL,
    -- Password (hashed)
    [PasswordHash] NVARCHAR(500) NULL,
    [PasswordSalt] NVARCHAR(255) NULL,
    -- Enterprise extension
    [EmployeeNumber] NVARCHAR(50) NULL,
    [CostCenter] NVARCHAR(50) NULL,
    [Organization] NVARCHAR(255) NULL,
    [Division] NVARCHAR(255) NULL,
    [Department] NVARCHAR(255) NULL,
    [ManagerId] UNIQUEIDENTIFIER NULL,
    CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Users_Manager] FOREIGN KEY ([ManagerId]) REFERENCES [Users]([Id]),
    CONSTRAINT [UQ_Users_UserName] UNIQUE ([UserName])
);

CREATE INDEX [IX_Users_UserName] ON [Users]([UserName]);
CREATE INDEX [IX_Users_ExternalId] ON [Users]([ExternalId]);
CREATE INDEX [IX_Users_Active] ON [Users]([Active]);
CREATE INDEX [IX_Users_ManagerId] ON [Users]([ManagerId]);

-- =============================================
-- User Emails Table
-- =============================================
CREATE TABLE [dbo].[UserEmails] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(255) NOT NULL,
    [Type] NVARCHAR(50) NULL, -- work, home, other
    [Primary] BIT NOT NULL DEFAULT 0,
    [Display] NVARCHAR(255) NULL,
    CONSTRAINT [PK_UserEmails] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserEmails_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_UserEmails_UserId] ON [UserEmails]([UserId]);
CREATE INDEX [IX_UserEmails_Value] ON [UserEmails]([Value]);

-- =============================================
-- User Phone Numbers Table
-- =============================================
CREATE TABLE [dbo].[UserPhoneNumbers] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(50) NOT NULL,
    [Type] NVARCHAR(50) NULL, -- work, home, mobile, fax, pager, other
    [Primary] BIT NOT NULL DEFAULT 0,
    [Display] NVARCHAR(255) NULL,
    CONSTRAINT [PK_UserPhoneNumbers] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserPhoneNumbers_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_UserPhoneNumbers_UserId] ON [UserPhoneNumbers]([UserId]);

-- =============================================
-- User Addresses Table
-- =============================================
CREATE TABLE [dbo].[UserAddresses] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Type] NVARCHAR(50) NULL, -- work, home, other
    [StreetAddress] NVARCHAR(500) NULL,
    [Locality] NVARCHAR(255) NULL,
    [Region] NVARCHAR(255) NULL,
    [PostalCode] NVARCHAR(50) NULL,
    [Country] NVARCHAR(255) NULL,
    [Formatted] NVARCHAR(1000) NULL,
    [Primary] BIT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_UserAddresses] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserAddresses_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_UserAddresses_UserId] ON [UserAddresses]([UserId]);

-- =============================================
-- Groups Table
-- =============================================
CREATE TABLE [dbo].[Groups] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [ExternalId] NVARCHAR(255) NULL,
    [DisplayName] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Version] INT NOT NULL DEFAULT 1,
    CONSTRAINT [PK_Groups] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_Groups_DisplayName] UNIQUE ([DisplayName])
);

CREATE INDEX [IX_Groups_DisplayName] ON [Groups]([DisplayName]);
CREATE INDEX [IX_Groups_ExternalId] ON [Groups]([ExternalId]);

-- =============================================
-- Group Members Table
-- =============================================
CREATE TABLE [dbo].[GroupMembers] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [GroupId] UNIQUEIDENTIFIER NOT NULL,
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Added] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_GroupMembers] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_GroupMembers_Groups] FOREIGN KEY ([GroupId]) REFERENCES [Groups]([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_GroupMembers_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE,
    CONSTRAINT [UQ_GroupMembers_GroupUser] UNIQUE ([GroupId], [UserId])
);

CREATE INDEX [IX_GroupMembers_GroupId] ON [GroupMembers]([GroupId]);
CREATE INDEX [IX_GroupMembers_UserId] ON [GroupMembers]([UserId]);

-- =============================================
-- Roles Table
-- =============================================
CREATE TABLE [dbo].[Roles] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [ExternalId] NVARCHAR(255) NULL,
    [DisplayName] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Version] INT NOT NULL DEFAULT 1,
    CONSTRAINT [PK_Roles] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_Roles_DisplayName] UNIQUE ([DisplayName])
);

CREATE INDEX [IX_Roles_DisplayName] ON [Roles]([DisplayName]);

-- =============================================
-- User Roles Table
-- =============================================
CREATE TABLE [dbo].[UserRoles] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [RoleId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(255) NULL,
    [Display] NVARCHAR(255) NULL,
    [Type] NVARCHAR(50) NULL,
    [Primary] BIT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_UserRoles] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserRoles_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_UserRoles_Roles] FOREIGN KEY ([RoleId]) REFERENCES [Roles]([Id]) ON DELETE CASCADE,
    CONSTRAINT [UQ_UserRoles_UserRole] UNIQUE ([UserId], [RoleId])
);

CREATE INDEX [IX_UserRoles_UserId] ON [UserRoles]([UserId]);
CREATE INDEX [IX_UserRoles_RoleId] ON [UserRoles]([RoleId]);

-- =============================================
-- Custom Attributes Schema Table
-- =============================================
CREATE TABLE [dbo].[CustomAttributeSchemas] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [SchemaUrn] NVARCHAR(500) NOT NULL,
    [Name] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [Type] NVARCHAR(50) NOT NULL, -- string, integer, decimal, boolean, datetime, reference
    [MultiValued] BIT NOT NULL DEFAULT 0,
    [Required] BIT NOT NULL DEFAULT 0,
    [CaseExact] BIT NOT NULL DEFAULT 0,
    [Mutability] NVARCHAR(50) NOT NULL DEFAULT 'readWrite', -- readOnly, readWrite, immutable, writeOnly
    [Returned] NVARCHAR(50) NOT NULL DEFAULT 'default', -- always, never, default, request
    [Uniqueness] NVARCHAR(50) NOT NULL DEFAULT 'none', -- none, server, global
    [ResourceType] NVARCHAR(50) NOT NULL, -- User, Group, Role
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_CustomAttributeSchemas] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_CustomAttributeSchemas_SchemaName] UNIQUE ([SchemaUrn], [Name])
);

CREATE INDEX [IX_CustomAttributeSchemas_ResourceType] ON [CustomAttributeSchemas]([ResourceType]);

-- =============================================
-- Custom Attribute Values Table
-- =============================================
CREATE TABLE [dbo].[CustomAttributeValues] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [SchemaId] UNIQUEIDENTIFIER NOT NULL,
    [ResourceId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(MAX) NOT NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_CustomAttributeValues] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_CustomAttributeValues_Schema] FOREIGN KEY ([SchemaId]) REFERENCES [CustomAttributeSchemas]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_CustomAttributeValues_SchemaId] ON [CustomAttributeValues]([SchemaId]);
CREATE INDEX [IX_CustomAttributeValues_ResourceId] ON [CustomAttributeValues]([ResourceId]);

-- =============================================
-- Departments Table (for organizational structures)
-- =============================================
CREATE TABLE [dbo].[Departments] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Name] NVARCHAR(255) NOT NULL,
    [ParentId] UNIQUEIDENTIFIER NULL,
    [Level] INT NOT NULL DEFAULT 0,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_Departments] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Departments_Parent] FOREIGN KEY ([ParentId]) REFERENCES [Departments]([Id]),
    CONSTRAINT [UQ_Departments_Name] UNIQUE ([Name])
);

CREATE INDEX [IX_Departments_ParentId] ON [Departments]([ParentId]);

-- =============================================
-- API Tokens Table (for authentication testing)
-- =============================================
CREATE TABLE [dbo].[ApiTokens] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Name] NVARCHAR(255) NOT NULL,
    [Token] NVARCHAR(500) NOT NULL,
    [TokenHash] NVARCHAR(500) NOT NULL,
    [Type] NVARCHAR(50) NOT NULL, -- Bearer, Basic, ApiKey
    [Scopes] NVARCHAR(MAX) NULL,
    [Active] BIT NOT NULL DEFAULT 1,
    [ExpiresAt] DATETIME2 NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastUsed] DATETIME2 NULL,
    CONSTRAINT [PK_ApiTokens] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_ApiTokens_TokenHash] UNIQUE ([TokenHash])
);

CREATE INDEX [IX_ApiTokens_TokenHash] ON [ApiTokens]([TokenHash]);
CREATE INDEX [IX_ApiTokens_Active] ON [ApiTokens]([Active]);

-- =============================================
-- Audit Log Table
-- =============================================
CREATE TABLE [dbo].[AuditLogs] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Timestamp] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Action] NVARCHAR(50) NOT NULL, -- Created, Updated, Deleted, Read
    [ResourceType] NVARCHAR(50) NOT NULL,
    [ResourceId] UNIQUEIDENTIFIER NOT NULL,
    [UserId] UNIQUEIDENTIFIER NULL,
    [IpAddress] NVARCHAR(50) NULL,
    [UserAgent] NVARCHAR(500) NULL,
    [Changes] NVARCHAR(MAX) NULL,
    CONSTRAINT [PK_AuditLogs] PRIMARY KEY CLUSTERED ([Id])
);

CREATE INDEX [IX_AuditLogs_Timestamp] ON [AuditLogs]([Timestamp]);
CREATE INDEX [IX_AuditLogs_ResourceId] ON [AuditLogs]([ResourceId]);

-- =============================================
-- System Configuration Table
-- =============================================
CREATE TABLE [dbo].[SystemConfiguration] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Key] NVARCHAR(255) NOT NULL,
    [Value] NVARCHAR(MAX) NOT NULL,
    [Type] NVARCHAR(50) NOT NULL, -- String, Integer, Boolean, Json
    [Description] NVARCHAR(1000) NULL,
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_SystemConfiguration] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_SystemConfiguration_Key] UNIQUE ([Key])
);

-- Insert default configuration
INSERT INTO [SystemConfiguration] ([Key], [Value], [Type], [Description])
VALUES 
    ('MaxPageSize', '1000', 'Integer', 'Maximum number of results per page'),
    ('DefaultPageSize', '100', 'Integer', 'Default number of results per page'),
    ('EnableCustomAttributes', 'true', 'Boolean', 'Enable custom attributes feature'),
    ('EnableAuditLog', 'true', 'Boolean', 'Enable audit logging'),
    ('TokenExpiration', '3600', 'Integer', 'Default token expiration in seconds'),
    ('EnableUserGeneration', 'true', 'Boolean', 'Enable user generation feature');

-- =============================================
-- Stored Procedures
-- =============================================

-- Get User with all related data
CREATE PROCEDURE [dbo].[GetUserById]
    @UserId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    
    -- Get user
    SELECT * FROM [Users] WHERE [Id] = @UserId;
    
    -- Get emails
    SELECT * FROM [UserEmails] WHERE [UserId] = @UserId;
    
    -- Get phone numbers
    SELECT * FROM [UserPhoneNumbers] WHERE [UserId] = @UserId;
    
    -- Get addresses
    SELECT * FROM [UserAddresses] WHERE [UserId] = @UserId;
    
    -- Get groups
    SELECT g.* FROM [Groups] g
    INNER JOIN [GroupMembers] gm ON g.[Id] = gm.[GroupId]
    WHERE gm.[UserId] = @UserId;
    
    -- Get roles
    SELECT r.*, ur.[Value], ur.[Display], ur.[Type], ur.[Primary] 
    FROM [Roles] r
    INNER JOIN [UserRoles] ur ON r.[Id] = ur.[RoleId]
    WHERE ur.[UserId] = @UserId;
    
    -- Get custom attributes
    SELECT cas.*, cav.[Value] 
    FROM [CustomAttributeSchemas] cas
    INNER JOIN [CustomAttributeValues] cav ON cas.[Id] = cav.[SchemaId]
    WHERE cav.[ResourceId] = @UserId AND cas.[ResourceType] = 'User';
END
GO