using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using SCIMServer.Core.Models;

namespace SCIMServer.DataAccess.Repositories
{
    /// <summary>
    /// Repository for managing tenants (UI label: "Connected Systems").
    /// </summary>
    public class TenantRepository : BaseRepository
    {
        /// <summary>Fixed GUID of the seed Default tenant (see migration v8).</summary>
        public static readonly Guid DefaultTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

        public TenantRepository(DatabaseConfig config) : base(config) { }

        public async Task<List<Tenant>> GetAllAsync(bool includeInactive = false)
        {
            var sql = includeInactive
                ? "SELECT * FROM Tenants ORDER BY Name"
                : "SELECT * FROM Tenants WHERE IsActive = 1 ORDER BY Name";
            var rows = await QueryAsync<Tenant>(sql);
            return rows.ToList();
        }

        public Task<Tenant?> GetByIdAsync(Guid id) =>
            QuerySingleOrDefaultAsync<Tenant>(
                "SELECT * FROM Tenants WHERE Id = @Id",
                new { Id = id });

        public Task<Tenant?> GetBySlugAsync(string slug) =>
            QuerySingleOrDefaultAsync<Tenant>(
                "SELECT * FROM Tenants WHERE Slug = @Slug",
                new { Slug = slug });

        public async Task<Tenant> CreateAsync(Tenant tenant)
        {
            if (tenant.Id == Guid.Empty) tenant.Id = Guid.NewGuid();
            tenant.Created = DateTime.UtcNow;
            tenant.LastModified = tenant.Created;

            const string sql = @"
                INSERT INTO Tenants (Id, Name, Slug, Description, SystemType, Domain, IsActive, Created, LastModified)
                VALUES (@Id, @Name, @Slug, @Description, @SystemType, @Domain, @IsActive, @Created, @LastModified);";
            await ExecuteAsync(sql, tenant);
            return tenant;
        }

        public async Task<Tenant?> UpdateAsync(Tenant tenant)
        {
            tenant.LastModified = DateTime.UtcNow;

            const string sql = @"
                UPDATE Tenants
                   SET Name = @Name,
                       Slug = @Slug,
                       Description = @Description,
                       SystemType = @SystemType,
                       Domain = @Domain,
                       IsActive = @IsActive,
                       LastModified = @LastModified
                 WHERE Id = @Id;";

            var rows = await ExecuteAsync(sql, tenant);
            return rows == 0 ? null : tenant;
        }

        /// <summary>
        /// Soft-delete by default — flips IsActive=0 so existing FKs stay valid.
        /// The Default tenant cannot be deleted.
        /// </summary>
        public async Task<bool> DeactivateAsync(Guid id)
        {
            if (id == DefaultTenantId) return false;
            var rows = await ExecuteAsync(
                "UPDATE Tenants SET IsActive = 0, LastModified = SYSUTCDATETIME() WHERE Id = @Id",
                new { Id = id });
            return rows > 0;
        }

        /// <summary>
        /// Hard delete. Fails (returns false) if any Users / Groups / ApiTokens reference
        /// this tenant — FKs are enforced.
        /// </summary>
        public async Task<bool> DeleteAsync(Guid id)
        {
            if (id == DefaultTenantId) return false;
            var rows = await ExecuteAsync(
                "DELETE FROM Tenants WHERE Id = @Id",
                new { Id = id });
            return rows > 0;
        }

        /// <summary>
        /// Returns user + group + active-token counts for each tenant.
        /// Used by the Connected Systems dashboard.
        /// </summary>
        public async Task<List<TenantStats>> GetStatsAsync()
        {
            const string sql = @"
                SELECT t.Id AS TenantId,
                       t.Name AS Name,
                       t.Slug AS Slug,
                       t.SystemType AS SystemType,
                       t.Domain AS Domain,
                       t.IsActive AS IsActive,
                       (SELECT COUNT(*) FROM Users  WHERE TenantId = t.Id) AS UserCount,
                       (SELECT COUNT(*) FROM Groups WHERE TenantId = t.Id) AS GroupCount,
                       (SELECT COUNT(*) FROM ApiTokens WHERE TenantId = t.Id AND IsActive = 1) AS TokenCount
                  FROM Tenants t
                 ORDER BY t.Name";
            var rows = await QueryAsync<TenantStats>(sql);
            return rows.ToList();
        }
    }

    /// <summary>
    /// Row shape for the Connected Systems dashboard summary.
    /// </summary>
    public class TenantStats
    {
        public Guid TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public string SystemType { get; set; } = "Emulator";
        public string? Domain { get; set; }
        public bool IsActive { get; set; }
        public int UserCount { get; set; }
        public int GroupCount { get; set; }
        public int TokenCount { get; set; }
    }
}
