using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SCIMServer.DataAccess;

namespace SCIMServer.Web.Services
{
    /// <summary>
    /// Service to handle initial setup and configuration
    /// </summary>
    public class SetupService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<SetupService> _logger;
        private readonly DatabaseConfig _databaseConfig;
        private readonly string _setupCompleteFile = "setup.complete";

        /// <summary>
        /// Initializes a new instance of the SetupService class
        /// </summary>
        public SetupService(IConfiguration configuration, ILogger<SetupService> logger, DatabaseConfig databaseConfig)
        {
            _configuration = configuration;
            _logger = logger;
            _databaseConfig = databaseConfig;
        }

        /// <summary>
        /// Checks if the application needs initial setup
        /// </summary>
        public async Task<bool> IsSetupRequiredAsync()
        {
            // Even if setup.complete exists, verify the connection string is real (not a placeholder)
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString) || IsPlaceholderConnectionString(connectionString))
            {
                return true;
            }

            // Check if setup has already been completed
            if (File.Exists(_setupCompleteFile))
            {
                return false;
            }

            try
            {
                return !await IsDatabaseConfiguredAsync(connectionString);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not connect to database, setup required");
                return true;
            }
        }

        /// <summary>
        /// Checks if the database is properly configured
        /// </summary>
        private async Task<bool> IsDatabaseConfiguredAsync(string connectionString)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                var databaseName = builder.InitialCatalog;
                builder.InitialCatalog = "master";

                using var connection = new SqlConnection(builder.ConnectionString);
                await connection.OpenAsync();

                // Check if database exists
                var dbExistsQuery = "SELECT COUNT(*) FROM sys.databases WHERE name = @name";
                using var command = new SqlCommand(dbExistsQuery, connection);
                command.Parameters.AddWithValue("@name", databaseName);
                
                var result = await command.ExecuteScalarAsync();
                var exists = result is int count && count > 0;
                if (!exists)
                {
                    return false;
                }

                // Check if tables exist
                builder.InitialCatalog = databaseName;
                using var dbConnection = new SqlConnection(builder.ConnectionString);
                await dbConnection.OpenAsync();

                var tableExistsQuery = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'Users'";
                using var tableCommand = new SqlCommand(tableExistsQuery, dbConnection);
                
                var tableResult = await tableCommand.ExecuteScalarAsync();
                var tableExists = tableResult is int tableCount && tableCount > 0;
                return tableExists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking database configuration");
                return false;
            }
        }

        /// <summary>
        /// Checks if a connection string is a placeholder/template value
        /// </summary>
        private static bool IsPlaceholderConnectionString(string connectionString)
        {
            var upper = connectionString.ToUpperInvariant();
            return upper.Contains("YOUR_SERVER") ||
                   upper.Contains("YOUR_USER") ||
                   upper.Contains("YOUR_PASSWORD") ||
                   upper.Contains("YOUR_DATABASE") ||
                   upper.Contains("(LOCALDB)") ||
                   upper.Contains("**");
        }

        /// <summary>
        /// Validates the setup configuration
        /// </summary>
        public SetupValidationResult ValidateSetup(SetupConfiguration config)
        {
            var result = new SetupValidationResult();

            // Validate database connection
            if (string.IsNullOrWhiteSpace(config.ConnectionString))
            {
                result.AddError("ConnectionString", "Database connection string is required");
            }

            // Validate admin credentials
            if (string.IsNullOrWhiteSpace(config.AdminUsername))
            {
                result.AddError("AdminUsername", "Admin username is required");
            }

            if (string.IsNullOrWhiteSpace(config.AdminPassword))
            {
                result.AddError("AdminPassword", "Admin password is required");
            }
            else if (config.AdminPassword.Length < 8)
            {
                result.AddError("AdminPassword", "Password must be at least 8 characters long");
            }

            // Validate JWT configuration
            if (string.IsNullOrWhiteSpace(config.JwtSecretKey) || config.JwtSecretKey.Length < 32)
            {
                result.AddError("JwtSecretKey", "JWT secret key must be at least 32 characters long");
            }

            return result;
        }

        /// <summary>
        /// Applies the setup configuration
        /// </summary>
        public async Task<bool> ApplySetupAsync(SetupConfiguration config)
        {
            try
            {
                // Update the live singleton so the rest of the app uses the new connection string
                _databaseConfig.SetConnectionString(config.ConnectionString);
                _databaseConfig.AutoCreateDatabase = true;
                _databaseConfig.AutoMigrate = true;

                // Create logger factory for DatabaseInitializer
                using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
                var dbLogger = loggerFactory.CreateLogger<DatabaseInitializer>();

                var initializer = new DatabaseInitializer(_databaseConfig, dbLogger);
                await initializer.InitializeAsync();

                // Update configuration
                await UpdateConfigurationAsync(config);

                // Create admin user
                await CreateAdminUserAsync(config);

                // Mark setup as complete
                await File.WriteAllTextAsync(_setupCompleteFile, DateTime.UtcNow.ToString("O"));

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying setup configuration: {Message}", ex.Message);
                
                // Also log inner exception if present
                if (ex.InnerException != null)
                {
                    _logger.LogError(ex.InnerException, "Inner exception: {Message}", ex.InnerException.Message);
                }
                
                // Write error to console for debugging
                Console.WriteLine($"Setup error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner error: {ex.InnerException.Message}");
                }
                
                return false;
            }
        }

        /// <summary>
        /// Updates the application configuration
        /// </summary>
        private async Task UpdateConfigurationAsync(SetupConfiguration config)
        {
            // Get the current environment
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            
            // Update the environment-specific config file
            var configFileName = environment == "Development" 
                ? "appsettings.Development.json" 
                : "appsettings.Production.json";
                
            var configPath = Path.Combine(Directory.GetCurrentDirectory(), configFileName);
            
            var settings = new
            {
                ConnectionStrings = new
                {
                    DefaultConnection = config.ConnectionString
                },
                Jwt = new
                {
                    SecretKey = config.JwtSecretKey,
                    Issuer = config.JwtIssuer ?? "SCIMServer",
                    Audience = config.JwtAudience ?? "SCIMServerAPI"
                },
                Kestrel = new
                {
                    Endpoints = new
                    {
                        Http = new
                        {
                            Url = $"http://localhost:{config.ServerPort}"
                        }
                    }
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions 
            { 
                WriteIndented = true 
            });

            await File.WriteAllTextAsync(configPath, json);
        }

        /// <summary>
        /// Creates the admin user
        /// </summary>
        private Task CreateAdminUserAsync(SetupConfiguration config)
        {
            // In a real implementation, this would create the admin user in the database
            _logger.LogInformation("Creating admin user: {Username}", config.AdminUsername);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Setup configuration model
    /// </summary>
    public class SetupConfiguration
    {
        public string ConnectionString { get; set; } = string.Empty;
        public string AdminUsername { get; set; } = "admin";
        public string AdminPassword { get; set; } = string.Empty;
        public string JwtSecretKey { get; set; } = string.Empty;
        public string? JwtIssuer { get; set; }
        public string? JwtAudience { get; set; }
        public int ServerPort { get; set; } = 5000;
        public bool UseHttps { get; set; } = false;
    }

    /// <summary>
    /// Setup validation result
    /// </summary>
    public class SetupValidationResult
    {
        public bool IsValid => Errors.Count == 0;
        public Dictionary<string, List<string>> Errors { get; } = new();

        public void AddError(string field, string message)
        {
            if (!Errors.ContainsKey(field))
            {
                Errors[field] = new List<string>();
            }
            Errors[field].Add(message);
        }
    }
}