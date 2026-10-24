using System.Text;
using System.Threading.RateLimiting;
using FitOS.Api;
using FitOS.Api.Auth;
using FitOS.Api.Attendance;
using FitOS.Api.Dashboard;
using FitOS.Api.Members;
using FitOS.Api.Platform;
using FitOS.Api.Products;
using FitOS.Api.Sales;
using FitOS.Api.Security;
using FitOS.Api.Measurements;
using FitOS.Api.Settings;
using FitOS.Api.Subscriptions;
using FitOS.Api.Team;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure;
using FitOS.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

var security = SecurityOptions.From(builder.Configuration);
builder.Services.AddSingleton(security);
var trustsProxy = builder.ConfigureForwardedHeaders();
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = security.MaxBodyBytes;
});
builder.Services.AddHsts(hsts =>
{
    hsts.MaxAge = TimeSpan.FromDays(365);
    hsts.IncludeSubDomains = true;
});

builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

// Browsers may call the API only from the origins listed in configuration (Cors:AllowedOrigins). Empty = no CORS at all.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Any(origin => !IsValidOrigin(origin)))
{
    throw new InvalidOperationException("Cors:AllowedOrigins must list absolute origins such as https://app.example.com (no wildcard).");
}

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins)
            .WithHeaders("Authorization", "Content-Type", TenantHostResolver.TenantHeader)
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE");
    }
}));

builder.Services.AddOptions<TenancyOptions>().Bind(builder.Configuration.GetSection(TenancyOptions.Section));
builder.Services.AddSingleton<TenantHostResolver>();

if (builder.Configuration.GetValue<bool>($"{TenancyOptions.Section}:{nameof(TenancyOptions.AllowTenantHeader)}")
    && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Tenancy:AllowTenantHeader is for Development only; use Tenancy:AllowLoginTenantHeader for a single-domain deployment.");
}

if (!builder.Environment.IsDevelopment())
{
    // Welcome and reset links point at the front end. The mail server itself may come from the panel (database),
    // so its absence in the environment is not a startup error: a missing one just fails the send with a clear reason.
    if (!Uri.TryCreate(builder.Configuration["App:PublicUrl"], UriKind.Absolute, out var publicUrl) || publicUrl.Scheme != Uri.UriSchemeHttps)
    {
        throw new InvalidOperationException("App:PublicUrl must be the https address of the front end (it goes into welcome e-mails).");
    }
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

// Token validation reads the SAME JwtOptions the issuer signs with (validated at startup).
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        bearer.MapInboundClaims = false; // keep "role" / "tenant_id" / "sub" as issued
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = JwtAccessTokenIssuer.RoleClaim,
            NameClaimType = "name",
        };
    });
builder.Services.AddAuthorization(AuthPolicies.Configure);

builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddEdgeRateLimiting(security);
    limiter.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, httpContext =>
    {
        var limit = httpContext.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.LoginRequestsPerMinute;
        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
    });
});

builder.Services.AddExceptionHandler<ApplicationExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(SwaggerSupport.Configure);
builder.Services.AddHealthChecks();

var app = builder.Build();

if (trustsProxy)
{
    app.UseForwardedHeaders(); // first: everything below must see the real client IP, scheme and host
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts(); // only ever sent over HTTPS
}

if (security.RequireHttps)
{
    app.UseMiddleware<HttpsRedirectMiddleware>();
}

app.UseExceptionHandler();
app.UseMiddleware<RequestSizeLimitMiddleware>();
app.UseCors();
app.UseAuthentication(); // before the rate limiter, so signed-in users are counted per user and not per IP
app.UseRateLimiter();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapAuth();
app.MapPlatform();
app.MapMembers();
app.MapAttendance();
app.MapDashboard();
app.MapMeasurements();
app.MapSettings();
app.MapSubscription();
app.MapTeam();
app.MapProducts();
app.MapSales();

app.Run();

public partial class Program
{
    /// <summary>An origin is exactly scheme://host[:port]: no wildcard, no path, no query ("localhost:3000" would parse as scheme "localhost").</summary>
    private static bool IsValidOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && !origin.Contains('*', StringComparison.Ordinal)
        && !origin.EndsWith('/');
}
