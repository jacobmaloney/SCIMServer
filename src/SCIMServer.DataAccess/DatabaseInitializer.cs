using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SCIMServer.DataAccess
{
    /// <summary>
    /// Handles database initialization and migration
    /// </summary>
    public class DatabaseInitializer
    {
        private readonly DatabaseConfig _config;
        private readonly ILogger<DatabaseInitializer> _logger;

        /// <summary>
        /// Initializes a new instance of the DatabaseInitializer class
        /// </summary>
        /// <param name="config">Database configuration</param>
        /// <param name="logger">Logger instance</param>
        public DatabaseInitializer(DatabaseConfig config, ILogger<DatabaseInitializer>? logger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? NullLogger<DatabaseInitializer>.Instance;
        }

        /// <summary>
        /// Initializes the database, creating it if necessary
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_config.AutoCreateDatabase)
            {
                await EnsureDatabaseExistsAsync();
            }

            if (_config.AutoMigrate)
            {
                await RunMigrationsAsync();
            }
        }

        /// <summary>
        /// Ensures the database exists, creating it if necessary
        /// </summary>
        private async Task EnsureDatabaseExistsAsync()
        {
            var builder = new SqlConnectionStringBuilder(_config.ConnectionString);
            var databaseName = builder.InitialCatalog;
            builder.InitialCatalog = "master";

            using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();

            var exists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM sys.databases WHERE name = @name",
                new { name = databaseName }) > 0;

            if (!exists)
            {
                await connection.ExecuteAsync($"CREATE DATABASE [{databaseName}]");
                Console.WriteLine($"Created database: {databaseName}");
            }
        }

        /// <summary>
        /// Runs database migrations
        /// </summary>
        private async Task RunMigrationsAsync()
        {
            // Create migrator with logger
            var migrator = new DatabaseMigrator(_config.ConnectionString, NullLogger<DatabaseMigrator>.Instance);

            // Ensure schema version table exists
            await migrator.EnsureSchemaVersionTableAsync();

            // Analyze current schema
            var analysis = await migrator.AnalyzeSchemaAsync();

            // If essential tables don't exist, run initial schema
            var essentialTables = new[] { "Users", "Groups", "GroupMembers" };
            var missingEssentialTables = essentialTables.Where(t => !analysis.ExistingTables.Contains(t)).ToList();
            
            if (missingEssentialTables.Any())
            {
                _logger.LogInformation($"Missing essential tables: {string.Join(", ", missingEssentialTables)}. Running initial schema creation.");
                using var connection = new SqlConnection(_config.ConnectionString);
                await connection.OpenAsync();
                await RunInitialSchemaAsync(connection);
            }
            else
            {
                // Apply any pending migrations
                var migrations = migrator.GetRequiredMigrations(analysis);
                if (migrations.Any())
                {
                    _logger.LogInformation($"Found {migrations.Count} migrations to apply");
                    await migrator.ApplyMigrationsAsync(migrations);
                }
                else
                {
                    _logger.LogInformation("Database schema is up to date");
                }
            }
        }

        /// <summary>
        /// Runs the initial database schema creation
        /// </summary>
        private async Task RunInitialSchemaAsync(IDbConnection connection)
        {
            var schemaScript = GetInitialSchemaScript();
            
            // Split by GO statements and execute each batch
            var batches = schemaScript.Split(new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n" }, 
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var batch in batches)
            {
                if (!string.IsNullOrWhiteSpace(batch))
                {
                    await connection.ExecuteAsync(batch);
                }
            }

            // Record the schema version if not already present
            var versionExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM [SchemaVersion] WHERE [Version] = 1") > 0;
                
            if (!versionExists)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO [SchemaVersion] ([Version], [Description]) VALUES (@Version, @Description)",
                    new { Version = 1, Description = "Initial schema creation" });
            }

            Console.WriteLine("Database schema created successfully");
        }

        /// <summary>
        /// Gets the initial schema creation script
        /// </summary>
        private string GetInitialSchemaScript()
        {
            // In a real application, this might read from an embedded resource or file
            // For now, we'll include the essential tables inline
            return @"
-- Users Table
CREATE TABLE [dbo].[Users] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [ExternalId] NVARCHAR(255) NULL,
    [UserName] NVARCHAR(255) NOT NULL,
    [Active] BIT NOT NULL DEFAULT 1,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Version] INT NOT NULL DEFAULT 1,
    [FormattedName] NVARCHAR(500) NULL,
    [FamilyName] NVARCHAR(255) NULL,
    [GivenName] NVARCHAR(255) NULL,
    [MiddleName] NVARCHAR(255) NULL,
    [HonorificPrefix] NVARCHAR(50) NULL,
    [HonorificSuffix] NVARCHAR(50) NULL,
    [DisplayName] NVARCHAR(255) NULL,
    [NickName] NVARCHAR(255) NULL,
    [ProfileUrl] NVARCHAR(500) NULL,
    [Title] NVARCHAR(255) NULL,
    [UserType] NVARCHAR(255) NULL,
    [PreferredLanguage] NVARCHAR(10) NULL,
    [Locale] NVARCHAR(10) NULL,
    [Timezone] NVARCHAR(50) NULL,
    [PasswordHash] NVARCHAR(500) NULL,
    [PasswordSalt] NVARCHAR(255) NULL,
    [EmployeeNumber] NVARCHAR(50) NULL,
    [CostCenter] NVARCHAR(50) NULL,
    [Organization] NVARCHAR(255) NULL,
    [Division] NVARCHAR(255) NULL,
    [Department] NVARCHAR(255) NULL,
    [ManagerId] UNIQUEIDENTIFIER NULL,
    [IsAdmin] BIT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_Users_Manager] FOREIGN KEY ([ManagerId]) REFERENCES [Users]([Id]),
    CONSTRAINT [UQ_Users_UserName] UNIQUE ([UserName])
);

