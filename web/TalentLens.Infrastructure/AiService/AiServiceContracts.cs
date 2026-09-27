namespace TalentLens.Infrastructure.AiService;

// C# mirrors of the AI service's Pydantic contracts (ai-service/app/models.py).
// JSON uses snake_case on the wire; AiServiceClient maps it (FullName <-> full_name).

public sealed record CandidateProfileDto(
    string? FullName,
    string? Email,
    string? Phone,
    string? Location,
    double? YearsOfExperience,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> JobTitles,
    IReadOnlyList<Dictionary<string, string?>> Education);

public sealed record EvidenceDto(string Section, string Quote);

public sealed record CandidateResultDto(
    string CandidateId,
    double Score,
    string Reason,
    IReadOnlyList<EvidenceDto> Evidence);

public sealed class AiServiceOptions
{
    public const string SectionName = "AiService";

    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>
    /// Shared secret sent as X-Api-Key (must equal SERVICE_API_KEY in the AI service).
    /// Set it with user-secrets or an environment variable — never in appsettings.json.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Ingestion runs an LLM + embeddings; the first call also loads models. Be generous.</summary>
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>The AI service answered with an error status (Detail is its "detail" message).</summary>
public sealed class AiServiceException : Exception
{
    public AiServiceException(int statusCode, string detail)
        : base($"AI service returned {statusCode}: {detail}")
    {
        StatusCode = statusCode;
        Detail = detail;
    }

    public int StatusCode { get; }
    public string Detail { get; }

    /// <summary>4xx = the request/file is bad, retrying will not help. 5xx = maybe temporary.</summary>
    public bool IsPermanent => StatusCode is >= 400 and < 500;
}
