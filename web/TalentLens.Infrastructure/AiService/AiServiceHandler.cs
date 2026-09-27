using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace TalentLens.Infrastructure.AiService;

/// <summary>
/// Outgoing-request handler for the AI service's HttpClient:
/// adds the service API key (<c>X-Api-Key</c>) and a correlation id (<c>X-Request-ID</c>) so a
/// web request and the AI calls it causes can be matched in both services' logs.
/// Registered in Program.cs with AddHttpMessageHandler; AiServiceClient doesn't know about it.
/// </summary>
public sealed class AiServiceHandler : DelegatingHandler
{
    public const string ApiKeyHeader = "X-Api-Key";
    public const string RequestIdHeader = "X-Request-ID";

    private readonly AiServiceOptions _options;

    public AiServiceHandler(IOptions<AiServiceOptions> options)
    {
        _options = options.Value;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_options.ApiKey))
            request.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.ApiKey);

        // Inside a web request ASP.NET Core has an Activity with a trace id; background jobs
        // (Hangfire) may not, so fall back to a fresh id.
        var requestId = Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation(RequestIdHeader, requestId);

        return base.SendAsync(request, ct);
    }
}
