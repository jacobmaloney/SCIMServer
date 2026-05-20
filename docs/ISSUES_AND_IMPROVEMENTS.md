# Issues & Improvements

Honest log of what's still open. Items marked **resolved** stay in the file with the closing commit so the audit trail is visible. Everything else is a real open.

---

## Resolved (kept for history)

- ✅ **Groups API controller** — implemented in `Controllers/GroupsController.cs`. Full CRUD + member ops.
- ✅ **SCIM filter parsing** — `Core/Filtering/ScimFilterParser.cs` + `SqlFilterBuilder.cs`. `eq`, `ne`, `co`, `sw`, `ew`, `gt`, `lt`, `ge`, `le`, `pr`, with `and` / `or`.
- ✅ **Schema discovery** — `SchemasController`, `ResourceTypesController`, `ServiceProviderConfigController` all anonymous-accessible.
- ✅ **JWT brittleness** — replaced as the primary API auth mechanism. `scim_*` tokens with SHA-256 hash storage are the supported flow. JWT support remains for compatibility but isn't the default.
- ✅ **CORS overly permissive** — default policy now allows nothing cross-origin. Operators must explicitly add origins to `Cors:AllowedOrigins`.
- ✅ **HTTPS redirect noise in dev** — `UseHttpsRedirection` is now conditional on HTTPS actually being configured; production deployments still enforce.
- ✅ **Admin lockout via SCIM data ops** — portal admins moved to a separate `PortalAdmins` table in migration v10. `Delete All Users` can no longer invalidate the portal login.
- ✅ **Token mint UX** — create modal now surfaces the full `Authorization: Bearer scim_xxx` header (not just the raw value), forces a "I've copied it" confirmation, and has a built-in "Test this token now" button that round-trips against `/scim/v2/ServiceProviderConfig`.
- ✅ **Multi-tenant SCIM URL scope** — `/scim/v2/t/{slug}/...` route accepted across all controllers; URL slug is authoritative when both slug and token are present; mismatched scope returns 403.
- ✅ **API consumers get HTML 302 on auth failure** — Cookie scheme now returns clean `401 + WWW-Authenticate: Bearer` for `/scim/`, `/api/v1/`, `/sql/v1/`, `/ars/v1/`.
- ✅ **Stack-trace leak risk** — production `UseExceptionHandler` branches on path: HTML routes redirect to `/Error`, API routes return a JSON envelope with a correlation id and no details.
- ✅ **Rate limiting** — ASP.NET Core 8 `RateLimiter` with four named policies (`auth`, `scim`, `anon`, `global`). Token-bucket on the API surface partitioned by bearer token, fixed-window per-IP elsewhere.
- ✅ **Brute-force login** — `LoginThrottle` (singleton) gives sliding-window soft delay + hard lockout per (user, IP). Returns `429 Retry-After`. Events go to the audit log.
- ✅ **Security headers bundle** — `SecurityHeadersMiddleware` writes HSTS (HTTPS only), CSP (varies by route), X-Frame-Options DENY, X-Content-Type-Options nosniff, Referrer-Policy, Permissions-Policy, Cross-Origin-Opener/Resource-Policy. Strips X-Powered-By / X-AspNet-Version. Kestrel server header off.
- ✅ **Kestrel hardening** — MaxConcurrentConnections, MaxRequestBodySize 256KB, header size caps, KeepAlive/RequestHeaders timeouts, Min*DataRate against slow-loris.
- ✅ **PBKDF2 cost** — iterations bumped 100k → 600k (OWASP 2023). `Verify` accepts both costs so existing hashes still work.
- ✅ **Token sprawl** — `CreateTokenAsync` defaults a 90-day expiration when caller passes null. Fixed-value demo tokens intentionally remain non-expiring.

---

## Open — security

### Persistent brute-force throttle (MEDIUM)
**Issue:** `LoginThrottle` lives in-process. A restart resets failure counters.
**Solution:** Move the bucket state to a `LoginAttempts` table — same sliding-window semantics, durable across restarts. Cheaper than it sounds since each lookup is keyed by (UsernameLower, IpAddress).

