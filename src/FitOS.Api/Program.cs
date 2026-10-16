using System.Text;
using System.Threading.RateLimiting;
using FitOS.Api;
using FitOS.Api.Auth;
using FitOS.Api.Members;
using FitOS.Api.Platform;
using FitOS.Api.Tenancy;
using FitOS.Infrastructure;
using FitOS.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddOptions<TenancyOptions>().Bind(builder.Configuration.GetSection(TenancyOptions.Section));
builder.Services.AddSingleton<TenantHostResolver>();

if (builder.Configuration.GetValue<bool>($"{TenancyOptions.Section}:{nameof(TenancyOptions.AllowTenantHeader)}")
    && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Tenancy:AllowTenantHeader is for Development only; the tenant must come from the host name.");
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapAuth();
app.MapPlatform();
app.MapMembers();

app.Run();

public partial class Program;
