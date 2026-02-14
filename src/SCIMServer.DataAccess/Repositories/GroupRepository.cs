using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using SCIMServer.Core.Models;
using Microsoft.Data.SqlClient;

namespace SCIMServer.DataAccess.Repositories
{
    /// <summary>
    /// Repository for managing SCIM groups
    /// </summary>
    public class GroupRepository : BaseRepository
    {
        /// <summary>
        /// Initializes a new instance of the GroupRepository class
        /// </summary>
        /// <param name="databaseConfig">Database configuration</param>
        public GroupRepository(DatabaseConfig databaseConfig) : base(databaseConfig)
        {
        }

        /// <summary>
        /// Gets a group by ID
        /// </summary>
        /// <param name="id">The group ID</param>
        /// <returns>The group if found, null otherwise</returns>
        public async Task<ScimGroup?> GetByIdAsync(Guid id)
        {
            var sql = @"
                SELECT g.*, u.UserName as OwnerDisplay
                FROM Groups g
                LEFT JOIN Users u ON g.OwnerId = u.Id
                WHERE g.Id = @Id;

                SELECT gm.*, u.UserName as Display
                FROM GroupMembers gm
                LEFT JOIN Users u ON gm.Value = u.Id
                WHERE gm.GroupId = @Id;";

            using var connection = CreateConnection();
            using var multi = await connection.QueryMultipleAsync(sql, new { Id = id });

            var group = await multi.ReadSingleOrDefaultAsync<dynamic>();
            if (group == null) return null;

            var scimGroup = MapToScimGroup(group);
            var members = await multi.ReadAsync<dynamic>();

            scimGroup.Members = members.Select(m => new ScimGroupMember
            {
                Value = m.Value.ToString(),
                Display = m.Display,
                Type = m.Type
            }).ToList();

            return scimGroup;
        }

        /// <summary>
        /// Gets all groups with pagination, optional filter, and batch-loaded members
        /// </summary>
        /// <param name="options">Query options</param>
        /// <param name="filterSql">Optional WHERE clause from filter parser</param>
        /// <param name="filterParams">Optional Dapper parameters for the filter</param>
        /// <returns>List of groups and total count</returns>
        public async Task<(List<ScimGroup> Groups, int TotalCount)> GetAllAsync(
            ScimQueryOptions options,
            string? filterSql = null,
            DynamicParameters? filterParams = null)
        {
            var whereClause = "WHERE 1=1";
            if (!string.IsNullOrWhiteSpace(filterSql))
            {
                whereClause += $" AND ({filterSql})";
            }

            var sql = $@"
                SELECT COUNT(*) FROM Groups g {whereClause};
                SELECT g.*, u.UserName as OwnerDisplay
                FROM Groups g
                LEFT JOIN Users u ON g.OwnerId = u.Id
                {whereClause}
                ORDER BY g.DisplayName
                OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;";

            var parameters = new DynamicParameters();
            parameters.Add("Offset", options.StartIndex - 1);
            parameters.Add("Limit", options.Count);
            if (filterParams != null)
            {
                parameters.AddDynamicParams(filterParams);
            }

            using var connection = CreateConnection();
            using var multi = await connection.QueryMultipleAsync(sql, parameters);

            var totalCount = await multi.ReadSingleAsync<int>();
            var groups = await multi.ReadAsync<dynamic>();

            List<ScimGroup> scimGroups = groups.Select<dynamic, ScimGroup>(g => MapToScimGroup(g)).ToList();

            // Batch-load members for all groups instead of N+1
            if (scimGroups.Count > 0)
            {
                var groupIds = scimGroups.Select(g => Guid.Parse(g.Id)).ToList();
                var membersSql = @"
                    SELECT gm.GroupId, gm.Value, gm.Type, u.UserName as Display
                    FROM GroupMembers gm
                    LEFT JOIN Users u ON gm.Value = u.Id
                    WHERE gm.GroupId IN @GroupIds;";

                var allMembers = await connection.QueryAsync<dynamic>(membersSql, new { GroupIds = groupIds });
                var membersByGroup = allMembers.GroupBy(m => (Guid)m.GroupId).ToDictionary(g => g.Key, g => g.ToList());

                foreach (var group in scimGroups)
                {
                    var gid = Guid.Parse(group.Id);
                    if (membersByGroup.TryGetValue(gid, out var members))
                    {
                        group.Members = members.Select(m => new ScimGroupMember
                        {
                            Value = m.Value.ToString(),
                            Display = m.Display,
                            Type = m.Type
                        }).ToList();
                    }
                }
            }

            return (scimGroups, totalCount);
        }

