using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SCIMServer.DataAccess;
using SCIMServer.DataAccess.Repositories;

namespace SCIMServer.Web.Services
{
    /// <summary>
    /// Validates portal admin credentials. As of migration v10 these live in their own
    /// PortalAdmins table, decoupled from SCIM Users — so directory data ops can never
    /// invalidate the portal login.
    /// </summary>
    public class LoginService
    {
        private readonly PortalAdminRepository _admins;
        private readonly ILogger<LoginService> _logger;

        public LoginService(PortalAdminRepository admins, ILogger<LoginService> logger)
        {
            _admins = admins;
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
                var admin = await _admins.GetByUserNameAsync(lookup);
                if (admin == null)
                {
                    _logger.LogWarning("Login rejected: no portal admin with UserName='{UserName}'", lookup);
                    return null;
                }
                if (!admin.Active)
                {
                    _logger.LogWarning("Login rejected: portal admin '{UserName}' is not active", lookup);
                    return null;
                }
                if (string.IsNullOrEmpty(admin.PasswordHash) || string.IsNullOrEmpty(admin.PasswordSalt))
                {
                    _logger.LogWarning("Login rejected: portal admin '{UserName}' has no stored password hash/salt", lookup);
                    return null;
                }

                if (!PasswordHasher.Verify(password, admin.PasswordHash, admin.PasswordSalt))
                {
                    _logger.LogWarning("Login rejected: password hash mismatch for '{UserName}'", lookup);
                    return null;
                }

                await _admins.MarkLoggedInAsync(admin.Id);
                _logger.LogInformation("Portal admin login OK for '{UserName}'", admin.UserName);
                return new AdminLoginResult(admin.Id, admin.UserName, admin.DisplayName ?? admin.UserName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login validation threw exception for {Username}", lookup);
                return null;
            }
        }
    }

    public record AdminLoginResult(Guid Id, string UserName, string DisplayName);
}
