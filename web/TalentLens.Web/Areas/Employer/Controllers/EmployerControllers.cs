using System.Security.Claims;
using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.AiService;
using TalentLens.Infrastructure.Data;
using TalentLens.Infrastructure.Storage;
using TalentLens.Web.Localization;
using TalentLens.Web.Models;
using TalentLens.Web.Security;
using TalentLens.Web.Services;

namespace TalentLens.Web.Areas.Employer.Controllers;

/// <summary>Base for recruiter pages: role check + the recruiter's company.</summary>
[Area("Employer"), Authorize(Roles = AppRoles.Recruiter)]
public abstract class EmployerControllerBase : Controller
{
    protected readonly AppDbContext Db;

    protected EmployerControllerBase(AppDbContext db)
    {
        Db = db;
    }

    protected Guid CompanyId => User.GetCompanyId();

    protected Task<Company> MyCompanyAsync(CancellationToken ct = default) =>
        Db.Companies.FirstAsync(c => c.Id == CompanyId, ct);

    /// <summary>🧸 Jobs have no automatic filter (they're public), so employer pages always add this.</summary>
    protected IQueryable<Job> MyJobs() => Db.Jobs.Where(j => j.CompanyId == CompanyId);

    protected static bool IsAiError(Exception ex) =>
        ex is AiServiceException or HttpRequestException or TaskCanceledException;
}

public class DashboardController : EmployerControllerBase
{
    public DashboardController(AppDbContext db) : base(db)
    {
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var company = await MyCompanyAsync(ct);
        var jobs = await MyJobs()
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new EmployerJobRow(j,
                j.Applications.Count(a => a.Status != ApplicationStatus.Withdrawn),
                j.Applications.Count(a => a.Status == ApplicationStatus.Submitted)))
            .Take(8)
            .ToListAsync(ct);
        var applications = Db.Applications.Where(a => a.Job!.CompanyId == CompanyId && a.Status != ApplicationStatus.Withdrawn);

        return View(new EmployerDashboardViewModel
        {
            Company = company,
            OpenJobs = await MyJobs().CountAsync(j => j.Status == JobStatus.Open, ct),
            TotalApplicants = await applications.CountAsync(ct),
            NewApplicants = await applications.CountAsync(a => a.Status == ApplicationStatus.Submitted, ct),
            TalentPool = await Db.Candidates.CountAsync(ct),
            Jobs = jobs,
            RecentApplications = await applications
                .Include(a => a.Job).Include(a => a.SeekerProfile)
                .OrderByDescending(a => a.AppliedAt).Take(6).ToListAsync(ct),
        });
    }
}

public class JobsController : EmployerControllerBase
{
    private readonly ApplicantRankingService _ranking;
    private readonly UiText _t;
    private readonly ILogger<JobsController> _logger;

    public JobsController(AppDbContext db, ApplicantRankingService ranking, UiText t, ILogger<JobsController> logger)
        : base(db)
    {
        _ranking = ranking;
        _t = t;
        _logger = logger;
    }

