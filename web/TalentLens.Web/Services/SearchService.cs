using TalentLens.Infrastructure.AiService;

namespace TalentLens.Web.Services;

/// <summary>
/// 🔒 MANUAL — search orchestration: scope to the user's workspace, call the
/// AI service, join results with candidate records for display.
/// </summary>
public class SearchService
{
    /// <summary>Run a recruiter search within one workspace.</summary>
    public Task<IReadOnlyList<CandidateResultDto>> SearchAsync(
        Guid workspaceId, string query, int topK = 10, CancellationToken ct = default)
    {
        throw new NotImplementedException("MANUAL: search orchestration");
    }
}