### External secret management (MEDIUM)
**Issue:** `Jwt:SecretKey` is read from local `appsettings.json` / env. There's no integration with Key Vault, AWS Secrets Manager, or a sidecar.
**Solution:** Add `IConfigurationBuilder.AddAzureKeyVault(...)` behind an opt-in config flag (`Secrets:Provider`). Document for AWS via `SecretsManagerConfigurationProvider`.

### SBOM + CI vulnerability scan (MEDIUM)
**Issue:** No automated dependency scan. NuGet packages aren't pinned; `dotnet list package --vulnerable` is run manually.
**Solution:** Add a GitHub Action that runs `dotnet list package --vulnerable` on PRs and a weekly `dotnet outdated`. Optionally Snyk or Trivy.

### Per-token rate limit override (LOW)
**Issue:** All `scim`-policy traffic shares one bucket shape (200 cap, +100/10s). Some integrations need higher.
**Solution:** Store a token's permit rate alongside its hash; rate limiter partitions on the token id + reads its tier.

### Audit log retention + rotation (LOW)
**Issue:** `AuditLogs` grows unbounded.
**Solution:** Background job that archives rows older than N days to a partitioned table, then truncates. Configurable retention.

---

## Open — feature gaps

### SCIM `/Bulk` endpoint (MEDIUM)
**Issue:** Bulk operations endpoint not implemented.
**Solution:** Standard SCIM 2.0 Bulk envelope. Requires a multi-operation transaction in `UserRepository` / `GroupRepository`.

### Audit log viewer (MEDIUM)
**Issue:** `/audit-log` page exists but the filter UX is minimal. Hard to investigate a specific user or IP from the portal.
**Solution:** Pivots by user / IP / action; date-range presets; CSV export.

### ARS proxy execution (MEDIUM)
**Issue:** `/ars/v1/...` accepts and logs but doesn't yet call out to the Active Roles Administration Service.
**Solution:** PowerShell-via-Management-Shell handoff with a queue, retry, and dead-letter. Currently blocked on a live ARS lab.

### Group nesting (LOW)
**Issue:** Group-of-group membership isn't modeled; the SCIM `members` array currently accepts user references only.
**Solution:** Allow `value` to reference a Group; resolve recursively on read; cap depth.

### OpenAPI / Swagger (LOW)
**Issue:** No machine-readable API description.
**Solution:** `Swashbuckle.AspNetCore`. SCIM controllers need filters that strip the `[Route]`-merging trick the SCIM spec needs.

---

## Open — operational / polish

- **Page titles** — Blazor pages all render with the default `<PageTitle>` tag set per-page, but a couple still show generic strings. Sweep for consistency.
- **Favicon** — using the default ASP.NET icon. A simple SVG would carry better in screenshots.
- **CHANGELOG.md** — not yet maintained at the top level. Consider adopting Keep-A-Changelog style.
- **Test coverage** — unit tests aren't part of this repo today. Integration smoke tests via a separate test project would be the next add.
- **Google Workspace emulator** — separate project (`SCIMServer.Emulator.GoogleWorkspace`) currently has to be started by hand. Either start it from the same compose or document it more prominently.

---

## Architecture notes worth being aware of

- **Cookie auth + Blazor `<NotAuthorized>`** — returns 200 with a client-side JS redirect to `/login` (not a true 302). Works, but unusual; some HTTP scanners may flag.
- **`SmartAuth` policy scheme** — picks Cookie vs JWT per-request from the Authorization header. The `scim_*` flow is short-circuited by `ApiTokenAuthMiddleware` before either scheme runs, so token validation is in the middleware rather than an `AuthenticationHandler`. This is intentional (lets us write the failure response shape we want) but means the conventional "Add a custom AuthenticationHandler" pattern doesn't apply.
- **Migrations are forward-only** — `DatabaseMigrator` has no down scripts. Rolling back a deployment requires restoring from backup; the schema diff itself is rebuilt on the next forward run.
