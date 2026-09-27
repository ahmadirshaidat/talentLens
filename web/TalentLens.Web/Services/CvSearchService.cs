using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.AiService;
using TalentLens.Infrastructure.Data;

namespace TalentLens.Web.Services;

/// <summary>One CV search hit, from either the company's talent pool or the seekers database.</summary>
public sealed record CvHit(
    Guid Id,
    SearchScope Scope,
    string Name,
    string? Title,
    double? Years,
    string? Location,
    IReadOnlyList<string> Skills,
    double Score,
    string Reason,
    IReadOnlyList<EvidenceDto> Evidence);

/// <summary>
/// 🔒 MANUAL — recruiter CV search: scope, call the AI service, join with our records.
///
/// 🧸 ELI5
/// The AI finds people by id ("7f3c…, score 0.91"); the database knows names and titles.
/// The search service is the matchmaker:
///   1. Decide WHICH box to search:
///        • Talent pool → the company's own private box (CVs they uploaded).
///        • Seekers     → the shared "seekers" box, but ONLY people who said
///                        "recruiters may find me" (IsSearchable) — we pass their ids as an
///                        allow-list, so the AI can't return anyone else.
///   2. Ask the AI, get ids + scores + reasons + quotes.
///   3. Look those ids up in the database and glue each answer to its person.
///   4. Skip ids we don't know anymore; write a line in the search diary.
/// </summary>
public class CvSearchService
{
    private readonly AppDbContext _db;
    private readonly AiServiceClient _ai;

    public CvSearchService(AppDbContext db, AiServiceClient ai)
    {
        _db = db;
        _ai = ai;
    }

    public async Task<IReadOnlyList<CvHit>> SearchAsync(
        Guid companyId, string? userId, string query, SearchScope scope, int topK,
        CancellationToken ct = default)
    {
        var results = scope == SearchScope.TalentPool
            ? await SearchTalentPoolAsync(companyId, query, topK, ct)
            : await SearchSeekersAsync(query, topK, ct);

        _db.SearchLogs.Add(new SearchLog
        {
            CompanyId = companyId,
            UserId = userId,
            Query = query.Length <= 2000 ? query : query[..2000],
            Scope = scope,
            ResultCount = results.Count,
        });
        await _db.SaveChangesAsync(ct);
        return results;
    }

    private async Task<IReadOnlyList<CvHit>> SearchTalentPoolAsync(
        Guid companyId, string query, int topK, CancellationToken ct)
    {
        var hits = await _ai.SearchAsync(AiIds.For(companyId), query, topK, ct: ct);
        var ids = ParseIds(hits);
        // The query filter already limits this to the recruiter's company; the explicit
        // CompanyId check documents it.
        var people = await _db.Candidates
            .Where(c => c.CompanyId == companyId && ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        return Join(hits, people, c => new CvHit(
            c.Id, SearchScope.TalentPool, c.DisplayName, c.JobTitles.FirstOrDefault(),
            c.YearsOfExperience, c.Location, c.Skills, 0, "", []));
    }

    private async Task<IReadOnlyList<CvHit>> SearchSeekersAsync(string query, int topK, CancellationToken ct)
    {
        var allowed = await SearchableSeekerIds().ToListAsync(ct);
        if (allowed.Count == 0) return [];

        var hits = await _ai.SearchAsync(AiIds.SeekersWorkspace, query, topK,
            allowed.Select(AiIds.For).ToList(), ct);
        var ids = ParseIds(hits);
        var people = await _db.SeekerProfiles.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        return Join(hits, people, p => new CvHit(
            p.Id, SearchScope.Seekers, p.DisplayName, p.Headline ?? p.JobTitles.FirstOrDefault(),
            p.YearsOfExperience, p.Location, p.Skills, 0, "", []));
    }

    /// <summary>"Why this candidate?" — only for people this company is allowed to see.</summary>
    public async Task<CandidateResultDto?> ExplainAsync(
        Guid companyId, SearchScope scope, Guid personId, string query, CancellationToken ct = default)
    {
        if (scope == SearchScope.TalentPool)
        {
            var exists = await _db.Candidates.AnyAsync(c => c.Id == personId && c.CompanyId == companyId, ct);
            return exists
                ? await _ai.ExplainAsync(AiIds.For(personId), AiIds.For(companyId), query, ct)
                : null;
        }

        return await CanViewSeekerAsync(companyId, personId, ct)
            ? await _ai.ExplainAsync(AiIds.For(personId), AiIds.SeekersWorkspace, query, ct)
            : null;
    }

    /// <summary>A company may see a seeker who is searchable, or who applied to one of its jobs.</summary>
    public Task<bool> CanViewSeekerAsync(Guid companyId, Guid profileId, CancellationToken ct = default) =>
        _db.SeekerProfiles.AnyAsync(p => p.Id == profileId && (
            p.IsSearchable ||
            p.Applications.Any(a => a.Job!.CompanyId == companyId)), ct);

    public async Task<IReadOnlyList<SearchLog>> RecentSearchesAsync(int count, CancellationToken ct = default)
    {
        var latest = await _db.SearchLogs.OrderByDescending(s => s.CreatedAt).Take(count * 5).ToListAsync(ct);
        return latest.DistinctBy(s => (s.Scope, s.Query.Trim().ToLowerInvariant())).Take(count).ToList();
    }

    public IQueryable<Guid> SearchableSeekerIds() =>
        _db.SeekerProfiles
            .Where(p => p.IsSearchable && p.CvStatus == ProcessingStatus.Ready)
            .Select(p => p.Id);

    private static List<Guid> ParseIds(IEnumerable<CandidateResultDto> hits) =>
        hits.Select(h => Guid.TryParse(h.CandidateId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();

    /// <summary>Keep the AI's order, attach score/reason/evidence, drop unknown ids.</summary>
    private static List<CvHit> Join<T>(
        IEnumerable<CandidateResultDto> hits, Dictionary<Guid, T> people, Func<T, CvHit> toHit) =>
        hits.Select(h => Guid.TryParse(h.CandidateId, out var id) && people.TryGetValue(id, out var person)
                ? toHit(person) with { Score = h.Score, Reason = h.Reason, Evidence = h.Evidence }
                : null)
            .OfType<CvHit>()
            .ToList();
}
