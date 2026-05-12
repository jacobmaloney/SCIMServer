using System;
using Microsoft.AspNetCore.Http;
using SCIMServer.Core.Models;

namespace SCIMServer.Web.Services
{
    /// <summary>
    /// Per-request resolution of the active tenant (UI label: "Connected System").
    /// Populated by <see cref="Middleware.ApiTokenAuthMiddleware"/> from the validated
    /// API token. Controllers and repositories read tenant identity here instead of
    /// poking <see cref="HttpContext"/> directly.
    /// </summary>
    public interface ITenantContext
    {
        /// <summary>
        /// True when the caller is authenticated and tenant scope has been resolved.
        /// </summary>
        bool IsResolved { get; }

        /// <summary>
        /// Tenant the caller is scoped to. NULL when Scope == Admin (caller may operate
        /// across all tenants) or when no token has been validated yet.
        /// </summary>
        Guid? TenantId { get; }

        /// <summary>
        /// Scope of the active token. Defaults to <see cref="ApiTokenScope.Tenant"/>.
        /// </summary>
        ApiTokenScope Scope { get; }

        /// <summary>
        /// True if the active token has admin (cross-tenant) authority.
        /// </summary>
        bool IsAdmin { get; }

        /// <summary>
        /// True if the active token is the ARS proxy scope.
        /// </summary>
        bool IsArsProxy { get; }

        /// <summary>
        /// Convenience: tenant filter to apply to repository queries.
        /// Returns the active TenantId, or throws if the caller is unscoped (Admin)
        /// — callers that may operate across tenants should check <see cref="IsAdmin"/>
        /// before calling this.
        /// </summary>
        Guid RequireTenantId();
    }

    /// <summary>
    /// Default implementation backed by <see cref="HttpContext.Items"/>.
    /// </summary>
    public class TenantContext : ITenantContext
    {
        public const string TenantIdItemKey = "TenantId";
        public const string ScopeItemKey = "TokenScope";

        private readonly IHttpContextAccessor _accessor;

        public TenantContext(IHttpContextAccessor accessor)
        {
            _accessor = accessor;
        }

        private HttpContext? Ctx => _accessor.HttpContext;

        public bool IsResolved => Ctx?.Items.ContainsKey(ScopeItemKey) == true;

        public Guid? TenantId
        {
            get
            {
                var v = Ctx?.Items[TenantIdItemKey];
                return v is Guid g ? g : (Guid?)null;
            }
        }

        public ApiTokenScope Scope
        {
            get
            {
                var v = Ctx?.Items[ScopeItemKey] as string;
                return Enum.TryParse<ApiTokenScope>(v, ignoreCase: true, out var parsed)
                    ? parsed
                    : ApiTokenScope.Tenant;
            }
        }

        public bool IsAdmin => Scope == ApiTokenScope.Admin;
        public bool IsArsProxy => Scope == ApiTokenScope.ArsProxy;

        public Guid RequireTenantId()
        {
            if (TenantId is { } id) return id;
            throw new InvalidOperationException(
                "No tenant in scope. The caller has Admin scope; you must accept an explicit tenant from the request " +
                "or refuse the operation.");
        }
    }
}
