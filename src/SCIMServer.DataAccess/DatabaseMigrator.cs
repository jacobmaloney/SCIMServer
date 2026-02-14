using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace SCIMServer.DataAccess
{
    /// <summary>
    /// Handles database schema migrations and updates
    /// </summary>
    public class DatabaseMigrator
    {
        private readonly string _connectionString;
        private readonly ILogger<DatabaseMigrator> _logger;

        public DatabaseMigrator(string connectionString, ILogger<DatabaseMigrator> logger)
        {
            _connectionString = connectionString;
            _logger = logger;
        }

        /// <summary>
        /// Analyzes the current database schema and returns needed migrations
        /// </summary>
        public async Task<SchemaAnalysisResult> AnalyzeSchemaAsync()
        {
            var result = new SchemaAnalysisResult();
            
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Get all tables
            var tables = await connection.QueryAsync<string>(@"
                SELECT TABLE_NAME 
                FROM INFORMATION_SCHEMA.TABLES 
                WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA = 'dbo'
                ORDER BY TABLE_NAME");
            
            result.ExistingTables = tables.ToHashSet();

            // Get all columns for each table
            var columns = await connection.QueryAsync<ColumnInfo>(@"
                SELECT 
                    TABLE_NAME as TableName,
                    COLUMN_NAME as ColumnName,
                    DATA_TYPE as DataType,
                    CHARACTER_MAXIMUM_LENGTH as MaxLength,
                    IS_NULLABLE as IsNullable,
                    COLUMN_DEFAULT as DefaultValue
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'dbo'
                ORDER BY TABLE_NAME, ORDINAL_POSITION");

            foreach (var column in columns)
            {
                if (!result.TableColumns.ContainsKey(column.TableName))
                    result.TableColumns[column.TableName] = new HashSet<string>();
                
                result.TableColumns[column.TableName].Add(column.ColumnName);
            }

            // Check schema version
            if (result.ExistingTables.Contains("SchemaVersion"))
            {
                result.CurrentVersion = await connection.QuerySingleOrDefaultAsync<int?>(
                    "SELECT MAX(Version) FROM SchemaVersion") ?? 0;
            }

            return result;
        }

        /// <summary>
        /// Gets required migrations based on schema analysis
        /// </summary>
        public List<SchemaMigration> GetRequiredMigrations(SchemaAnalysisResult analysis)
        {
            var migrations = new List<SchemaMigration>();

            // Migration 1: Fix GroupMembers table
            if (analysis.ExistingTables.Contains("GroupMembers"))
            {
                var columns = analysis.TableColumns.GetValueOrDefault("GroupMembers", new HashSet<string>());
                
                if (columns.Contains("UserId") && !columns.Contains("Value"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 2,
                        Name = "Fix GroupMembers table schema",
                        Description = "Rename UserId to Value and add Type/Primary columns",
                        SqlScript = @"
-- Fix GroupMembers table to match repository expectations
BEGIN TRANSACTION;

-- Drop constraints
DECLARE @constraint_name NVARCHAR(128);

-- Drop foreign key
SELECT @constraint_name = name FROM sys.foreign_keys 
WHERE parent_object_id = OBJECT_ID('dbo.GroupMembers') 
AND referenced_object_id = OBJECT_ID('dbo.Users');
IF @constraint_name IS NOT NULL
    EXEC('ALTER TABLE [dbo].[GroupMembers] DROP CONSTRAINT ' + @constraint_name);

-- Drop unique constraint
SELECT @constraint_name = name FROM sys.key_constraints 
WHERE parent_object_id = OBJECT_ID('dbo.GroupMembers') 
AND type = 'UQ';
IF @constraint_name IS NOT NULL
    EXEC('ALTER TABLE [dbo].[GroupMembers] DROP CONSTRAINT ' + @constraint_name);

-- Rename column
EXEC sp_rename 'dbo.GroupMembers.UserId', 'Value', 'COLUMN';

-- Add new columns
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GroupMembers') AND name = 'Type')
    ALTER TABLE [dbo].[GroupMembers] ADD [Type] NVARCHAR(50) NULL DEFAULT 'User';

IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GroupMembers') AND name = 'Primary')
    ALTER TABLE [dbo].[GroupMembers] ADD [Primary] BIT NULL DEFAULT 0;

-- Drop unused column
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GroupMembers') AND name = 'Added')
BEGIN
    -- Drop default constraint first
    SELECT @constraint_name = name FROM sys.default_constraints 
    WHERE parent_object_id = OBJECT_ID('dbo.GroupMembers') 
    AND parent_column_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.GroupMembers') AND name = 'Added');
    IF @constraint_name IS NOT NULL
        EXEC('ALTER TABLE [dbo].[GroupMembers] DROP CONSTRAINT ' + @constraint_name);
    
    ALTER TABLE [dbo].[GroupMembers] DROP COLUMN [Added];
END

-- Recreate constraints
ALTER TABLE [dbo].[GroupMembers] ADD CONSTRAINT [UQ_GroupMembers_GroupValue] UNIQUE ([GroupId], [Value]);

COMMIT TRANSACTION;"
                    });
                }
            }

            // Migration 2: Add Owner to Groups table
            if (analysis.ExistingTables.Contains("Groups"))
            {
                var columns = analysis.TableColumns.GetValueOrDefault("Groups", new HashSet<string>());
                
                if (!columns.Contains("OwnerId"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 3,
                        Name = "Add Owner to Groups",
                        Description = "Add OwnerId column to Groups table",
                        SqlScript = @"
-- Add OwnerId column to Groups table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Groups') AND name = 'OwnerId')
BEGIN
    ALTER TABLE [dbo].[Groups] ADD [OwnerId] UNIQUEIDENTIFIER NULL;
    ALTER TABLE [dbo].[Groups] ADD CONSTRAINT [FK_Groups_Owner] FOREIGN KEY ([OwnerId]) REFERENCES [Users]([Id]);
    CREATE INDEX [IX_Groups_OwnerId] ON [Groups]([OwnerId]);
END"
                    });
                }

                if (!columns.Contains("Type"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 4,
                        Name = "Add Type to Groups",
                        Description = "Add Type column to Groups table",
                        SqlScript = @"
-- Add Type column to Groups table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Groups') AND name = 'Type')
BEGIN
    ALTER TABLE [dbo].[Groups] ADD [Type] NVARCHAR(50) NULL;
END"
                    });
                }
            }

            // Migration 3: Add Manager to Users table
            if (analysis.ExistingTables.Contains("Users"))
            {
                var columns = analysis.TableColumns.GetValueOrDefault("Users", new HashSet<string>());
                
                if (!columns.Contains("ManagerId"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 5,
                        Name = "Add Manager to Users",
                        Description = "Add ManagerId column to Users table",
                        SqlScript = @"
-- Add ManagerId column to Users table
IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Users') AND name = 'ManagerId')
BEGIN
    ALTER TABLE [dbo].[Users] ADD [ManagerId] UNIQUEIDENTIFIER NULL;
    ALTER TABLE [dbo].[Users] ADD CONSTRAINT [FK_Users_Manager] FOREIGN KEY ([ManagerId]) REFERENCES [Users]([Id]);
    CREATE INDEX [IX_Users_ManagerId] ON [Users]([ManagerId]);
END"
                    });
                }
            }

            // Migration 4: Fix ApiTokens table schema
            if (analysis.ExistingTables.Contains("ApiTokens"))
            {
                var columns = analysis.TableColumns.GetValueOrDefault("ApiTokens", new HashSet<string>());
                
                if (!columns.Contains("CreatedAt") && columns.Contains("Created"))
                {
                    migrations.Add(new SchemaMigration
                    {
                        Version = 6,
                        Name = "Fix ApiTokens column names",
                        Description = "Rename columns in ApiTokens table to match repository",
                        SqlScript = @"
-- Fix ApiTokens table column names
BEGIN TRANSACTION;

-- Rename columns
IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ApiTokens') AND name = 'Created')
    EXEC sp_rename 'dbo.ApiTokens.Created', 'CreatedAt', 'COLUMN';

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ApiTokens') AND name = 'LastUsed')
    EXEC sp_rename 'dbo.ApiTokens.LastUsed', 'LastUsedAt', 'COLUMN';

IF EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('dbo.ApiTokens') AND name = 'Expires')
    EXEC sp_rename 'dbo.ApiTokens.Expires', 'ExpiresAt', 'COLUMN';

COMMIT TRANSACTION;"
                    });
                }
            }

            // Filter migrations that haven't been applied yet
            return migrations.Where(m => m.Version > analysis.CurrentVersion).OrderBy(m => m.Version).ToList();
        }

        /// <summary>
        /// Applies migrations to the database
        /// </summary>
        public async Task<bool> ApplyMigrationsAsync(List<SchemaMigration> migrations)
        {
            if (!migrations.Any())
            {
                _logger.LogInformation("No migrations to apply");
                return true;
            }

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            foreach (var migration in migrations)
            {
                _logger.LogInformation($"Applying migration {migration.Version}: {migration.Name}");
                
                using var transaction = connection.BeginTransaction();
                try
                {
                    // Split by GO statements and execute each batch
                    var batches = migration.SqlScript.Split(new[] { "\nGO\n", "\nGO\r\n", "\r\nGO\r\n", "\r\nGO\n" }, 
                        StringSplitOptions.RemoveEmptyEntries);

                    foreach (var batch in batches)
                    {
                        if (!string.IsNullOrWhiteSpace(batch))
                        {
                            await connection.ExecuteAsync(batch, transaction: transaction);
                        }
                    }

                    // Record the migration
                    await connection.ExecuteAsync(@"
                        INSERT INTO SchemaVersion (Version, AppliedOn, Description)
                        VALUES (@Version, @AppliedOn, @Description)",
                        new { migration.Version, AppliedOn = DateTime.UtcNow, migration.Description },
                        transaction);

                    transaction.Commit();
                    _logger.LogInformation($"Migration {migration.Version} applied successfully");
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    _logger.LogError(ex, $"Error applying migration {migration.Version}: {migration.Name}");
                    throw new Exception($"Migration {migration.Version} failed: {ex.Message}", ex);
                }
            }

            return true;
        }

        /// <summary>
        /// Ensures SchemaVersion table exists
        /// </summary>
        public async Task EnsureSchemaVersionTableAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            var exists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'SchemaVersion'") > 0;

            if (!exists)
            {
                await connection.ExecuteAsync(@"
                    CREATE TABLE [dbo].[SchemaVersion] (
                        [Version] INT NOT NULL,
                        [AppliedOn] DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        [Description] NVARCHAR(500) NULL,
                        CONSTRAINT [PK_SchemaVersion] PRIMARY KEY CLUSTERED ([Version])
                    )");

                // Mark initial schema as version 1
                await connection.ExecuteAsync(
                    "INSERT INTO SchemaVersion (Version, Description) VALUES (1, 'Initial schema creation')");
            }
        }
    }

    public class SchemaAnalysisResult
    {
        public HashSet<string> ExistingTables { get; set; } = new();
        public Dictionary<string, HashSet<string>> TableColumns { get; set; } = new();
        public int CurrentVersion { get; set; } = 0;
    }

    public class ColumnInfo
    {
        public string TableName { get; set; } = "";
        public string ColumnName { get; set; } = "";
        public string DataType { get; set; } = "";
        public int? MaxLength { get; set; }
        public string IsNullable { get; set; } = "";
        public string? DefaultValue { get; set; }
    }

    public class SchemaMigration
    {
        public int Version { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string SqlScript { get; set; } = "";
    }
}