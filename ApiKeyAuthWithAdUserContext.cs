// ApiKeyAuthWithAdUserContext.cs
// Complete single-file example for ASP.NET Core API key authentication
// where the API key resolves to an AD user GUID and becomes the request User context.
//
// Packages needed:
//   Microsoft.AspNetCore.Authentication.JwtBearer
//   Microsoft.EntityFrameworkCore
//   Microsoft.EntityFrameworkCore.SqlServer
//
// Usage:
//   1. Copy the code into your ASP.NET Core project.
//   2. Replace AppDbContext registration/connection string as needed.
//   3. Create the ApiKeys table/entity using EF migration or your existing DB process.
//   4. Generate an API key once using ApiKeyGenerator.CreateApiKeyForUserAsync(...).
//   5. Client sends: X-API-Key: <raw key shown once>

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Replace with your real connection string.
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default"));
});

builder.Services.AddScoped<IApiKeyValidator, ApiKeyValidator>();
builder.Services.AddScoped<ApiKeyGenerator>();

builder.Services
    .AddAuthentication(options =>
    {
        // This scheme decides whether to use API key auth or Okta/JWT auth.
        options.DefaultScheme = "OktaOrApiKey";
        options.DefaultChallengeScheme = "OktaOrApiKey";
    })
    .AddPolicyScheme("OktaOrApiKey", "Okta or API Key", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            if (context.Request.Headers.ContainsKey(ApiKeyConstants.HeaderName))
                return ApiKeyConstants.SchemeName;

            return JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyConstants.SchemeName,
        options => { })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        // Replace with your real Okta settings.
        options.Authority = builder.Configuration["Okta:Authority"];
        options.Audience = builder.Configuration["Okta:Audience"];
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

public static class ApiKeyConstants
{
    public const string HeaderName = "X-API-Key";
    public const string SchemeName = "ApiKey";
    public const string AuthTypeClaim = "auth_type";
    public const string ApiKeyIdClaim = "api_key_id";
    public const string AdUserGuidClaim = "ad_user_guid";
}

public sealed class ApiKeyRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    // Useful for showing/searching keys without exposing the actual key.
    // Example: sk_live_ab12
    public string KeyPrefix { get; set; } = string.Empty;

    // Store only the hash, never the raw key.
    public string KeyHash { get; set; } = string.Empty;

    // This is the AD user GUID this key represents.
    public Guid AdUserGuid { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public bool IsRevoked { get; set; }
}

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiKeyRecord>(entity =>
        {
            entity.ToTable("ApiKeys");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.KeyPrefix).HasMaxLength(30).IsRequired();
            entity.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AdUserGuid).IsRequired();
            entity.Property(x => x.CreatedAtUtc).IsRequired();
            entity.Property(x => x.IsRevoked).IsRequired();

            entity.HasIndex(x => x.KeyHash).IsUnique();
            entity.HasIndex(x => x.AdUserGuid);
        });
    }
}

public sealed record ApiKeyValidationResult(
    Guid ApiKeyId,
    Guid AdUserGuid,
    string Name);

public interface IApiKeyValidator
{
    Task<ApiKeyValidationResult?> ValidateAsync(string rawApiKey, CancellationToken cancellationToken);
}

public sealed class ApiKeyValidator : IApiKeyValidator
{
    private readonly AppDbContext _db;

    public ApiKeyValidator(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ApiKeyValidationResult?> ValidateAsync(
        string rawApiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
            return null;

        var keyHash = ApiKeySecurity.HashApiKey(rawApiKey);

        var apiKey = await _db.ApiKeys
            .SingleOrDefaultAsync(x =>
                x.KeyHash == keyHash &&
                !x.IsRevoked,
                cancellationToken);

        if (apiKey == null)
            return null;

        apiKey.LastUsedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return new ApiKeyValidationResult(
            apiKey.Id,
            apiKey.AdUserGuid,
            apiKey.Name);
    }
}

public sealed class ApiKeyAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IApiKeyValidator _apiKeyValidator;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyValidator apiKeyValidator)
        : base(options, logger, encoder)
    {
        _apiKeyValidator = apiKeyValidator;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyConstants.HeaderName, out var apiKeyHeader))
            return AuthenticateResult.NoResult();

        var rawApiKey = apiKeyHeader.ToString();

        var result = await _apiKeyValidator.ValidateAsync(
            rawApiKey,
            Context.RequestAborted);

        if (result == null)
            return AuthenticateResult.Fail("Invalid API key");

        var claims = new List<Claim>
        {
            // This makes the request behave like it is authenticated as that AD user.
            new Claim(ClaimTypes.NameIdentifier, result.AdUserGuid.ToString()),
            new Claim(ApiKeyConstants.AdUserGuidClaim, result.AdUserGuid.ToString()),
            new Claim(ApiKeyConstants.ApiKeyIdClaim, result.ApiKeyId.ToString()),
            new Claim(ApiKeyConstants.AuthTypeClaim, "api_key"),
            new Claim(ClaimTypes.Name, result.Name)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}

