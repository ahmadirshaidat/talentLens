using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.AiService;
using TalentLens.Infrastructure.Data;

namespace TalentLens.Web.Services;

/// <summary>Queries shared by the public job board and the seeker pages.</summary>
public static class JobQueries
{
    /// <summary>🧸 What job seekers may see: open jobs of companies the admin approved.</summary>
    public static IQueryable<Job> PublicJobs(this AppDbContext db) =>
        db.Jobs.Where(j => j.Status == JobStatus.Open && j.Company!.Status == CompanyStatus.Active);
}

/// <summary>
/// 🔒 MANUAL — rank a job's applicants with the AI.
///
/// 🧸 ELI5
/// A job gets 40 applications. Which to read first? We turn the job into a search sentence
/// ("Backend Developer. Python, Django. 5+ years…") and ask the AI to search ONLY among the
/// people who applied (their ids are the allow-list). Each applicant gets a score, a reason
/// and quotes from their CV, which we SAVE on the application so the page is instant next
/// time. People the AI left out (e.g. fewer years than required) get their own short
/// explanation, so the recruiter sees why they're at the bottom.
/// </summary>
public class ApplicantRankingService
{
    private const int MaxIndividualExplains = 10;

    private readonly AppDbContext _db;
    private readonly AiServiceClient _ai;

    public ApplicantRankingService(AppDbContext db, AiServiceClient ai)
    {
        _db = db;
        _ai = ai;
    }

    /// <returns>How many applications got a score.</returns>
    public async Task<int> RankAsync(Guid companyId, Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.Jobs
            .Include(j => j.Applications).ThenInclude(a => a.SeekerProfile)
            .FirstOrDefaultAsync(j => j.Id == jobId && j.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("Job not found");

        var rankable = job.Applications
            .Where(a => a.Status != ApplicationStatus.Withdrawn && a.SeekerProfile!.CvReady)
            .ToList();
        if (rankable.Count == 0) return 0;

        var query = job.ToSearchQuery();
        var ids = rankable.Select(a => AiIds.For(a.SeekerProfileId)).ToList();

        // 🧸 One search among all applicants (the AI accepts up to 100 results).
        var hits = (await _ai.SearchAsync(AiIds.SeekersWorkspace, query, Math.Min(ids.Count, 100), ids, ct))
            .ToDictionary(h => h.CandidateId);

        var explained = 0;
        foreach (var app in rankable)
        {
            if (!hits.TryGetValue(AiIds.For(app.SeekerProfileId), out var hit))
            {
                if (explained++ >= MaxIndividualExplains)
                {
                    Apply(app, new CandidateResultDto(AiIds.For(app.SeekerProfileId), 0, "", []));
                    continue;
                }
                // 🧸 Not in the main results: ask for this one person's explanation.
                hit = await _ai.ExplainAsync(AiIds.For(app.SeekerProfileId), AiIds.SeekersWorkspace, query, ct);
                hit = hit with { Score = Math.Min(hit.Score, 0.3) }; // filtered out → keep it low
            }
            Apply(app, hit);
        }

        await _db.SaveChangesAsync(ct);
        return rankable.Count;
    }

    private static void Apply(JobApplication app, CandidateResultDto hit)
    {
        app.MatchScore = Math.Round(hit.Score, 4);
        app.MatchReason = ProfileMapper.Trim(hit.Reason, 2000);
        app.MatchEvidence = hit.Evidence
            .Select(e => new EvidenceQuote { Section = e.Section, Quote = e.Quote })
            .ToList();
        app.MatchedAt = DateTime.UtcNow;
    }
}

/// <summary>A job suggested to a seeker, with the skills that matched.</summary>
public sealed record JobRecommendation(Job Job, int Score, IReadOnlyList<string> MatchedSkills);

/// <summary>
/// 🔒 MANUAL — job ↔ seeker matching for the seeker side.
///
/// 🧸 ELI5
/// • "Recommended for you" must be instant, so it's simple counting: +3 points for each
///   skill the job wants that you have, +1 for each word of your headline/titles in the job
///   title, +1 if you have enough years. Most points first.
/// • "Check my match" on a job page is the SMART version: the AI reads your whole CV
///   against the job and returns a score, a reason and quotes — slower, so only on click.
/// </summary>
public class JobMatchService
{
    private readonly AppDbContext _db;
    private readonly AiServiceClient _ai;

    public JobMatchService(AppDbContext db, AiServiceClient ai)
    {
        _db = db;
        _ai = ai;
    }

    public async Task<IReadOnlyList<JobRecommendation>> RecommendAsync(
        JobSeekerProfile profile, int take, CancellationToken ct = default)
    {
        var jobs = await _db.PublicJobs()
            .Include(j => j.Company)
            .OrderByDescending(j => j.PublishedAt)
            .Take(300)
            .ToListAsync(ct);
        return Recommend(profile, jobs, take);
    }

    /// <summary>Pure scoring, separated so it can be unit tested.</summary>
    public static IReadOnlyList<JobRecommendation> Recommend(
        JobSeekerProfile profile, IEnumerable<Job> jobs, int take)
    {
        var mySkills = profile.Skills.Select(s => s.ToLowerInvariant()).ToHashSet();
        var myWords = profile.JobTitles.Append(profile.Headline ?? "")
            .SelectMany(t => t.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length > 2)
            .ToHashSet();

        return jobs
            .Select(job =>
            {
                var matched = job.Skills.Where(s => mySkills.Contains(s.ToLowerInvariant())).ToList();
                var titleWords = job.Title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var score = matched.Count * 3 + titleWords.Count(myWords.Contains);
                if (score > 0 && job.MinYearsExperience is int min && profile.YearsOfExperience >= min) score++;
                return new JobRecommendation(job, score, matched);
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Job.PublishedAt)
            .Take(take)
            .ToList();
    }

    public Task<CandidateResultDto> CheckMatchAsync(JobSeekerProfile profile, Job job, CancellationToken ct = default) =>
        _ai.ExplainAsync(AiIds.For(profile.Id), AiIds.SeekersWorkspace, job.ToSearchQuery(), ct);
}
