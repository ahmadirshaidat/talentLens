namespace TalentLens.Infrastructure.AiService;

// C# mirrors of the AI service's Pydantic contracts (ai-service/app/models.py).

public sealed record CandidateProfileDto(
    string? FullName,
    string? Email,
    string? Phone,
    string? Location,
    double? YearsOfExperience,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> JobTitles,
    IReadOnlyList<Dictionary<string, object?>> Education);

public sealed record EvidenceDto(string Section, string Quote);

public sealed record CandidateResultDto(
    string CandidateId,
    double Score,
    string Reason,
    IReadOnlyList<EvidenceDto> Evidence);
