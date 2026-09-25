namespace TalentLens.Infrastructure.AiService;

/// <summary>
/// 🔒 MANUAL — typed HttpClient for the Python AI service (incl. streaming).
/// </summary>
public class AiServiceClient
{
    private readonly HttpClient _http;

    public AiServiceClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>POST /ingest (multipart: file, candidate_id, workspace_id) → extracted profile.</summary>
    public Task<CandidateProfileDto> IngestAsync(
        Stream file, string fileName, string candidateId, string workspaceId,
        CancellationToken ct = default)
    {
        throw new NotImplementedException("MANUAL: POST /ingest");
    }

    /// <summary>POST /search → ranked candidates for the workspace.</summary>
    public Task<IReadOnlyList<CandidateResultDto>> SearchAsync(
        string workspaceId, string query, int topK = 10,
        IReadOnlyList<string>? candidateIds = null, CancellationToken ct = default)
    {
        throw new NotImplementedException("MANUAL: POST /search");
    }

    /// <summary>POST /explain → why this candidate matches, with evidence.</summary>
    public Task<CandidateResultDto> ExplainAsync(
        string candidateId, string workspaceId, string query, CancellationToken ct = default)
    {
        throw new NotImplementedException("MANUAL: POST /explain");
    }

    /// <summary>DELETE /candidates/{candidateId}?workspace_id= → remove from vectors + BM25.</summary>
    public Task DeleteCandidateAsync(
        string candidateId, string workspaceId, CancellationToken ct = default)
    {
        throw new NotImplementedException("MANUAL: DELETE /candidates/{id}");
    }

    /// <summary>GET /health → true if the AI service is up.</summary>
    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException("MANUAL: GET /health");
    }
}
