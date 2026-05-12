// ApiKeyFirstThenOktaComplete.cs
// ------------------------------------------------------------
// Purpose:
// API key authentication runs BEFORE Okta authentication.
// If X-API-Key is valid, it resolves the AD user GUID from DB and sets HttpContext.User.
// Then your app can use User.Identity / User.Claims normally.
//
// This file is a complete example for ASP.NET Core minimal hosting style.
// You can split these classes into separate files later.
// ------------------------------------------------------------

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ------------------------------------------------------------
// 1. Register DB
// ------------------------------------------------------------
// Replace with your actual DB provider/connection string.
// Example for SQL Server:
// builder.Services.AddDbContext<AppDbContext>(options =>
//     options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Demo uses in-memory DB so this single file can run as-is.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseInMemoryDatabase("DemoApiKeysDb"));

builder.Services.AddControllers();

// ------------------------------------------------------------
// 2. Register Okta/JWT authentication
// ------------------------------------------------------------
// Replace Authority and Audience with your Okta values.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://your-okta-domain/oauth2/default";
        options.Audience = "api://default";

        // Important:
        // Normal JwtBearer auth should not reject just because there is no bearer token.
        // It should only reject invalid bearer tokens.
        // If your custom Okta middleware immediately returns 401 when no token exists,
        // put ApiKeyMiddleware before it and make that Okta middleware skip when User is already authenticated.
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// ------------------------------------------------------------
// 3. Demo seed only
// ------------------------------------------------------------
// This creates one API key row in the DB for testing.
// In real production, generate API keys from an admin screen or command.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    if (!db.ApiKeys.Any())
    {
        const string demoRawApiKey = "sk_live_demo_12345";
        var demoAdUserGuid = Guid.Parse("11111111-1111-1111-1111-111111111111");

        db.ApiKeys.Add(new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            Name = "Demo API Key",
            KeyPrefix = "sk_live_demo",
            KeyHash = ApiKeyHasher.Hash(demoRawApiKey),
            AdUserGuid = demoAdUserGuid,
            CreatedAtUtc = DateTime.UtcNow,
            IsRevoked = false
        });

        db.SaveChanges();

        Console.WriteLine("Demo API key created:");
        Console.WriteLine(demoRawApiKey);
    }
}

// ------------------------------------------------------------
// 4. Middleware order
// ------------------------------------------------------------
// API key runs first.
// If valid, it sets HttpContext.User.
app.UseMiddleware<ApiKeyMiddleware>();

// Okta/JWT runs after.
// If there is no bearer token, it should not fail immediately.
// Authorization will decide later.
app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.Run();

// ------------------------------------------------------------
// API key middleware
// ------------------------------------------------------------
public sealed class ApiKeyMiddleware
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private readonly RequestDelegate _next;

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        // If some earlier middleware already authenticated the user, do nothing.
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // No API key? Continue.
        // Then Okta/JWT auth can try.
        // If neither authenticates, [Authorize] will reject.
        if (!context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeader))
        {
            await _next(context);
            return;
        }

        var rawApiKey = apiKeyHeader.ToString();

        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing API key value");
            return;
        }

        var keyHash = ApiKeyHasher.Hash(rawApiKey);

        var apiKey = await db.ApiKeys
            .AsNoTracking()
            .SingleOrDefaultAsync(x =>
                x.KeyHash == keyHash &&
                !x.IsRevoked);

        if (apiKey == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid API key");
            return;
        }

        // Resolve user context from API key row.
        // This AD GUID now becomes the authenticated user identity.
        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.AdUserGuid.ToString()),
            new Claim("ad_user_guid", apiKey.AdUserGuid.ToString()),
            new Claim("api_key_id", apiKey.Id.ToString()),
            new Claim("auth_type", "api_key")
        };

        var identity = new ClaimsIdentity(claims, authenticationType: "ApiKey");
        var principal = new ClaimsPrincipal(identity);

        context.User = principal;

        // Optional: update LastUsedAtUtc.
        // This uses a separate tracked entity to avoid tracking the auth lookup.
        var trackedApiKey = await db.ApiKeys.SingleAsync(x => x.Id == apiKey.Id);
        trackedApiKey.LastUsedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await _next(context);
    }
}

// ------------------------------------------------------------
// Example controller
// ------------------------------------------------------------
[ApiController]
[Route("api/test")]
public sealed class TestController : ControllerBase
{
    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        return Ok(new
        {
            IsAuthenticated = User.Identity?.IsAuthenticated,
            AuthenticationType = User.Identity?.AuthenticationType,
            NameIdentifier = User.FindFirstValue(ClaimTypes.NameIdentifier),
            AdUserGuid = User.FindFirstValue("ad_user_guid"),
            ApiKeyId = User.FindFirstValue("api_key_id"),
            AuthType = User.FindFirstValue("auth_type")
        });
    }
}

// ------------------------------------------------------------
// DB entity
// ------------------------------------------------------------
public sealed class ApiKeyEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    // Example: first few chars only, useful for display/search.
    // Do not use prefix for authentication.
    public string KeyPrefix { get; set; } = string.Empty;

    // Store hash only. Never store the raw API key.
    public string KeyHash { get; set; } = string.Empty;

    // This is your resolved AD user GUID.
    public Guid AdUserGuid { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }

    public bool IsRevoked { get; set; }
}

// ------------------------------------------------------------
// DB context
// ------------------------------------------------------------
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKeyEntity>(entity =>
        {
            entity.ToTable("ApiKeys");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(x => x.KeyPrefix)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(x => x.KeyHash)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(x => x.AdUserGuid)
                .IsRequired();

            entity.Property(x => x.CreatedAtUtc)
                .IsRequired();

            entity.Property(x => x.IsRevoked)
                .IsRequired();

            entity.HasIndex(x => x.KeyHash)
                .IsUnique();

            entity.HasIndex(x => x.AdUserGuid);
        });
    }
}

// ------------------------------------------------------------
// API key hasher
// ------------------------------------------------------------
public static class ApiKeyHasher
{
    public static string Hash(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }
}

// ------------------------------------------------------------
// Optional API key generator
// ------------------------------------------------------------
public static class ApiKeyGenerator
{
    public static string GenerateLiveKey()
    {
        return "sk_live_" + GenerateSecretPart();
    }

    public static string GenerateTestKey()
    {
        return "sk_test_" + GenerateSecretPart();
    }

    private static string GenerateSecretPart()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);

        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }
}

// ------------------------------------------------------------
// If you have custom Okta middleware instead of AddJwtBearer
// ------------------------------------------------------------
// Your custom Okta middleware should start with this:
//
// if (context.User?.Identity?.IsAuthenticated == true)
// {
//     await _next(context);
//     return;
// }
//
// That means:
// - API key already authenticated the request
// - Do not force Okta token
// - Continue to controller
// ------------------------------------------------------------
