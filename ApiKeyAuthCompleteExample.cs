// ApiKeyAuthCompleteExample.cs
// Complete minimal example for adding API key authentication alongside Okta/JWT auth in ASP.NET Core.
// Copy the relevant pieces into your project.

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

// -----------------------------------------------------------------------------
// DB registration
// -----------------------------------------------------------------------------
// Replace with your actual DB provider / connection string.
// Example appsettings.json:
// "ConnectionStrings": {
//   "Default": "Server=.;Database=MyAppDb;Trusted_Connection=True;TrustServerCertificate=True"
// }
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default"));
});

builder.Services.AddScoped<IApiKeyValidator, ApiKeyValidator>();

// -----------------------------------------------------------------------------
// Authentication registration
// -----------------------------------------------------------------------------
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "OktaOrApiKey";
        options.DefaultChallengeScheme = "OktaOrApiKey";
    })
    .AddPolicyScheme("OktaOrApiKey", "Okta JWT or API Key", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            // If request has API key header, use API key auth.
            if (context.Request.Headers.ContainsKey(ApiKeyConstants.HeaderName))
                return ApiKeyConstants.AuthenticationScheme;

            // Otherwise use normal Okta/JWT auth.
            return JwtBearerDefaults.AuthenticationScheme;
        };
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        // Replace these with your Okta values.
        options.Authority = builder.Configuration["Okta:Authority"];
        options.Audience = builder.Configuration["Okta:Audience"];
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyConstants.AuthenticationScheme,
        options => { });

builder.Services.AddAuthorization();
builder.Services.AddControllers();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// =============================================================================
// Constants
// =============================================================================
public static class ApiKeyConstants
{
    public const string HeaderName = "X-API-Key";
    public const string AuthenticationScheme = "ApiKey";
}

// =============================================================================
// Entity
// =============================================================================
public sealed class ApiKeyEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }

    // Example: first 12 chars of raw key, useful for display/search.
    public string KeyPrefix { get; set; } = string.Empty;

    // Store only hash. Never store raw API key.
    public string KeyHash { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    public bool IsRevoked { get; set; }
}

// =============================================================================
// DbContext
// =============================================================================
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
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
                .HasMaxLength(30)
                .IsRequired();

            entity.Property(x => x.KeyHash)
                .HasMaxLength(128)
                .IsRequired();

            entity.HasIndex(x => x.KeyHash)
                .IsUnique();
        });
    }
}

// =============================================================================
// API key generator and hasher
// =============================================================================
public static class ApiKeySecurity
{
    public static string GenerateApiKey(bool live = true)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);

        var secret = Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");

        return live
            ? $"sk_live_{secret}"
            : $"sk_test_{secret}";
    }

    public static string HashApiKey(string apiKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(bytes);
    }
}

// =============================================================================
// API key validator service
// =============================================================================
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

        var hash = ApiKeySecurity.HashApiKey(rawApiKey);

        var apiKey = await _db.ApiKeys
            .SingleOrDefaultAsync(x =>
                x.KeyHash == hash &&
                !x.IsRevoked,
                cancellationToken);

        if (apiKey == null)
            return null;

        apiKey.LastUsedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return new ApiKeyValidationResult(
            apiKey.Id,
            apiKey.OwnerId,
            apiKey.Name);
    }
}

public sealed record ApiKeyValidationResult(
    Guid ApiKeyId,
    Guid OwnerId,
    string Name);

// =============================================================================
// Authentication handler
// =============================================================================
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

        var validationResult = await _apiKeyValidator.ValidateAsync(
            rawApiKey,
            Context.RequestAborted);

        if (validationResult == null)
            return AuthenticateResult.Fail("Invalid API key");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, validationResult.OwnerId.ToString()),
            new(ClaimTypes.Name, validationResult.Name),
            new("api_key_id", validationResult.ApiKeyId.ToString()),
            new("auth_type", "api_key")
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}

// =============================================================================
// Controller to create API keys
// =============================================================================
// IMPORTANT:
// Protect this endpoint. Only admins/internal users should be able to create keys.
// The raw key is shown once. After that, only the hash remains in DB.
[ApiController]
[Route("api/api-keys")]
public sealed class ApiKeysController : ControllerBase
{
    private readonly AppDbContext _db;

    public ApiKeysController(AppDbContext db)
    {
        _db = db;
    }

    [Authorize]
    [HttpPost]
    public async Task<ActionResult<CreateApiKeyResponse>> CreateApiKey(
        CreateApiKeyRequest request,
        CancellationToken cancellationToken)
    {
        var rawApiKey = ApiKeySecurity.GenerateApiKey(live: true);
        var hash = ApiKeySecurity.HashApiKey(rawApiKey);

        var entity = new ApiKeyEntity
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            OwnerId = request.OwnerId,
            KeyPrefix = rawApiKey[..Math.Min(12, rawApiKey.Length)],
            KeyHash = hash,
            CreatedAtUtc = DateTime.UtcNow,
            IsRevoked = false
        };

        _db.ApiKeys.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new CreateApiKeyResponse(
            entity.Id,
            entity.Name,
            entity.KeyPrefix,
            rawApiKey));
    }

    [Authorize]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> RevokeApiKey(
        Guid id,
        CancellationToken cancellationToken)
    {
        var apiKey = await _db.ApiKeys.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (apiKey == null)
            return NotFound();

        apiKey.IsRevoked = true;
        await _db.SaveChangesAsync(cancellationToken);

        return NoContent();
    }
}

public sealed record CreateApiKeyRequest(
    string Name,
    Guid OwnerId);

public sealed record CreateApiKeyResponse(
    Guid Id,
    string Name,
    string KeyPrefix,
    string RawApiKey);

// =============================================================================
// Example protected controller
// =============================================================================
[ApiController]
[Route("api/test")]
public sealed class TestController : ControllerBase
{
    [Authorize]
    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            Message = "Authenticated using Okta token or API key",
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            AuthType = User.FindFirstValue("auth_type"),
            ApiKeyId = User.FindFirstValue("api_key_id")
        });
    }
}