        /// <summary>
        /// Creates a new group
        /// </summary>
        /// <param name="group">The group to create</param>
        /// <returns>The created group</returns>
        public async Task<ScimGroup> CreateAsync(ScimGroup group)
        {
            var id = Guid.NewGuid();
            group.Id = id.ToString();
            group.Meta.Created = DateTime.UtcNow;
            group.Meta.LastModified = DateTime.UtcNow;
            group.Meta.Version = "1";

            var sql = @"
                INSERT INTO Groups (
                    Id, DisplayName, Description, Type, OwnerId, Created, LastModified, Version
                ) VALUES (
                    @Id, @DisplayName, @Description, @Type, @OwnerId, @Created, @LastModified, @Version
                );";

            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();

            try
            {
                await connection.ExecuteAsync(sql, new
                {
                    Id = id,
                    DisplayName = group.DisplayName,
                    Description = group.Description,
                    Type = group.Type,
                    OwnerId = group.Owner != null ? Guid.Parse(group.Owner.Value) : (Guid?)null,
                    Created = group.Meta.Created,
                    LastModified = group.Meta.LastModified,
                    Version = group.Meta.Version
                }, transaction);

                // Insert members
                if (group.Members?.Any() == true)
                {
                    var membersSql = @"
                        INSERT INTO GroupMembers (GroupId, Value, Type, [Primary])
                        VALUES (@GroupId, @Value, @Type, @Primary);";

                    foreach (var member in group.Members)
                    {
                        await connection.ExecuteAsync(membersSql, new
                        {
                            GroupId = id,
                            Value = Guid.Parse(member.Value),
                            Type = member.Type ?? "User",
                            Primary = false
                        }, transaction);
                    }
                }

                transaction.Commit();
                group.Meta.ResourceType = "Group";
                group.Meta.Location = $"/scim/v2/Groups/{id}";
                return group;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Updates an existing group
        /// </summary>
        /// <param name="id">The group ID</param>
        /// <param name="group">The updated group data</param>
        /// <returns>The updated group</returns>
        public async Task<ScimGroup> UpdateAsync(Guid id, ScimGroup group)
        {
            group.Id = id.ToString();
            group.Meta.LastModified = DateTime.UtcNow;
            var newVersion = (int.Parse(group.Meta.Version ?? "1") + 1).ToString();
            group.Meta.Version = newVersion;

            var sql = @"
                UPDATE Groups SET
                    DisplayName = @DisplayName,
                    Description = @Description,
                    Type = @Type,
                    OwnerId = @OwnerId,
                    LastModified = @LastModified,
                    Version = @Version
                WHERE Id = @Id;";

            using var connection = CreateConnection();
            using var transaction = connection.BeginTransaction();

            try
            {
                await connection.ExecuteAsync(sql, new
                {
                    Id = id,
                    DisplayName = group.DisplayName,
                    Description = group.Description,
                    Type = group.Type,
                    OwnerId = group.Owner != null ? Guid.Parse(group.Owner.Value) : (Guid?)null,
                    LastModified = group.Meta.LastModified,
                    Version = newVersion
                }, transaction);

                // Update members - delete all and re-insert
                await connection.ExecuteAsync("DELETE FROM GroupMembers WHERE GroupId = @GroupId",
                    new { GroupId = id }, transaction);

                if (group.Members?.Any() == true)
                {
                    var membersSql = @"
                        INSERT INTO GroupMembers (GroupId, Value, Type, [Primary])
                        VALUES (@GroupId, @Value, @Type, @Primary);";

                    foreach (var member in group.Members)
                    {
                        await connection.ExecuteAsync(membersSql, new
                        {
                            GroupId = id,
                            Value = Guid.Parse(member.Value),
                            Type = member.Type ?? "User",
                            Primary = false
                        }, transaction);
                    }
                }

                transaction.Commit();
                return group;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Deletes a group
        /// </summary>
        /// <param name="id">The group ID</param>
        /// <returns>True if deleted, false if not found</returns>
        public async Task<bool> DeleteAsync(Guid id)
        {
            var sql = @"
                DELETE FROM GroupMembers WHERE GroupId = @Id;
                DELETE FROM Groups WHERE Id = @Id;";

            using var connection = CreateConnection();
            var affected = await connection.ExecuteAsync(sql, new { Id = id });
            return affected > 0;
        }

        /// <summary>
        /// Maps database record to ScimGroup
        /// </summary>
        private ScimGroup MapToScimGroup(dynamic record)
        {
            var group = new ScimGroup
            {
                Id = record.Id.ToString(),
                DisplayName = record.DisplayName,
                Description = record.Description,
                Type = record.Type,
                Members = new List<ScimGroupMember>(),
                Meta = new ScimMetadata
                {
                    ResourceType = "Group",
                    Created = record.Created,
                    LastModified = record.LastModified,
                    Version = record.Version,
                    Location = $"/scim/v2/Groups/{record.Id}"
                }
            };

            // Map owner if present
            if (record.OwnerId != null)
            {
                group.Owner = new ScimGroupMember
                {
                    Value = record.OwnerId.ToString(),
                    Display = record.OwnerDisplay,
                    Type = "User"
                };
            }

            return group;
        }
    }
}