public static class ApiKeySecurity
{
    public static string GenerateRawApiKey(bool live = true)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");

        return live ? $"sk_live_{secret}" : $"sk_test_{secret}";
    }

    public static string HashApiKey(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }
}

public sealed class ApiKeyGenerator
{
    private readonly AppDbContext _db;

    public ApiKeyGenerator(AppDbContext db)
    {
        _db = db;
    }

    // Call this from an admin endpoint, seed script, back-office screen, or CLI tool.
    // Return the raw key to the user only once.
    public async Task<string> CreateApiKeyForUserAsync(
        Guid adUserGuid,
        string name,
        CancellationToken cancellationToken)
    {
        var rawApiKey = ApiKeySecurity.GenerateRawApiKey(live: true);

        var entity = new ApiKeyRecord
        {
            Id = Guid.NewGuid(),
            Name = name,
            KeyPrefix = rawApiKey[..Math.Min(12, rawApiKey.Length)],
            KeyHash = ApiKeySecurity.HashApiKey(rawApiKey),
            AdUserGuid = adUserGuid,
            CreatedAtUtc = DateTime.UtcNow,
            IsRevoked = false
        };

        _db.ApiKeys.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return rawApiKey;
    }
}

[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController : ControllerBase
{
    [HttpGet]
    public IActionResult GetCurrentUser()
    {
        var adUserGuid = User.FindFirstValue(ApiKeyConstants.AdUserGuidClaim)
            ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

        var authType = User.FindFirstValue(ApiKeyConstants.AuthTypeClaim) ?? "jwt_or_unknown";
        var apiKeyId = User.FindFirstValue(ApiKeyConstants.ApiKeyIdClaim);

        return Ok(new
        {
            IsAuthenticated = User.Identity?.IsAuthenticated ?? false,
            AuthenticationType = User.Identity?.AuthenticationType,
            AuthType = authType,
            AdUserGuid = adUserGuid,
            ApiKeyId = apiKeyId
        });
    }
}

[ApiController]
[Route("api/admin/api-keys")]
[Authorize]
public sealed class ApiKeysAdminController : ControllerBase
{
    private readonly ApiKeyGenerator _apiKeyGenerator;
    private readonly AppDbContext _db;

    public ApiKeysAdminController(ApiKeyGenerator apiKeyGenerator, AppDbContext db)
    {
        _apiKeyGenerator = apiKeyGenerator;
        _db = db;
    }

    [HttpPost]
    public async Task<IActionResult> CreateApiKey(
        [FromBody] CreateApiKeyRequest request,
        CancellationToken cancellationToken)
    {
        var rawApiKey = await _apiKeyGenerator.CreateApiKeyForUserAsync(
            request.AdUserGuid,
            request.Name,
            cancellationToken);

        // Show this once only. Never store or log the raw key.
        return Ok(new
        {
            ApiKey = rawApiKey,
            Message = "Copy this API key now. It will not be shown again."
        });
    }

    [HttpPost("{apiKeyId:guid}/revoke")]
    public async Task<IActionResult> RevokeApiKey(
        Guid apiKeyId,
        CancellationToken cancellationToken)
    {
        var apiKey = await _db.ApiKeys.SingleOrDefaultAsync(x => x.Id == apiKeyId, cancellationToken);

        if (apiKey == null)
            return NotFound();

        apiKey.IsRevoked = true;
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { Revoked = true });
    }
}

public sealed class CreateApiKeyRequest
{
    public Guid AdUserGuid { get; set; }
    public string Name { get; set; } = string.Empty;
}
