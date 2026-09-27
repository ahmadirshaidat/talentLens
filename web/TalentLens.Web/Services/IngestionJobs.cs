using Hangfire;
using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.AiService;
using TalentLens.Infrastructure.Data;
using TalentLens.Infrastructure.Storage;

namespace TalentLens.Web.Services;

/// <summary>
/// 🔒 MANUAL — background ingestion of a CV a recruiter uploaded to the talent pool.
///
/// 🧸 ELI5
/// Reading a CV with AI takes a while, so the upload page just says "got it!" and puts a
/// ticket in a queue. Hangfire is the worker who takes tickets one by one:
///   1. Find the candidate card.                        Status → Processing
///   2. Mail the saved file to the AI service (into the company's private box).
///   3. Copy the profile the AI found onto the card.     Status → Ready ✅
/// If the FILE is bad (AI says 4xx) → Failed, no retry (same file = same answer).
/// If the AI is down (5xx / network) → Failed for now and Hangfire retries later.
/// Background jobs have no signed-in user, so we use IgnoreQueryFilters() and look the
/// card up by its exact Id.
/// </summary>
public class CandidateIngestionJob
{
    private readonly AppDbContext _db;
    private readonly AiServiceClient _ai;
    private readonly IFileStorage _storage;
    private readonly ILogger<CandidateIngestionJob> _logger;

    public CandidateIngestionJob(AppDbContext db, AiServiceClient ai, IFileStorage storage,
        ILogger<CandidateIngestionJob> logger)
    {
        _db = db;
        _ai = ai;
        _storage = storage;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task RunAsync(Guid candidateId, CancellationToken ct = default)
    {
        var candidate = await _db.Candidates.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == candidateId, ct);
        if (candidate is null) return; // deleted before we got to it

        candidate.Status = ProcessingStatus.Processing;
        candidate.Error = null;
        await _db.SaveChangesAsync(ct);

        try
        {
            CandidateProfileDto profile;
            await using (var file = _storage.OpenRead(candidate.CompanyId, candidate.StoredFileName))
            {
                profile = await _ai.IngestAsync(file, candidate.OriginalFileName,
                    AiIds.For(candidate.Id), AiIds.For(candidate.CompanyId), ct);
            }
            ProfileMapper.Overwrite(candidate, profile);
            candidate.Status = ProcessingStatus.Ready;
            candidate.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (AiServiceException ex) when (ex.IsPermanent)
        {
            await MarkFailedAsync(candidate, ex.Detail);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 🧸 Deleted while the AI was reading it: remove the AI's copy too.
            await _ai.DeleteCandidateAsync(AiIds.For(candidateId), AiIds.For(candidate.CompanyId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MarkFailedAsync(candidate, ex is AiServiceException ai ? ai.Detail : ex.Message);
            throw; // 🧸 let Hangfire retry
        }
    }

    private async Task MarkFailedAsync(Candidate candidate, string error)
    {
        _logger.LogWarning("Ingestion failed for candidate {Id}: {Error}", candidate.Id, error);
        candidate.Status = ProcessingStatus.Failed;
        candidate.Error = ProfileMapper.Trim(error, 2000);
        try { await _db.SaveChangesAsync(CancellationToken.None); }
        catch (DbUpdateConcurrencyException) { /* deleted meanwhile */ }
    }
}

/// <summary>
/// 🔒 MANUAL — background ingestion of a job seeker's own CV.
///
/// 🧸 ELI5: same trip as above, but the CV goes into the shared "seekers" box, and we only
/// FILL EMPTY boxes on the seeker's profile — if they typed their headline or phone by
/// hand, the AI must not overwrite it. Skills and languages are merged (union).
/// </summary>
public class SeekerCvIngestionJob
{
    private readonly AppDbContext _db;
    private readonly AiServiceClient _ai;
    private readonly IFileStorage _storage;
    private readonly ILogger<SeekerCvIngestionJob> _logger;

    public SeekerCvIngestionJob(AppDbContext db, AiServiceClient ai, IFileStorage storage,
        ILogger<SeekerCvIngestionJob> logger)
    {
        _db = db;
        _ai = ai;
        _storage = storage;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 30, 120, 600 })]
    public async Task RunAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await _db.SeekerProfiles.FirstOrDefaultAsync(p => p.Id == profileId, ct);
        if (profile?.CvStoredFileName is null) return;

        profile.CvStatus = ProcessingStatus.Processing;
        profile.CvError = null;
        await _db.SaveChangesAsync(ct);

        try
        {
            CandidateProfileDto extracted;
            await using (var file = _storage.OpenRead(profile.Id, profile.CvStoredFileName))
            {
                extracted = await _ai.IngestAsync(file, profile.CvOriginalFileName ?? "cv.pdf",
                    AiIds.For(profile.Id), AiIds.SeekersWorkspace, ct);
            }
            ProfileMapper.FillMissing(profile, extracted);
            profile.CvStatus = ProcessingStatus.Ready;
            profile.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (AiServiceException ex) when (ex.IsPermanent)
        {
            await MarkFailedAsync(profile, ex.Detail);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MarkFailedAsync(profile, ex is AiServiceException ai ? ai.Detail : ex.Message);
            throw;
        }
    }

    private async Task MarkFailedAsync(JobSeekerProfile profile, string error)
    {
        _logger.LogWarning("CV ingestion failed for seeker {Id}: {Error}", profile.Id, error);
        profile.CvStatus = ProcessingStatus.Failed;
        profile.CvError = ProfileMapper.Trim(error, 2000);
        await _db.SaveChangesAsync(CancellationToken.None);
    }
}

/// <summary>Removes a CV from the AI service's indexes (retried by Hangfire if the AI is down).</summary>
public class AiCleanupJob
{
    private readonly AiServiceClient _ai;

