using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.Data;
using TalentLens.Infrastructure.Storage;
using TalentLens.Web.Localization;
using TalentLens.Web.Models;
using TalentLens.Web.Security;
using TalentLens.Web.Services;

namespace TalentLens.Web.Areas.Seeker.Controllers;

/// <summary>Base for job-seeker pages: role check + loading "my" profile.</summary>
[Area("Seeker"), Authorize(Roles = AppRoles.JobSeeker)]
public abstract class SeekerControllerBase : Controller
{
    protected readonly AppDbContext Db;

    protected SeekerControllerBase(AppDbContext db)
    {
        Db = db;
    }

    protected async Task<JobSeekerProfile> MyProfileAsync(CancellationToken ct = default)
    {
        var userId = User.GetUserId();
        var profile = await Db.SeekerProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is not null) return profile;

        // Accounts created before profiles existed (or by an admin) get one on first visit.
        profile = new JobSeekerProfile { UserId = userId, FullName = User.GetDisplayName() };
        Db.SeekerProfiles.Add(profile);
        await Db.SaveChangesAsync(ct);
        return profile;
    }
}

public class DashboardController : SeekerControllerBase
{
    private readonly JobMatchService _match;

    public DashboardController(AppDbContext db, JobMatchService match) : base(db)
    {
        _match = match;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        var apps = Db.Applications.Where(a => a.SeekerProfileId == profile.Id);
        return View(new SeekerDashboardViewModel
        {
            Profile = profile,
            Recommendations = await _match.RecommendAsync(profile, 6, ct),
            RecentApplications = await apps.Include(a => a.Job!).ThenInclude(j => j.Company)
                .OrderByDescending(a => a.AppliedAt).Take(5).ToListAsync(ct),
            ApplicationCount = await apps.CountAsync(ct),
            InterviewCount = await apps.CountAsync(a =>
                a.Status == ApplicationStatus.Shortlisted || a.Status == ApplicationStatus.Interview ||
                a.Status == ApplicationStatus.Offered, ct),
            SavedCount = await Db.SavedJobs.CountAsync(s => s.SeekerProfileId == profile.Id, ct),
        });
    }
}

public class ProfileController : SeekerControllerBase
{
    private readonly IFileStorage _storage;
    private readonly IBackgroundJobClient _jobs;
    private readonly StorageOptions _storageOptions;
    private readonly UploadScreener _screener;
    private readonly UiText _t;

    public ProfileController(AppDbContext db, IFileStorage storage, IBackgroundJobClient jobs,
        IOptions<StorageOptions> storageOptions, UploadScreener screener, UiText t) : base(db)
    {
        _storage = storage;
        _jobs = jobs;
        _storageOptions = storageOptions.Value;
        _screener = screener;
        _t = t;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        return View(new SeekerProfilePageModel { Profile = profile, Edit = ToEdit(profile) });
    }

