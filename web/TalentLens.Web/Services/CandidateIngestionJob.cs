namespace TalentLens.Web.Services;

/// <summary>
/// 🔒 MANUAL — background ingestion flow (Hangfire): load the stored CV,
/// send it to the AI service, save the extracted profile, update status.
/// </summary>
public class CandidateIngestionJob
{
    /// <summary>Ingest one uploaded CV. The id type follows your entity design.</summary>
    public Task RunAsync(Guid candidateId, CancellationToken ct = default)
    {
        throw new NotImplementedException("MANUAL: Hangfire ingestion flow");
    }
}