    public AiCleanupJob(AiServiceClient ai)
    {
        _ai = ai;
    }

    [AutomaticRetry(Attempts = 5)]
    public Task RunAsync(string candidateId, string workspaceId, CancellationToken ct = default) =>
        _ai.DeleteCandidateAsync(candidateId, workspaceId, ct);
}

/// <summary>Copies an AI-extracted profile onto our entities.</summary>
public static class ProfileMapper
{
    /// <summary>Talent pool: the AI's reading is the only source → overwrite everything.</summary>
    public static void Overwrite(IExtractedProfile target, CandidateProfileDto source)
    {
        target.FullName = Trim(source.FullName, 200);
        target.Email = Trim(source.Email, 256);
        target.Phone = Trim(source.Phone, 50);
        target.Location = Trim(source.Location, 200);
        target.YearsOfExperience = source.YearsOfExperience;
        target.Skills = source.Skills.ToList();
        target.Languages = source.Languages.ToList();
        target.JobTitles = source.JobTitles.ToList();
        target.Education = MapEducation(source);
    }

    /// <summary>Seeker profile: keep what the person typed, fill only the gaps, merge lists.</summary>
    public static void FillMissing(JobSeekerProfile target, CandidateProfileDto source)
    {
        target.FullName ??= Trim(source.FullName, 200);
        target.Email ??= Trim(source.Email, 256);
        target.Phone ??= Trim(source.Phone, 50);
        target.Location ??= Trim(source.Location, 200);
        target.YearsOfExperience ??= source.YearsOfExperience;
        target.Headline ??= Trim(source.JobTitles.FirstOrDefault(), 200);
        target.Skills = Merge(target.Skills, source.Skills);
        target.Languages = Merge(target.Languages, source.Languages);
        target.JobTitles = Merge(target.JobTitles, source.JobTitles);
        if (target.Education.Count == 0) target.Education = MapEducation(source);
    }

    public static List<string> Merge(IEnumerable<string> mine, IEnumerable<string> theirs) =>
        mine.Concat(theirs)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .DistinctBy(s => s.ToLowerInvariant())
            .ToList();

    private static List<Education> MapEducation(CandidateProfileDto source) =>
        source.Education
            .Select(e => new Education
            {
                Degree = e.GetValueOrDefault("degree"),
                Field = e.GetValueOrDefault("field"),
                Institution = e.GetValueOrDefault("institution"),
                Year = e.GetValueOrDefault("year"),
            })
            .ToList();

    public static string? Trim(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
