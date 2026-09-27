using System.Diagnostics;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.Data;
using TalentLens.Web.Models;
using TalentLens.Web.Services;

namespace TalentLens.Web.Controllers;

public class HomeController : Controller
{
    private readonly AppDbContext _db;

    public HomeController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var latest = await _db.PublicJobs()
            .Include(j => j.Company)
            .OrderByDescending(j => j.PublishedAt)
            .Take(8)
            .ToListAsync(ct);

        var topCompanies = await _db.Companies
            .Where(c => c.Status == CompanyStatus.Active)
            .Select(c => new { Company = c, Open = c.Jobs.Count(j => j.Status == JobStatus.Open) })
            .Where(x => x.Open > 0)
            .OrderByDescending(x => x.Open)
            .Take(6)
            .ToListAsync(ct);

        return View(new HomeViewModel
        {
            LatestJobs = latest,
            TopCompanies = topCompanies.Select(x => new CompanyCard(x.Company, x.Open)).ToList(),
            JobCount = await _db.PublicJobs().CountAsync(ct),
            CompanyCount = await _db.Companies.CountAsync(c => c.Status == CompanyStatus.Active, ct),
            SeekerCount = await _db.SeekerProfiles.CountAsync(ct),
        });
    }

    public IActionResult Employers() => View();

    /// <summary>Switch UI language (en / ar) via the standard culture cookie.</summary>
    [HttpPost]
    public IActionResult SetLanguage(string culture, string? returnUrl)
    {
        if (culture is not (Localization.UiText.English or Localization.UiText.Arabic))
            culture = Localization.UiText.English;
        Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true });
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() =>
        View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