    public async Task<IActionResult> Index(JobStatus? status, CancellationToken ct)
    {
        var query = MyJobs();
        if (status is not null) query = query.Where(j => j.Status == status);
        var rows = await query
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new EmployerJobRow(j,
                j.Applications.Count(a => a.Status != ApplicationStatus.Withdrawn),
                j.Applications.Count(a => a.Status == ApplicationStatus.Submitted)))
            .ToListAsync(ct);
        ViewData["Status"] = status;
        ViewData["CompanyActive"] = (await MyCompanyAsync(ct)).Status == CompanyStatus.Active;
        return View(rows);
    }

    [HttpGet]
    public IActionResult Create() => View("Edit", new JobEditModel());

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var job = await MyJobs().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return NotFound();
        return View(new JobEditModel
        {
            Id = job.Id, Title = job.Title, Description = job.Description, Requirements = job.Requirements,
            Location = job.Location, EmploymentType = job.EmploymentType, WorkplaceType = job.WorkplaceType,
            MinYearsExperience = job.MinYearsExperience, SalaryMin = job.SalaryMin, SalaryMax = job.SalaryMax,
            Currency = job.Currency, SkillsText = string.Join(", ", job.Skills),
        });
    }

    [HttpPost]
    public async Task<IActionResult> Save(JobEditModel model, CancellationToken ct)
    {
        if (model.SalaryMin > model.SalaryMax)
            ModelState.AddModelError(nameof(model.SalaryMax), _t["SalaryRange"]);
        if (!ModelState.IsValid) return View("Edit", model);

        Job? job;
        if (model.Id is Guid id)
        {
            job = await MyJobs().FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null) return NotFound();
        }
        else
        {
            job = new Job
            {
                CompanyId = CompanyId, Title = "", Description = "",
                CreatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            };
            Db.Jobs.Add(job);
        }

        job.Title = model.Title.Trim();
        job.Description = model.Description.Trim();
        job.Requirements = model.Requirements?.Trim();
        job.Location = model.Location?.Trim();
        job.EmploymentType = model.EmploymentType;
        job.WorkplaceType = model.WorkplaceType;
        job.MinYearsExperience = model.MinYearsExperience;
        job.SalaryMin = model.SalaryMin;
        job.SalaryMax = model.SalaryMax;
        job.Currency = string.IsNullOrWhiteSpace(model.Currency) ? null : model.Currency.Trim().ToUpperInvariant();
        job.Skills = Seeker.Controllers.ProfileController.SplitList(model.SkillsText);

        if (model.Publish && !await TryPublishAsync(job, ct))
        {
            await Db.SaveChangesAsync(ct);
            return RedirectToAction(nameof(Index));
        }

        await Db.SaveChangesAsync(ct);
        TempData["Success"] = model.Publish ? _t["JobPublished"] : _t["JobSaved"];
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Publish(Guid id, CancellationToken ct)
    {
        var job = await MyJobs().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return NotFound();
        if (await TryPublishAsync(job, ct)) TempData["Success"] = _t["JobPublished"];
        await Db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        var job = await MyJobs().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return NotFound();
        job.Status = JobStatus.Closed;
        await Db.SaveChangesAsync(ct);
        TempData["Success"] = _t["JobClosed"];
        return RedirectToAction(nameof(Index));
    }

    /// <summary>🧸 Only companies the admin approved can put jobs on the public board.</summary>
    private async Task<bool> TryPublishAsync(Job job, CancellationToken ct)
    {
        var company = await MyCompanyAsync(ct);
        if (company.Status != CompanyStatus.Active)
        {
            job.Status = JobStatus.Draft;
            TempData["Error"] = _t["CompanyNotApproved"];
            return false;
        }
        job.Status = JobStatus.Open;
        job.PublishedAt ??= DateTime.UtcNow;
        return true;
    }

    public async Task<IActionResult> Applicants(Guid id, ApplicationStatus? status, string sort = "match",
        CancellationToken ct = default)
    {
        var job = await MyJobs().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return NotFound();

        var query = Db.Applications.Where(a => a.JobId == id).Include(a => a.SeekerProfile).AsQueryable();
        query = status is null
            ? query.Where(a => a.Status != ApplicationStatus.Withdrawn)
            : query.Where(a => a.Status == status);
        query = sort == "date"
            ? query.OrderByDescending(a => a.AppliedAt)
            : query.OrderByDescending(a => a.MatchScore ?? -1).ThenByDescending(a => a.AppliedAt);

        return View(new ApplicantsViewModel
        {
            Job = job, Applications = await query.ToListAsync(ct), Status = status, Sort = sort,
        });
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Ai)]
    public async Task<IActionResult> Rank(Guid id, CancellationToken ct)
    {
        try
        {
            var count = await _ranking.RankAsync(CompanyId, id, ct);
            TempData[count > 0 ? "Success" : "Error"] = count > 0 ? _t.F("Ranked", count) : _t["NothingToRank"];
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (Exception ex) when (IsAiError(ex))
        {
            _logger.LogWarning(ex, "Ranking failed");
            TempData["Error"] = ex is AiServiceException ai ? ai.Detail : _t["AiUnavailable"];
        }
        return RedirectToAction(nameof(Applicants), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> SetStatus(Guid applicationId, ApplicationStatus status, string? note,
        CancellationToken ct)
    {
        var app = await Db.Applications.Include(a => a.Job)
            .FirstOrDefaultAsync(a => a.Id == applicationId && a.Job!.CompanyId == CompanyId, ct);
        if (app is null) return NotFound();
        if (app.Status != ApplicationStatus.Withdrawn && status != ApplicationStatus.Withdrawn)
        {
            app.Status = status;
            if (note is not null) app.RecruiterNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            app.UpdatedAt = DateTime.UtcNow;
            await Db.SaveChangesAsync(ct);
            TempData["Success"] = _t["StatusUpdated"];
        }
        return RedirectToAction(nameof(Applicants), new { id = app.JobId });
    }
}

/// <summary>The company's private talent pool: CVs recruiters uploaded themselves.</summary>
public class CandidatesController : EmployerControllerBase
{
    private const int PageSize = 20;
    private const long MaxRequestBytes = 200L * 1024 * 1024;

    private readonly IFileStorage _storage;
    private readonly IBackgroundJobClient _jobs;
    private readonly StorageOptions _storageOptions;
    private readonly UploadScreener _screener;
    private readonly UiText _t;

    public CandidatesController(AppDbContext db, IFileStorage storage, IBackgroundJobClient jobs,
        IOptions<StorageOptions> storageOptions, UploadScreener screener, UiText t) : base(db)
    {
        _storage = storage;
        _jobs = jobs;
        _storageOptions = storageOptions.Value;
        _screener = screener;
        _t = t;
    }

    public async Task<IActionResult> Index(string? q, ProcessingStatus? status, int page = 1)
    {
        var query = Db.Candidates.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(c =>
                (c.FullName != null && c.FullName.Contains(term)) ||
                c.OriginalFileName.Contains(term) ||
                (c.Email != null && c.Email.Contains(term)));
        }
        if (status is not null) query = query.Where(c => c.Status == status);

        var total = await query.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        page = Math.Clamp(page, 1, totalPages);

        return View(new CandidateListViewModel
        {
            Candidates = await query.OrderByDescending(c => c.UploadedAt)
                .Skip((page - 1) * PageSize).Take(PageSize).ToListAsync(),
            Query = q,
            Status = status,
            Page = page,
            TotalPages = totalPages,
            TotalCount = total,
            HasInProgress = await Db.Candidates.AnyAsync(c =>
                c.Status == ProcessingStatus.Pending || c.Status == ProcessingStatus.Processing),
        });
    }

    [HttpGet]
    public IActionResult Upload() => View();

    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [EnableRateLimiting(RateLimitPolicies.Upload)]
    public async Task<IActionResult> Upload(List<IFormFile> files, CancellationToken ct)
    {
        if (files.Count == 0)
        {
            TempData["Error"] = _t["NoFilesSelected"];
            return RedirectToAction(nameof(Upload));
        }

        var accepted = 0;
        var rejected = new List<string>();
        foreach (var file in files)
        {
            await using var stream = file.OpenReadStream();
            var check = await _screener.ScreenAsync(file, stream, _storageOptions.MaxUploadMb, ct);
            if (!check.Ok)
            {
                rejected.Add($"{file.FileName}: {_t.F(check.ErrorKey!, _storageOptions.MaxUploadMb)}");
                continue;
            }

            var candidate = new Candidate
            {
                CompanyId = CompanyId,
                OriginalFileName = Path.GetFileName(file.FileName),
                StoredFileName = "",
                ContentType = check.ContentType,
                FileSizeBytes = file.Length,
                UploadedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            };
            candidate.StoredFileName = await _storage.SaveAsync(CompanyId, candidate.Id, check.Extension, stream, ct);
            Db.Candidates.Add(candidate);
            await Db.SaveChangesAsync(ct);
            _jobs.Enqueue<CandidateIngestionJob>(j => j.RunAsync(candidate.Id, CancellationToken.None));
            accepted++;
        }

        if (accepted > 0) TempData["Success"] = _t.F("FilesQueued", accepted);
        if (rejected.Count > 0) TempData["Error"] = string.Join("\n", rejected);
        return accepted > 0 ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Upload));
    }

    public async Task<IActionResult> Details(Guid id)
    {
        var candidate = await Db.Candidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        return candidate is null ? NotFound() : View(candidate);
    }

    public async Task<IActionResult> Download(Guid id)
    {
        var candidate = await Db.Candidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (candidate is null) return NotFound();
        try
        {
            return File(_storage.OpenRead(candidate.CompanyId, candidate.StoredFileName),
                candidate.ContentType, candidate.OriginalFileName);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<IActionResult> Retry(Guid id)
    {
        var candidate = await Db.Candidates.FirstOrDefaultAsync(c => c.Id == id);
        if (candidate is null) return NotFound();
        candidate.Status = ProcessingStatus.Pending;
        candidate.Error = null;
        await Db.SaveChangesAsync();
        _jobs.Enqueue<CandidateIngestionJob>(j => j.RunAsync(candidate.Id, CancellationToken.None));
        TempData["Success"] = _t["RetryQueued"];
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(Guid id)
    {
        var candidate = await Db.Candidates.FirstOrDefaultAsync(c => c.Id == id);
        if (candidate is null) return NotFound();

        Db.Candidates.Remove(candidate);
        await Db.SaveChangesAsync();
        _storage.Delete(candidate.CompanyId, candidate.StoredFileName);
        var aiId = AiIds.For(candidate.Id);
        var aiWorkspace = AiIds.For(candidate.CompanyId);
        _jobs.Enqueue<AiCleanupJob>(j => j.RunAsync(aiId, aiWorkspace, CancellationToken.None));

        TempData["Success"] = _t.F("CandidateDeleted", candidate.DisplayName);
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>AI CV search over the talent pool or the job seekers database.</summary>
public class SearchController : EmployerControllerBase
{
    private readonly CvSearchService _search;
    private readonly UiText _t;
    private readonly ILogger<SearchController> _logger;

    public SearchController(AppDbContext db, CvSearchService search, UiText t, ILogger<SearchController> logger)
        : base(db)
    {
        _search = search;
        _t = t;
        _logger = logger;
    }

    [EnableRateLimiting(RateLimitPolicies.Ai)]
    public async Task<IActionResult> Index(string? q, SearchScope scope = SearchScope.Seekers, int top = 10,
        CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 50);
        var recent = await _search.RecentSearchesAsync(8, ct);
        if (string.IsNullOrWhiteSpace(q))
            return View(new CvSearchViewModel { Scope = scope, TopK = top, RecentSearches = recent });

        var query = q.Trim();
        try
        {
            var hits = await _search.SearchAsync(CompanyId, User.FindFirstValue(ClaimTypes.NameIdentifier),
                query, scope, top, ct);
            return View(new CvSearchViewModel
            {
                Query = query, Scope = scope, TopK = top, Hits = hits,
                RecentSearches = await _search.RecentSearchesAsync(8, ct),
            });
        }
        catch (Exception ex) when (IsAiError(ex))
        {
            _logger.LogWarning(ex, "Search failed");
            return View(new CvSearchViewModel
            {
                Query = query, Scope = scope, TopK = top, RecentSearches = recent,
                Error = ex is AiServiceException ai ? ai.Detail : _t["AiUnavailable"],
            });
        }
    }

    /// <summary>AJAX: "Why this candidate?" → HTML fragment.</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Ai)]
    public async Task<IActionResult> Explain(Guid id, SearchScope scope, string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return BadRequest();
        try
        {
            var result = await _search.ExplainAsync(CompanyId, scope, id, q.Trim(), ct);
            return result is null ? NotFound() : PartialView("_Explanation", result);
        }
        catch (Exception ex) when (IsAiError(ex))
        {
            _logger.LogWarning(ex, "Explain failed");
            return StatusCode(502, ex is AiServiceException ai ? ai.Detail : _t["AiUnavailable"]);
        }
    }
}

/// <summary>A job seeker's profile as a recruiter sees it (searchable, or applied to us).</summary>
public class SeekersController : EmployerControllerBase
{
    private readonly CvSearchService _search;
    private readonly IFileStorage _storage;

    public SeekersController(AppDbContext db, CvSearchService search, IFileStorage storage) : base(db)
    {
        _search = search;
        _storage = storage;
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        if (!await _search.CanViewSeekerAsync(CompanyId, id, ct)) return NotFound();
        var profile = await Db.SeekerProfiles.FirstAsync(p => p.Id == id, ct);
        var apps = await Db.Applications
            .Where(a => a.SeekerProfileId == id && a.Job!.CompanyId == CompanyId)
            .Include(a => a.Job).OrderByDescending(a => a.AppliedAt).ToListAsync(ct);
        return View(new SeekerViewModel { Profile = profile, ApplicationsToUs = apps });
    }

    public async Task<IActionResult> Cv(Guid id, CancellationToken ct)
    {
        if (!await _search.CanViewSeekerAsync(CompanyId, id, ct)) return NotFound();
        var profile = await Db.SeekerProfiles.FirstAsync(p => p.Id == id, ct);
        if (!profile.HasCv) return NotFound();
        return File(_storage.OpenRead(profile.Id, profile.CvStoredFileName!),
            profile.CvContentType ?? "application/octet-stream", profile.CvOriginalFileName);
    }
}

public class CompanyController : EmployerControllerBase
{
    private readonly UiText _t;

    public CompanyController(AppDbContext db, UiText t) : base(db)
    {
        _t = t;
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var c = await MyCompanyAsync(ct);
        ViewData["Status"] = c.Status;
        ViewData["CompanyId"] = c.Id;
        return View(new CompanyEditModel
        {
            Name = c.Name, Industry = c.Industry, Size = c.Size, Location = c.Location,
            Website = c.Website, Description = c.Description,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Index(CompanyEditModel model, CancellationToken ct)
    {
        var c = await MyCompanyAsync(ct);
        if (!ModelState.IsValid)
        {
            ViewData["Status"] = c.Status;
            ViewData["CompanyId"] = c.Id;
            return View(model);
        }
        c.Name = model.Name.Trim();
        c.Industry = model.Industry?.Trim();
        c.Size = model.Size?.Trim();
        c.Location = model.Location?.Trim();
        c.Website = model.Website?.Trim();
        c.Description = model.Description?.Trim();
        await Db.SaveChangesAsync(ct);
        TempData["Success"] = _t["CompanySaved"];
        return RedirectToAction(nameof(Index));
    }
}