CREATE INDEX [IX_Users_UserName] ON [Users]([UserName]);
CREATE INDEX [IX_Users_ExternalId] ON [Users]([ExternalId]);
CREATE INDEX [IX_Users_Active] ON [Users]([Active]);
CREATE INDEX [IX_Users_ManagerId] ON [Users]([ManagerId]);
CREATE INDEX [IX_Users_IsAdmin] ON [Users]([IsAdmin]) WHERE [IsAdmin] = 1;

-- User Emails Table
CREATE TABLE [dbo].[UserEmails] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(255) NOT NULL,
    [Type] NVARCHAR(50) NULL,
    [Primary] BIT NOT NULL DEFAULT 0,
    [Display] NVARCHAR(255) NULL,
    CONSTRAINT [PK_UserEmails] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserEmails_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_UserEmails_UserId] ON [UserEmails]([UserId]);
CREATE INDEX [IX_UserEmails_Value] ON [UserEmails]([Value]);

-- User Phone Numbers Table
CREATE TABLE [dbo].[UserPhoneNumbers] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Value] NVARCHAR(50) NOT NULL,
    [Type] NVARCHAR(50) NULL,
    [Primary] BIT NOT NULL DEFAULT 0,
    [Display] NVARCHAR(255) NULL,
    CONSTRAINT [PK_UserPhoneNumbers] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_UserPhoneNumbers_Users] FOREIGN KEY ([UserId]) REFERENCES [Users]([Id]) ON DELETE CASCADE
);

CREATE INDEX [IX_UserPhoneNumbers_UserId] ON [UserPhoneNumbers]([UserId]);

-- User Addresses Table
CREATE TABLE [dbo].[UserAddresses] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [UserId] UNIQUEIDENTIFIER NOT NULL,
    [Type] NVARCHAR(50) NULL,
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

