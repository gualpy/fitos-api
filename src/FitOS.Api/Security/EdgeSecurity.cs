using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;
using Microsoft.AspNetCore.Http.Features;

namespace FitOS.Api.Security;

/// <summary>Protections at the edge of the API (section "Security" and "Proxy" of the configuration).</summary>
public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>Largest request body accepted. Everything this API receives is small JSON.</summary>
    public int MaxBodyBytes { get; init; } = 1_048_576;

    /// <summary>Answer plain-HTTP requests with a redirect to HTTPS. Needs the proxy to be trusted (Proxy:*) or it loops.</summary>
    public bool RequireHttps { get; init; }

    /// <summary>Requests per minute per client IP for callers without a valid token.</summary>
    public int AnonymousRequestsPerMinute { get; init; } = 120;

    /// <summary>Requests per minute per signed-in user (a whole gym shares one IP, so users are counted apart).</summary>
    public int AuthenticatedRequestsPerMinute { get; init; } = 600;

    public static SecurityOptions From(IConfiguration configuration)
    {
        var options = configuration.GetSection(Section).Get<SecurityOptions>() ?? new SecurityOptions();
        if (options.MaxBodyBytes is < 1_024 or > 50_000_000 || options.AnonymousRequestsPerMinute < 1 || options.AuthenticatedRequestsPerMinute < 1)
        {
            throw new InvalidOperationException("Security: MaxBodyBytes must be 1 KB - 50 MB and the request limits at least 1.");
        }

        return options;
    }
}

public static class EdgeSecurity
{
    /// <summary>
    /// X-Forwarded-For/Proto/Host are believed ONLY when they come from a proxy listed in Proxy:KnownProxies or
    /// Proxy:KnownNetworks (CIDR). Otherwise anyone could fake their IP (dodging rate limits) or the host (impersonating a gym).
    /// </summary>
    public static bool ConfigureForwardedHeaders(this WebApplicationBuilder builder)
    {
        var proxies = builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [];
        var networks = builder.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [];

        var parsedProxies = proxies.Select(p => IPAddress.TryParse(p, out var ip)
            ? ip
            : throw new InvalidOperationException($"Proxy:KnownProxies has an invalid IP address: '{p}'.")).ToList();
        var parsedNetworks = networks.Select(ParseNetwork).ToList();

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1; // exactly the one proxy in front of us
            options.KnownProxies.Clear(); // the framework default trusts loopback: here only what is configured counts
            options.KnownNetworks.Clear();
            parsedProxies.ForEach(options.KnownProxies.Add);
            parsedNetworks.ForEach(options.KnownNetworks.Add);
        });

        return parsedProxies.Count + parsedNetworks.Count > 0;
    }

    private static IPNetwork ParseNetwork(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var prefix)
            || prefix < 0 || prefix > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
        {
            throw new InvalidOperationException($"Proxy:KnownNetworks has an invalid CIDR: '{cidr}'.");
        }

        return new IPNetwork(ip, prefix);
    }

    public static void AddEdgeRateLimiting(this RateLimiterOptions limiter, SecurityOptions security)
    {
        limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (context.Request.Path.StartsWithSegments("/health"))
            {
                return RateLimitPartition.GetNoLimiter("health"); // monitoring must never be throttled
            }

            // Signed-in users are counted one by one; a gym's whole staff shares one public IP.
            var user = context.User.Identity?.IsAuthenticated == true ? context.User.FindFirstValue("sub") : null;
            return user is not null
                ? RateLimitPartition.GetFixedWindowLimiter($"user:{user}", _ => Window(security.AuthenticatedRequestsPerMinute))
                : RateLimitPartition.GetFixedWindowLimiter($"ip:{context.Connection.RemoteIpAddress}", _ => Window(security.AnonymousRequestsPerMinute));
        });

        limiter.OnRejected = async (rejected, cancellationToken) =>
        {
            var response = rejected.HttpContext.Response;
            response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await response.WriteAsJsonAsync(
                new { error = new { code = "rate_limited", message = "Too many requests. Slow down and try again shortly." } },
                cancellationToken);
        };
    }

    public static FixedWindowRateLimiterOptions Window(int permits) =>
        new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };
}

/// <summary>Defensive response headers: this API serves JSON only, so everything else is switched off.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment environment)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (!environment.IsDevelopment() || !context.Request.Path.StartsWithSegments("/swagger"))
            {
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'"; // Swagger UI (development) needs its own scripts
            }

            // Responses hold personal data and tokens: nothing may be stored by browsers or intermediaries.
            if (!headers.ContainsKey("Cache-Control"))
            {
                headers.CacheControl = "no-store";
            }

            headers.Remove("Server");
            headers.Remove("X-Powered-By");
            return Task.CompletedTask;
        });

        return next(context);
    }
}

/// <summary>Refuses oversized bodies before anything reads them (also caps chunked uploads where the server supports it).</summary>
public sealed class RequestSizeLimitMiddleware(RequestDelegate next, SecurityOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentLength > options.MaxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = new { code = "payload_too_large", message = "The request body is too large." } });
            return;
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = options.MaxBodyBytes;
        }

        await next(context);
    }
}

/// <summary>Sends plain-HTTP callers to HTTPS (permanent, method-preserving). Honours X-Forwarded-Proto from trusted proxies only.</summary>
public sealed class HttpsRedirectMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.IsHttps)
        {
            return next(context);
        }

        var request = context.Request;
        context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
        context.Response.Headers.Location = $"https://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
        return Task.CompletedTask;
    }
}
