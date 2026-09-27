using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.AiService;
using TalentLens.Infrastructure.Data;
using TalentLens.Web.Localization;
using TalentLens.Web.Models;
using TalentLens.Web.Security;
using TalentLens.Web.Services;

namespace TalentLens.Web.Controllers;

/// <summary>The public job board: search, details, and (for job seekers) save / apply / match.</summary>
public class JobsController : Controller
{
    private const int PageSize = 15;

    private readonly AppDbContext _db;
    private readonly JobMatchService _match;
    private readonly UiText _t;
    private readonly ILogger<JobsController> _logger;

    public JobsController(AppDbContext db, JobMatchService match, UiText t, ILogger<JobsController> logger)
    {
        _db = db;
        _match = match;
        _t = t;
        _logger = logger;
    }

    public async Task<IActionResult> Index(
        string? q, string? location, EmploymentType? type, WorkplaceType? workplace, int page = 1,
        CancellationToken ct = default)
    {
        var query = _db.PublicJobs();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(j =>
                j.Title.Contains(term) || j.Description.Contains(term) ||
                j.Company!.Name.Contains(term) || j.Skills.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(location))
        {
            var loc = location.Trim();
            query = query.Where(j => j.Location != null && j.Location.Contains(loc));
        }
        if (type is not null) query = query.Where(j => j.EmploymentType == type);
        if (workplace is not null) query = query.Where(j => j.WorkplaceType == workplace);

        var total = await query.CountAsync(ct);
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);
        var jobs = await query.Include(j => j.Company)
            .OrderByDescending(j => j.PublishedAt)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .ToListAsync(ct);

        var (saved, applied) = await MyJobMarksAsync(ct);
        return View(new JobSearchViewModel
        {
            Q = q, Location = location, Type = type, Workplace = workplace, Jobs = jobs,
            Page = page, TotalPages = totalPages, TotalCount = total, SavedIds = saved, AppliedIds = applied,
        });
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var job = await _db.PublicJobs().Include(j => j.Company).FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return NotFound();

        var profile = await MyProfileAsync(ct);
        return View(new JobDetailsViewModel
        {
            Job = job,
            Profile = profile,
            IsSeeker = User.IsInRole(AppRoles.JobSeeker),
            MyApplication = profile is null ? null : await _db.Applications
                .FirstOrDefaultAsync(a => a.JobId == id && a.SeekerProfileId == profile.Id, ct),
            IsSaved = profile is not null && await _db.SavedJobs
                .AnyAsync(s => s.JobId == id && s.SeekerProfileId == profile.Id, ct),
            MoreFromCompany = await _db.PublicJobs()
                .Where(j => j.CompanyId == job.CompanyId && j.Id != id)
                .OrderByDescending(j => j.PublishedAt).Take(4).ToListAsync(ct),
        });
    }

    [HttpPost, Authorize(Roles = AppRoles.JobSeeker)]
    public async Task<IActionResult> Apply(Guid id, string? coverLetter, CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        var job = await _db.PublicJobs().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (profile is null || job is null) return NotFound();

        if (!profile.HasCv)
        {
            TempData["Error"] = _t["ApplyNeedsCv"];
            return RedirectToAction("Index", "Profile", new { area = "Seeker" });
        }
        if (await _db.Applications.AnyAsync(a => a.JobId == id && a.SeekerProfileId == profile.Id, ct))
        {
            TempData["Error"] = _t["AlreadyApplied"];
            return RedirectToAction(nameof(Details), new { id });
        }

        _db.Applications.Add(new JobApplication
        {
            JobId = id,
            SeekerProfileId = profile.Id,
            CoverLetter = string.IsNullOrWhiteSpace(coverLetter) ? null : coverLetter.Trim()[..Math.Min(coverLetter.Trim().Length, 4000)],
        });
        await _db.SaveChangesAsync(ct);
        TempData["Success"] = _t.F("Applied", job.Title);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Roles = AppRoles.JobSeeker)]
    public async Task<IActionResult> ToggleSave(Guid id, string? returnUrl, CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        if (profile is null || !await _db.PublicJobs().AnyAsync(j => j.Id == id, ct)) return NotFound();

        var saved = await _db.SavedJobs.FindAsync([profile.Id, id], ct);
        if (saved is null) _db.SavedJobs.Add(new SavedJob { SeekerProfileId = profile.Id, JobId = id });
        else _db.SavedJobs.Remove(saved);
        await _db.SaveChangesAsync(ct);

        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>AJAX: the AI reads the seeker's CV against this job → HTML fragment.</summary>
    [HttpPost, Authorize(Roles = AppRoles.JobSeeker)]
    [EnableRateLimiting(RateLimitPolicies.Ai)]
    public async Task<IActionResult> Match(Guid id, CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        var job = await _db.PublicJobs().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (profile is null || job is null) return NotFound();
        if (!profile.CvReady) return StatusCode(409, _t["MatchNeedsCv"]);

        try
        {
            var result = await _match.CheckMatchAsync(profile, job, ct);
            return PartialView("_MatchResult", result);
        }
        catch (Exception ex) when (ex is AiServiceException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Match check failed");
            return StatusCode(502, _t["AiUnavailable"]);
        }
    }

    private async Task<JobSeekerProfile?> MyProfileAsync(CancellationToken ct)
    {
        if (!User.IsInRole(AppRoles.JobSeeker)) return null;
        var userId = User.GetUserId();
        return await _db.SeekerProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    private async Task<(HashSet<Guid> Saved, HashSet<Guid> Applied)> MyJobMarksAsync(CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        if (profile is null) return ([], []);
        var saved = await _db.SavedJobs.Where(s => s.SeekerProfileId == profile.Id).Select(s => s.JobId).ToListAsync(ct);
        var applied = await _db.Applications.Where(a => a.SeekerProfileId == profile.Id).Select(a => a.JobId).ToListAsync(ct);
        return (saved.ToHashSet(), applied.ToHashSet());
    }
}