-- Groups Table
CREATE TABLE [dbo].[Groups] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [ExternalId] NVARCHAR(255) NULL,
    [DisplayName] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [Type] NVARCHAR(50) NULL,
    [OwnerId] UNIQUEIDENTIFIER NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [LastModified] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    [Version] INT NOT NULL DEFAULT 1,
    CONSTRAINT [PK_Groups] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_Groups_DisplayName] UNIQUE ([DisplayName]),
    CONSTRAINT [FK_Groups_Owner] FOREIGN KEY ([OwnerId]) REFERENCES [Users]([Id])
);

CREATE INDEX [IX_Groups_DisplayName] ON [Groups]([DisplayName]);
CREATE INDEX [IX_Groups_ExternalId] ON [Groups]([ExternalId]);
CREATE INDEX [IX_Groups_OwnerId] ON [Groups]([OwnerId]);

-- Group Members Table
CREATE TABLE [dbo].[GroupMembers] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [GroupId] UNIQUEIDENTIFIER NOT NULL,
    [Value] UNIQUEIDENTIFIER NOT NULL,
    [Type] NVARCHAR(50) NULL DEFAULT 'User',
    [Primary] BIT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_GroupMembers] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_GroupMembers_Groups] FOREIGN KEY ([GroupId]) REFERENCES [Groups]([Id]) ON DELETE CASCADE,
    CONSTRAINT [UQ_GroupMembers_GroupValue] UNIQUE ([GroupId], [Value])
);

CREATE INDEX [IX_GroupMembers_GroupId] ON [GroupMembers]([GroupId]);
CREATE INDEX [IX_GroupMembers_Value] ON [GroupMembers]([Value]);

-- Roles Table
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

-- User Roles Table
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

-- Custom Attributes Schema Table
CREATE TABLE [dbo].[CustomAttributeSchemas] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [SchemaUrn] NVARCHAR(500) NOT NULL,
    [Name] NVARCHAR(255) NOT NULL,
    [Description] NVARCHAR(1000) NULL,
    [Type] NVARCHAR(50) NOT NULL,
    [MultiValued] BIT NOT NULL DEFAULT 0,
    [Required] BIT NOT NULL DEFAULT 0,
    [CaseExact] BIT NOT NULL DEFAULT 0,
    [Mutability] NVARCHAR(50) NOT NULL DEFAULT 'readWrite',
    [Returned] NVARCHAR(50) NOT NULL DEFAULT 'default',
    [Uniqueness] NVARCHAR(50) NOT NULL DEFAULT 'none',
    [ResourceType] NVARCHAR(50) NOT NULL,
    [Created] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
    CONSTRAINT [PK_CustomAttributeSchemas] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_CustomAttributeSchemas_SchemaName] UNIQUE ([SchemaUrn], [Name])
);

CREATE INDEX [IX_CustomAttributeSchemas_ResourceType] ON [CustomAttributeSchemas]([ResourceType]);

-- Custom Attribute Values Table
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

-- Departments Table
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

-- API Tokens Table
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

-- Audit Log Table
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

-- System Configuration Table
CREATE TABLE [dbo].[SystemConfiguration] (
    [Id] UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    [Key] NVARCHAR(255) NOT NULL,
    [Value] NVARCHAR(MAX) NOT NULL,
    [Type] NVARCHAR(50) NOT NULL,
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
";
        }

        /// <summary>
        /// Runs any pending database migrations
        /// </summary>
        private async Task RunPendingMigrationsAsync(IDbConnection connection)
        {
            // In a real application, this would check for and apply migration scripts
            // For now, we'll just ensure the latest version is recorded
            var currentVersion = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT MAX([Version]) FROM [SchemaVersion]") ?? 0;

            if (currentVersion < 1)
            {
                // This shouldn't happen if the schema was properly initialized
                await RunInitialSchemaAsync(connection);
            }

            // Future migrations would be applied here
            // Example:
            // if (currentVersion < 2) { await RunMigration2(connection); }
            // if (currentVersion < 3) { await RunMigration3(connection); }
        }
    }
}