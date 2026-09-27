using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.Data;
using TalentLens.Web.Models;
using TalentLens.Web.Services;

namespace TalentLens.Web.Controllers;

/// <summary>Public company directory and company pages.</summary>
public class CompaniesController : Controller
{
    private readonly AppDbContext _db;

    public CompaniesController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index(string? q, CancellationToken ct)
    {
        var query = _db.Companies.Where(c => c.Status == CompanyStatus.Active);
        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(c => c.Name.Contains(q.Trim()) || (c.Industry != null && c.Industry.Contains(q.Trim())));

        var companies = await query
            .Select(c => new { Company = c, Open = c.Jobs.Count(j => j.Status == JobStatus.Open) })
            .OrderByDescending(x => x.Open).ThenBy(x => x.Company.Name)
            .ToListAsync(ct);
        ViewData["Q"] = q;
        return View(companies.Select(x => new CompanyCard(x.Company, x.Open)).ToList());
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.Id == id && c.Status == CompanyStatus.Active, ct);
        if (company is null) return NotFound();

        var jobs = await _db.PublicJobs().Where(j => j.CompanyId == id)
            .OrderByDescending(j => j.PublishedAt).ToListAsync(ct);
        jobs.ForEach(j => j.Company = company);
        return View(new CompanyDetailsViewModel { Company = company, Jobs = jobs });
    }
}