    [HttpPost]
    public async Task<IActionResult> Update([Bind(Prefix = "Edit")] SeekerProfileEditModel model, CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        if (!ModelState.IsValid)
            return View("Index", new SeekerProfilePageModel { Profile = profile, Edit = model });

        profile.FullName = model.FullName.Trim();
        profile.Headline = Clean(model.Headline);
        profile.Summary = Clean(model.Summary);
        profile.Phone = Clean(model.Phone);
        profile.Location = Clean(model.Location);
        profile.YearsOfExperience = model.YearsOfExperience;
        profile.Skills = SplitList(model.SkillsText);
        profile.Languages = SplitList(model.LanguagesText);
        profile.IsSearchable = model.IsSearchable;
        profile.UpdatedAt = DateTime.UtcNow;
        await Db.SaveChangesAsync(ct);

        TempData["Success"] = _t["ProfileSaved"];
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [RequestSizeLimit(20 * 1024 * 1024)]
    [EnableRateLimiting(RateLimitPolicies.Upload)]
    public async Task<IActionResult> UploadCv(IFormFile? file, CancellationToken ct)
    {
        if (file is null)
        {
            TempData["Error"] = _t["NoFilesSelected"];
            return RedirectToAction(nameof(Index));
        }

        var profile = await MyProfileAsync(ct);
        await using var stream = file.OpenReadStream();
        var check = await _screener.ScreenAsync(file, stream, _storageOptions.MaxUploadMb, ct);
        if (!check.Ok)
        {
            TempData["Error"] = _t.F(check.ErrorKey!, _storageOptions.MaxUploadMb);
            return RedirectToAction(nameof(Index));
        }

        var oldFile = profile.CvStoredFileName;
        profile.CvStoredFileName = await _storage.SaveAsync(profile.Id, Guid.NewGuid(), check.Extension, stream, ct);
        profile.CvOriginalFileName = Path.GetFileName(file.FileName);
        profile.CvContentType = check.ContentType;
        profile.CvFileSizeBytes = file.Length;
        profile.CvUploadedAt = DateTime.UtcNow;
        profile.CvStatus = ProcessingStatus.Pending;
        profile.CvError = null;
        await Db.SaveChangesAsync(ct);
        if (oldFile is not null) _storage.Delete(profile.Id, oldFile);

        // 🧸 The AI reads the CV in the background and fills the empty profile boxes.
        _jobs.Enqueue<SeekerCvIngestionJob>(j => j.RunAsync(profile.Id, CancellationToken.None));
        TempData["Success"] = _t["CvUploaded"];
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> RetryCv(CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        if (!profile.HasCv) return RedirectToAction(nameof(Index));
        profile.CvStatus = ProcessingStatus.Pending;
        await Db.SaveChangesAsync(ct);
        _jobs.Enqueue<SeekerCvIngestionJob>(j => j.RunAsync(profile.Id, CancellationToken.None));
        TempData["Success"] = _t["RetryQueued"];
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Cv(CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        if (!profile.HasCv) return NotFound();
        return File(_storage.OpenRead(profile.Id, profile.CvStoredFileName!),
            profile.CvContentType ?? "application/octet-stream", profile.CvOriginalFileName);
    }

    private static SeekerProfileEditModel ToEdit(JobSeekerProfile p) => new()
    {
        FullName = p.FullName ?? "",
        Headline = p.Headline,
        Summary = p.Summary,
        Phone = p.Phone,
        Location = p.Location,
        YearsOfExperience = p.YearsOfExperience,
        SkillsText = string.Join(", ", p.Skills),
        LanguagesText = string.Join(", ", p.Languages),
        IsSearchable = p.IsSearchable,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static List<string> SplitList(string? text) =>
        ProfileMapper.Merge((text ?? "").Split([',', '،', '\n'], StringSplitOptions.RemoveEmptyEntries), []);
}

public class ApplicationsController : SeekerControllerBase
{
    private readonly UiText _t;

    public ApplicationsController(AppDbContext db, UiText t) : base(db)
    {
        _t = t;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        var apps = await Db.Applications
            .Where(a => a.SeekerProfileId == profile.Id)
            .Include(a => a.Job!).ThenInclude(j => j.Company)
            .OrderByDescending(a => a.AppliedAt)
            .ToListAsync(ct);
        return View(apps);
    }

    [HttpPost]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        var app = await Db.Applications.FirstOrDefaultAsync(a => a.Id == id && a.SeekerProfileId == profile.Id, ct);
        if (app is null) return NotFound();
        if (app.Status is not (ApplicationStatus.Hired or ApplicationStatus.Rejected))
        {
            app.Status = ApplicationStatus.Withdrawn;
            app.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync(ct);
            TempData["Success"] = _t["Withdrawn"];
        }
        return RedirectToAction(nameof(Index));
    }
}

public class SavedJobsController : SeekerControllerBase
{
    public SavedJobsController(AppDbContext db) : base(db)
    {
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var profile = await MyProfileAsync(ct);
        var jobs = await Db.SavedJobs
            .Where(s => s.SeekerProfileId == profile.Id)
            .OrderByDescending(s => s.SavedAt)
            .Select(s => s.Job!)
            .Include(j => j.Company)
            .ToListAsync(ct);
        return View(jobs);
    }
}
