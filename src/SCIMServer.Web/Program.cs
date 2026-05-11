using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SCIMServer.DataAccess;
using SCIMServer.DataAccess.Repositories;
using SCIMServer.Web.Authentication;
using SCIMServer.Web.Services;
using SCIMServer.Web.Middleware;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddControllers()
    .AddNewtonsoftJson(); // Use Newtonsoft.Json for better SCIM compatibility

// Configure database
builder.Services.Configure<DatabaseConfig>(builder.Configuration.GetSection("Database"));
builder.Services.AddSingleton<DatabaseConfig>(sp =>
{
    var config = new DatabaseConfig();
    builder.Configuration.GetSection("Database").Bind(config);
    config.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
    return config;
});

// Configure JWT
builder.Services.Configure<JwtConfig>(builder.Configuration.GetSection("Jwt"));
var jwtConfig = new JwtConfig();
builder.Configuration.GetSection("Jwt").Bind(jwtConfig);

// Set default JWT config if not provided
if (string.IsNullOrEmpty(jwtConfig.SecretKey))
{
    jwtConfig.SecretKey = "ThisIsADefaultSecretKeyForDevelopmentOnly-ChangeInProduction!";
}

// Configure authentication. Browser sessions use a cookie; API requests with a Bearer
// header forward to JWT. The SmartAuth policy scheme picks per-request based on headers,
// so existing [Authorize] controllers continue to validate JWTs unchanged.
const string SmartAuthScheme = "SmartAuth";
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = SmartAuthScheme;
    options.DefaultChallengeScheme = SmartAuthScheme;
})
.AddPolicyScheme(SmartAuthScheme, SmartAuthScheme, options =>
{
    options.ForwardDefaultSelector = context =>
    {
        var authHeader = context.Request.Headers["Authorization"].ToString();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return JwtBearerDefaults.AuthenticationScheme;
        }
        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
    options.AccessDeniedPath = "/login";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.Cookie.Name = "scim.admin";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = jwtConfig.ValidateIssuer,
        ValidateAudience = jwtConfig.ValidateAudience,
        ValidateLifetime = jwtConfig.ValidateLifetime,
        ValidateIssuerSigningKey = jwtConfig.ValidateIssuerSigningKey,
        ValidIssuer = jwtConfig.Issuer,
        ValidAudience = jwtConfig.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtConfig.SecretKey))
    };
});

// NOTE: No FallbackPolicy here. A fallback "require auth" policy also applies to /_blazor
// (the Blazor Server SignalR hub), which would break the interactive circuit on anonymous
// pages like /setup and /login. Each Blazor page uses @attribute [Authorize] explicitly;
// SCIM API controllers use [Authorize] explicitly.
builder.Services.AddAuthorization();

// Register services
builder.Services.AddTransient<DatabaseInitializer>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<GroupRepository>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ApiTokenService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<SetupService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<SCIMServer.Core.Services.UserGenerationService>();
builder.Services.AddSingleton<ApplicationLogService>();
builder.Services.AddSingleton<GenerationService>();
builder.Services.AddHostedService<StartupService>();

// Add CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("SCIMPolicy", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("Location");
    });
});

var app = builder.Build();

// Check if setup is required before initializing database
using (var scope = app.Services.CreateScope())
{
    var setupService = scope.ServiceProvider.GetRequiredService<SetupService>();
    bool setupRequired;
    try
    {
        setupRequired = await setupService.IsSetupRequiredAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Could not check setup status, assuming setup required");
        setupRequired = true;
    }

    // Only initialize database if setup is complete. If this fails (e.g. the server
    // moved or credentials changed since last run), do NOT crash startup — the user
    // needs the app running to reach /setup and fix the connection.
    if (!setupRequired)
    {
        var dbInitializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        try
        {
            await dbInitializer.InitializeAsync();
        }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "Database initialization failed at startup; routing user to /setup");
            SetupMiddleware.ClearCache();
        }
    }
}

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Check if setup is required before processing requests
app.UseSetupCheck();

app.UseCors("SCIMPolicy");

// Authenticate scim_ API tokens before the JWT handler runs
app.UseMiddleware<ApiTokenAuthMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Add SCIM connection logging middleware
app.UseMiddleware<ScimConnectionLoggingMiddleware>();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");
app.MapControllers();

// Add a simple health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
