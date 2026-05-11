using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SCIMServer.DataAccess;

namespace SCIMServer.Web.Services
{
    public class LoginService
    {
        private readonly DatabaseConfig _databaseConfig;
        private readonly ILogger<LoginService> _logger;

        public LoginService(DatabaseConfig databaseConfig, ILogger<LoginService> logger)
        {
            _databaseConfig = databaseConfig;
            _logger = logger;
        }

        public async Task<AdminLoginResult?> ValidateAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning("Login rejected: empty username or password");
                return null;
            }

            var lookup = username.Trim();

            try
            {
                using var connection = new SqlConnection(_databaseConfig.ConnectionString);
                await connection.OpenAsync();

                var row = await connection.QuerySingleOrDefaultAsync<AdminRow>(@"
                    SELECT TOP 1 [Id], [UserName], [DisplayName], [PasswordHash], [PasswordSalt], [IsAdmin], [Active]
                    FROM [Users]
                    WHERE LOWER([UserName]) = LOWER(@UserName)",
                    new { UserName = lookup });

                if (row == null)
                {
                    _logger.LogWarning("Login rejected: no user found with UserName='{UserName}'", lookup);
                    return null;
                }
                if (!row.IsAdmin)
                {
                    _logger.LogWarning("Login rejected: user '{UserName}' exists but IsAdmin=0", lookup);
                    return null;
                }
                if (!row.Active)
                {
                    _logger.LogWarning("Login rejected: user '{UserName}' is not active", lookup);
                    return null;
                }
                if (string.IsNullOrEmpty(row.PasswordHash) || string.IsNullOrEmpty(row.PasswordSalt))
                {
                    _logger.LogWarning("Login rejected: user '{UserName}' has no stored password hash/salt", lookup);
                    return null;
                }

                if (!PasswordHasher.Verify(password, row.PasswordHash, row.PasswordSalt))
                {
                    _logger.LogWarning("Login rejected: password hash mismatch for '{UserName}'", lookup);
                    return null;
                }

                _logger.LogInformation("Admin login OK for '{UserName}'", row.UserName);
                return new AdminLoginResult(row.Id, row.UserName, row.DisplayName ?? row.UserName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login validation threw exception for {Username}", lookup);
                return null;
            }
        }

        private class AdminRow
        {
            public Guid Id { get; set; }
            public string UserName { get; set; } = "";
            public string? DisplayName { get; set; }
            public string? PasswordHash { get; set; }
            public string? PasswordSalt { get; set; }
            public bool IsAdmin { get; set; }
            public bool Active { get; set; }
        }
    }

    public record AdminLoginResult(Guid Id, string UserName, string DisplayName);
}
