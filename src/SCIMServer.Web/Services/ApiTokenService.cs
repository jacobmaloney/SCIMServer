using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Dapper;
using Microsoft.Data.SqlClient;
using SCIMServer.DataAccess;

namespace SCIMServer.Web.Services
{
    /// <summary>
    /// Service for managing API tokens
    /// </summary>
    public class ApiTokenService
    {
        private readonly DatabaseConfig _databaseConfig;

        public ApiTokenService(DatabaseConfig databaseConfig)
        {
            _databaseConfig = databaseConfig;
        }

        /// <summary>
        /// Creates a new API token
        /// </summary>
        public async Task<string> CreateTokenAsync(string name, string? description, DateTime? expiresAt)
        {
            var tokenId = Guid.NewGuid();
            var tokenValue = GenerateSecureToken();
            var hashedToken = HashToken(tokenValue);

            var sql = @"
                INSERT INTO ApiTokens (Id, Name, Description, TokenHash, CreatedAt, ExpiresAt, IsActive)
                VALUES (@Id, @Name, @Description, @TokenHash, @CreatedAt, @ExpiresAt, @IsActive);";

            using var connection = new SqlConnection(_databaseConfig.ConnectionString);
            await connection.ExecuteAsync(sql, new
            {
                Id = tokenId,
                Name = name,
                Description = description,
                TokenHash = hashedToken,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = expiresAt,
                IsActive = true
            });

            // Return the actual token (only shown once)
            return $"scim_{tokenValue}";
        }

        /// <summary>
        /// Gets all tokens (without the actual token values)
        /// </summary>
        public async Task<List<ApiToken>> GetAllTokensAsync()
        {
            var sql = "SELECT * FROM ApiTokens ORDER BY CreatedAt DESC;";

            using var connection = new SqlConnection(_databaseConfig.ConnectionString);
            var tokens = await connection.QueryAsync<ApiToken>(sql);
            return tokens.ToList();
        }

        /// <summary>
        /// Validates a token
        /// </summary>
        public async Task<ApiToken?> ValidateTokenAsync(string token)
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("scim_"))
                return null;

            var tokenValue = token.Substring(5); // Remove "scim_" prefix
            var hashedToken = HashToken(tokenValue);

            var sql = @"
                SELECT * FROM ApiTokens 
                WHERE TokenHash = @TokenHash 
                AND IsActive = 1 
                AND (ExpiresAt IS NULL OR ExpiresAt > @Now);";

            using var connection = new SqlConnection(_databaseConfig.ConnectionString);
            var apiToken = await connection.QuerySingleOrDefaultAsync<ApiToken>(sql, new 
            { 
                TokenHash = hashedToken,
                Now = DateTime.UtcNow
            });

            if (apiToken != null)
            {
                // Update last used timestamp
                await connection.ExecuteAsync(
                    "UPDATE ApiTokens SET LastUsedAt = @Now WHERE Id = @Id",
                    new { Now = DateTime.UtcNow, Id = apiToken.Id });
            }

            return apiToken;
        }

        /// <summary>
        /// Toggles a token's active status
        /// </summary>
        public async Task ToggleTokenAsync(Guid tokenId)
        {
            var sql = @"
                UPDATE ApiTokens 
                SET IsActive = CASE WHEN IsActive = 1 THEN 0 ELSE 1 END 
                WHERE Id = @Id;";

            using var connection = new SqlConnection(_databaseConfig.ConnectionString);
            await connection.ExecuteAsync(sql, new { Id = tokenId });
        }

        /// <summary>
        /// Deletes a token
        /// </summary>
        public async Task DeleteTokenAsync(Guid tokenId)
        {
            var sql = "DELETE FROM ApiTokens WHERE Id = @Id;";

            using var connection = new SqlConnection(_databaseConfig.ConnectionString);
            await connection.ExecuteAsync(sql, new { Id = tokenId });
        }

        /// <summary>
        /// Generates a secure random token
        /// </summary>
        private string GenerateSecureToken()
        {
            var randomBytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomBytes);
            return Convert.ToBase64String(randomBytes)
                .Replace("+", "")
                .Replace("/", "")
                .Replace("=", "");
        }

        /// <summary>
        /// Hashes a token for secure storage
        /// </summary>
        private string HashToken(string token)
        {
            using var sha256 = SHA256.Create();
            var hashedBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(token));
            return Convert.ToBase64String(hashedBytes);
        }
    }

    /// <summary>
    /// API Token model
    /// </summary>
    public class ApiToken
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string TokenHash { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime? LastUsedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public bool IsActive { get; set; }
    }
}