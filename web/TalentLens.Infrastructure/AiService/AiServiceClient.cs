using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TalentLens.Infrastructure.AiService;

/// <summary>
/// 🔒 MANUAL — typed HttpClient for the Python AI service.
///
/// 🧸 ELI5
/// The web app (C#) and the AI service (Python) are two different programs. They talk by
/// sending letters over HTTP, written in JSON. This class is the "post office":
///   - It knows the AI service's address (BaseUrl from appsettings.json).
///   - It translates names: C# likes FullName, Python likes full_name ("snake_case").
///   - If the AI service answers with an error, it opens the error letter, reads the
///     "detail" message, and throws an AiServiceException with it — so the UI can show
///     "Unsupported file type" instead of a scary crash.
/// ASP.NET creates it for us with a ready HttpClient (see AddHttpClient in Program.cs).
/// </summary>
public class AiServiceClient
{
    // 🧸 The translation rules: snake_case names, ignore letter case when reading.
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;

    public AiServiceClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>POST /ingest (multipart: file, candidate_id, workspace_id) → extracted profile.</summary>
    public async Task<CandidateProfileDto> IngestAsync(
        Stream file, string fileName, string candidateId, string workspaceId,
        CancellationToken ct = default)
    {
        // 🧸 A "multipart form" is like an envelope with several items inside:
        // the file itself plus two small notes (candidate_id and workspace_id).
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(candidateId), "candidate_id");
        form.Add(new StringContent(workspaceId), "workspace_id");

        using var response = await _http.PostAsync("ingest", form, ct);
        return await ReadAsync<CandidateProfileDto>(response, ct);
    }

    /// <summary>POST /search → ranked candidates for the workspace.</summary>
    public async Task<IReadOnlyList<CandidateResultDto>> SearchAsync(
        string workspaceId, string query, int topK = 10,
        IReadOnlyList<string>? candidateIds = null, CancellationToken ct = default)
    {
        var body = new { workspaceId, query, topK, candidateIds };
        using var response = await _http.PostAsJsonAsync("search", body, Json, ct);
        return await ReadAsync<List<CandidateResultDto>>(response, ct);
    }

    /// <summary>POST /explain → why this candidate matches, with evidence.</summary>
    public async Task<CandidateResultDto> ExplainAsync(
        string candidateId, string workspaceId, string query, CancellationToken ct = default)
    {
        var body = new { candidateId, workspaceId, query };
        using var response = await _http.PostAsJsonAsync("explain", body, Json, ct);
        return await ReadAsync<CandidateResultDto>(response, ct);
    }

    /// <summary>DELETE /candidates/{candidateId}?workspace_id= → remove from vectors + BM25.</summary>
    public async Task DeleteCandidateAsync(
        string candidateId, string workspaceId, CancellationToken ct = default)
    {
        var url = $"candidates/{Uri.EscapeDataString(candidateId)}" +
                  $"?workspace_id={Uri.EscapeDataString(workspaceId)}";
        using var response = await _http.DeleteAsync(url, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>GET /health → true if the AI service is up.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            // 🧸 Don't wait minutes to learn the service is down: 3 seconds is enough.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync("health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new AiServiceException((int)response.StatusCode, "Empty response body");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        var raw = await response.Content.ReadAsStringAsync(ct);
        throw new AiServiceException((int)response.StatusCode, ExtractDetail(raw));
    }

    /// <summary>FastAPI errors look like {"detail": "..."} or {"detail": [{"msg": "..."}]}.</summary>
    public static string ExtractDetail(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("detail", out var detail))
            {
                return detail.ValueKind switch
                {
                    JsonValueKind.String => detail.GetString() ?? raw,
                    JsonValueKind.Array => string.Join("; ", detail.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.Object
                                     && e.TryGetProperty("msg", out var m)
                            ? m.GetString()
                            : e.ToString())),
                    _ => detail.ToString(),
                };
            }
        }
        catch (JsonException)
        {
            // not JSON — fall through
        }
        return string.IsNullOrWhiteSpace(raw) ? "(no details)" : raw;
    }
}
