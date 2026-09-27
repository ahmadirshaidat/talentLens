using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using TalentLens.Web.Localization;

namespace TalentLens.Web.Security;

/// <summary>Named rate-limit policies, applied with [EnableRateLimiting(...)] on actions.</summary>
public static class RateLimitPolicies
{
    /// <summary>Sign-in and sign-up POSTs: per IP, to slow down password guessing and spam accounts.</summary>
    public const string Auth = "auth";

    /// <summary>Anything that calls the AI service (search, explain, match, rank): per user.</summary>
    public const string Ai = "ai";

    /// <summary>CV uploads: per user.</summary>
    public const string Upload = "upload";
}

/// <summary>Limits from configuration (section "RateLimiting"). All windows are fixed windows.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Safety net for every request (static files excluded), per user or IP.</summary>
    public int GlobalPerMinute { get; set; } = 300;

    public int AuthPerMinute { get; set; } = 10;
    public int AiPerMinute { get; set; } = 20;
    public int UploadsPerTenMinutes { get; set; } = 30;
}

public static class RateLimitingSetup
{
    private static readonly string[] StaticPrefixes = ["/lib/", "/css/", "/js/", "/favicon"];

    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var options = config.GetSection(RateLimitingOptions.SectionName).Get<RateLimitingOptions>()
                      ?? new RateLimitingOptions();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = OnRejectedAsync;

            if (!options.Enabled)
            {
                // Keep the named policies resolvable, but never limit.
                foreach (var name in new[] { RateLimitPolicies.Auth, RateLimitPolicies.Ai, RateLimitPolicies.Upload })
                    o.AddPolicy(name, _ => RateLimitPartition.GetNoLimiter("off"));
                return;
            }

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                IsStatic(ctx.Request.Path)
                    ? RateLimitPartition.GetNoLimiter("static")
                    : Fixed($"global:{UserOrIp(ctx)}", options.GlobalPerMinute, TimeSpan.FromMinutes(1)));

            // Always by IP: before sign-in there is no user, and after a failed guess we still
            // want to count the attacker, not the account.
            o.AddPolicy(RateLimitPolicies.Auth, ctx =>
                Fixed($"auth:{Ip(ctx)}", options.AuthPerMinute, TimeSpan.FromMinutes(1)));

            o.AddPolicy(RateLimitPolicies.Ai, ctx =>
                Fixed($"ai:{UserOrIp(ctx)}", options.AiPerMinute, TimeSpan.FromMinutes(1)));

            o.AddPolicy(RateLimitPolicies.Upload, ctx =>
                Fixed($"upload:{UserOrIp(ctx)}", options.UploadsPerTenMinutes, TimeSpan.FromMinutes(10)));
        });
        return services;
    }

    private static RateLimitPartition<string> Fixed(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0, // reject immediately instead of making the browser wait
        });

    /// <summary>
    /// Signed-in users are limited per account; anonymous visitors per IP.
    /// Behind a reverse proxy, configure ForwardedHeaders so RemoteIpAddress is the real client.
    /// </summary>
    public static string UserOrIp(HttpContext ctx) =>
        ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            ? $"user:{userId}"
            : $"ip:{Ip(ctx)}";

    private static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool IsStatic(PathString path) =>
        StaticPrefixes.Any(p => path.StartsWithSegments(p.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>429 with Retry-After; plain text for fetch/AJAX calls, a small page otherwise.</summary>
    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken ct)
    {
        var http = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
            ? (int)Math.Ceiling(wait.TotalSeconds)
            : 60;
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        var t = http.RequestServices.GetService<UiText>();
        var title = t?["TooManyRequests"] ?? "Too many requests";
        var text = t?.F("TooManyRequestsText", retryAfter) ?? $"Please wait {retryAfter} seconds and try again.";

        http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("RateLimiting")
            .LogWarning("Rate limit hit on {Path} for {Partition}", http.Request.Path, UserOrIp(http));

        var wantsHtml = http.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
        if (!wantsHtml)
        {
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync(text, ct);
            return;
        }

        var dir = t?.IsArabic == true ? "rtl" : "ltr";
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.WriteAsync(
            $"""
             <!DOCTYPE html><html dir="{dir}"><head><meta charset="utf-8"><title>{title}</title>
             <link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css"></head>
             <body class="bg-light"><div class="container py-5 text-center">
             <h1 class="h4">{title}</h1><p class="text-muted">{text}</p>
             <a class="btn btn-primary" href="javascript:history.back()">←</a></div></body></html>
             """, ct);
    }
}
