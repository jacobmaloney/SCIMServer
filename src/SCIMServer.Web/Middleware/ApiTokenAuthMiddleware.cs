using System.Security.Claims;
using SCIMServer.Web.Services;
using SCIMServer.Core.Models;

namespace SCIMServer.Web.Middleware;

/// <summary>
/// Middleware that authenticates requests using scim_ API tokens.
/// Runs before UseAuthentication to set HttpContext.User for requests
/// bearing scim_ prefixed tokens, bypassing the JWT handler.
/// </summary>
public class ApiTokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiTokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var authHeader = context.Request.Headers["Authorization"].ToString();

        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith("Bearer scim_", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader.Substring("Bearer ".Length).Trim();

            using var scope = context.RequestServices.CreateScope();
            var tokenService = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
            var apiToken = await tokenService.ValidateTokenAsync(token);

            if (apiToken == null)
            {
                // The token announced itself as a scim_ API token but did not validate.
                // Don't fall through to the JWT handler — it's not a JWT, and silently
                // continuing would let downstream [Authorize] surface a confusing error.
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers["WWW-Authenticate"] = "Bearer error=\"invalid_token\"";
                return;
            }

            // Surface tenant identity for downstream services (TenantContext + repos).
            context.Items[TenantContext.TenantIdItemKey] = apiToken.TenantId;
            context.Items[TenantContext.ScopeItemKey] = apiToken.Scope ?? "Tenant";

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, apiToken.Id.ToString()),
                new(ClaimTypes.Name, apiToken.Name),
                new("TokenType", "ApiToken"),
                new("TokenScope", apiToken.Scope ?? "Tenant"),
                new("scope", "scim:read"),
                new("scope", "scim:write")
            };
            if (apiToken.TenantId is { } tid)
            {
                claims.Add(new Claim("TenantId", tid.ToString()));
            }

            var identity = new ClaimsIdentity(claims, "ApiToken");
            context.User = new ClaimsPrincipal(identity);
        }

        await _next(context);
    }
}
