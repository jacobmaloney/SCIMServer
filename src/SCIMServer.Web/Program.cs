using System.Text;
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

// Configure authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
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

builder.Services.AddAuthorization();

// Register services
builder.Services.AddTransient<DatabaseInitializer>();
builder.Services.AddScoped<UserRepository>();
builder.Services.AddScoped<GroupRepository>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ApiTokenService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<SetupService>();
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

    // Only initialize database if setup is complete
    if (!setupRequired)
    {
        var dbInitializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await dbInitializer.InitializeAsync();
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
