using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.AiService;
using TalentLens.Infrastructure.Data;
using TalentLens.Infrastructure.Identity;
using TalentLens.Web.Localization;
using TalentLens.Web.Models;
using TalentLens.Web.Services;

namespace TalentLens.Web.Areas.Admin.Controllers;

/// <summary>
/// Platform admin pages. The admin sees across companies, so private data is read with
/// IgnoreQueryFilters() — only ever counts and moderation here, never CV contents.
/// </summary>
[Area("Admin"), Authorize(Roles = AppRoles.Admin)]
public abstract class AdminControllerBase : Controller
{
    protected readonly AppDbContext Db;

    protected AdminControllerBase(AppDbContext db)
    {
        Db = db;
    }
}

public class DashboardController : AdminControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly AiServiceClient _ai;

    public DashboardController(AppDbContext db, UserManager<AppUser> users, AiServiceClient ai) : base(db)
    {
        _users = users;
        _ai = ai;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var talentReady = await Db.Candidates.IgnoreQueryFilters().CountAsync(c => c.Status == ProcessingStatus.Ready, ct);
        var seekerReady = await Db.SeekerProfiles.CountAsync(p => p.CvStatus == ProcessingStatus.Ready, ct);
        return View(new AdminDashboardViewModel
        {
            Seekers = (await _users.GetUsersInRoleAsync(AppRoles.JobSeeker)).Count,
            Recruiters = (await _users.GetUsersInRoleAsync(AppRoles.Recruiter)).Count,
            CompaniesActive = await Db.Companies.CountAsync(c => c.Status == CompanyStatus.Active, ct),
            CompaniesPending = await Db.Companies.CountAsync(c => c.Status == CompanyStatus.Pending, ct),
            JobsOpen = await Db.Jobs.CountAsync(j => j.Status == JobStatus.Open, ct),
            Applications = await Db.Applications.CountAsync(ct),
            CvsProcessed = talentReady + seekerReady,
            AiHealthy = await _ai.IsHealthyAsync(ct),
            PendingCompanies = await Db.Companies.Where(c => c.Status == CompanyStatus.Pending)
                .OrderBy(c => c.CreatedAt).Take(10).ToListAsync(ct),
        });
    }
}

public class CompaniesController : AdminControllerBase
{
    private readonly UiText _t;

    public CompaniesController(AppDbContext db, UiText t) : base(db)
    {
        _t = t;
    }

    public async Task<IActionResult> Index(CompanyStatus? status, string? q, CancellationToken ct)
    {
        var query = Db.Companies.AsQueryable();
        if (status is not null) query = query.Where(c => c.Status == status);
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(c => c.Name.Contains(q.Trim()));

        var rows = await query
            .OrderBy(c => c.Status).ThenByDescending(c => c.CreatedAt)
            .Select(c => new AdminCompanyRow(c,
                Db.Users.Count(u => u.CompanyId == c.Id),
                c.Jobs.Count,
                c.Jobs.SelectMany(j => j.Applications).Count()))
            .ToListAsync(ct);
        ViewData["Status"] = status;
        ViewData["Q"] = q;
        return View(rows);
    }

    [HttpPost]
    public Task<IActionResult> Approve(Guid id, CancellationToken ct) => SetStatusAsync(id, CompanyStatus.Active, ct);

    [HttpPost]
    public Task<IActionResult> Suspend(Guid id, CancellationToken ct) => SetStatusAsync(id, CompanyStatus.Suspended, ct);

    private async Task<IActionResult> SetStatusAsync(Guid id, CompanyStatus status, CancellationToken ct)
    {
        var company = await Db.Companies.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (company is null) return NotFound();
        company.Status = status;
        await Db.SaveChangesAsync(ct);
        TempData["Success"] = _t.F(status == CompanyStatus.Active ? "CompanyApproved" : "CompanySuspended", company.Name);
        return RedirectToAction(nameof(Index));
    }
}

public class UsersController : AdminControllerBase
{
    private readonly UserManager<AppUser> _users;
    private readonly UiText _t;

    public UsersController(AppDbContext db, UserManager<AppUser> users, UiText t) : base(db)
    {
        _users = users;
        _t = t;
    }

    public async Task<IActionResult> Index(string? role, string? q, CancellationToken ct)
    {
        var roleByUser = (await (
                from ur in Db.UserRoles
                join r in Db.Roles on ur.RoleId equals r.Id
                select new { ur.UserId, r.Name })
            .ToListAsync(ct))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Name)));

        var query = Db.Users.Include(u => u.Company).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(u => u.Email!.Contains(q.Trim()) || u.DisplayName.Contains(q.Trim()));
        var users = await query.OrderByDescending(u => u.CreatedAt).Take(500).ToListAsync(ct);

        var rows = users
            .Select(u => new AdminUserRow(u.Id, u.DisplayName, u.Email ?? "", roleByUser.GetValueOrDefault(u.Id, ""),
                u.Company?.Name, u.CreatedAt, u.LockoutEnd > DateTimeOffset.UtcNow))
            .Where(r => string.IsNullOrEmpty(role) || r.Role.Split(", ").Contains(role))
            .ToList();
        return View(new AdminUsersViewModel { Users = rows, Role = role, Q = q });
    }

    [HttpPost]
    public async Task<IActionResult> Lock(string id, bool locked)
    {
        var user = await _users.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (user.Id == User.GetUserId())
        {
            TempData["Error"] = _t["CannotLockSelf"];
            return RedirectToAction(nameof(Index));
        }
        await _users.SetLockoutEnabledAsync(user, true);
        await _users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null);
        if (locked) await _users.UpdateSecurityStampAsync(user); // signs them out
        TempData["Success"] = _t[locked ? "UserLocked" : "UserUnlocked"];
        return RedirectToAction(nameof(Index));
    }
}

public class JobsController : AdminControllerBase
{
    private readonly UiText _t;

    public JobsController(AppDbContext db, UiText t) : base(db)
    {
        _t = t;
    }

    public async Task<IActionResult> Index(JobStatus? status, string? q, CancellationToken ct)
    {
        var query = Db.Jobs.Include(j => j.Company).AsQueryable();
        if (status is not null) query = query.Where(j => j.Status == status);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(j => j.Title.Contains(q.Trim()) || j.Company!.Name.Contains(q.Trim()));
        var rows = await query.OrderByDescending(j => j.CreatedAt).Take(500)
            .Select(j => new AdminJobRow(j, j.Applications.Count)).ToListAsync(ct);
        ViewData["Status"] = status;
        ViewData["Q"] = q;
        return View(rows);
    }

    [HttpPost]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        var job = await Db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return NotFound();
        job.Status = JobStatus.Closed;
        await Db.SaveChangesAsync(ct);
        TempData["Success"] = _t["JobClosed"];
        return RedirectToAction(nameof(Index));
    }
}
