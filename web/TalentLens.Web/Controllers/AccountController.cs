using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.Data;
using TalentLens.Infrastructure.Identity;
using TalentLens.Web.Localization;
using TalentLens.Web.Models;
using TalentLens.Web.Security;

namespace TalentLens.Web.Controllers;

/// <summary>Sign in / out, and the two sign-up flows: job seeker and employer.</summary>
[AllowAnonymous]
public class AccountController : Controller
{
    private readonly UserManager<AppUser> _users;
    private readonly SignInManager<AppUser> _signIn;
    private readonly AppDbContext _db;
    private readonly UiText _t;

    public AccountController(UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db, UiText t)
    {
        _users = users;
        _signIn = signIn;
        _db = db;
        _t = t;
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await _signIn.PasswordSignInAsync(model.Email, model.Password, model.RememberMe, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.IsLockedOut ? _t["LockedOut"] : _t["InvalidLogin"]);
            return View(model);
        }

        if (Url.IsLocalUrl(model.ReturnUrl)) return LocalRedirect(model.ReturnUrl);
        var user = await _users.FindByEmailAsync(model.Email);
        return await HomeForAsync(user!);
    }

    [HttpGet]
    public IActionResult Register() => View(new RegisterSeekerViewModel());

    /// <summary>Job seeker sign-up: user + role + empty profile.</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> Register(RegisterSeekerViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        await using var tx = await _db.Database.BeginTransactionAsync();
        var user = new AppUser { UserName = model.Email, Email = model.Email, DisplayName = model.FullName.Trim() };
        if (!await CreateAsync(user, model.Password, AppRoles.JobSeeker)) return View(model);

        _db.SeekerProfiles.Add(new JobSeekerProfile { UserId = user.Id, FullName = user.DisplayName, Email = user.Email });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        await _signIn.SignInAsync(user, isPersistent: false);
        TempData["Success"] = _t["WelcomeSeeker"];
        return RedirectToAction("Index", "Profile", new { area = "Seeker" });
    }

    [HttpGet]
    public IActionResult RegisterEmployer() => View(new RegisterEmployerViewModel());

    /// <summary>Employer sign-up: company (pending admin approval) + recruiter user.</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> RegisterEmployer(RegisterEmployerViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        await using var tx = await _db.Database.BeginTransactionAsync();
        var company = new Company
        {
            Name = model.CompanyName.Trim(),
            Industry = model.Industry?.Trim(),
            Location = model.Location?.Trim(),
            Status = HttpContext.RequestServices.GetRequiredService<IConfiguration>()
                .GetValue("Platform:AutoApproveCompanies", false) ? CompanyStatus.Active : CompanyStatus.Pending,
        };
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var user = new AppUser
        {
            UserName = model.Email, Email = model.Email, DisplayName = model.FullName.Trim(), CompanyId = company.Id,
        };
        if (!await CreateAsync(user, model.Password, AppRoles.Recruiter)) return View(model);
        await tx.CommitAsync();

        await _signIn.SignInAsync(user, isPersistent: false);
        TempData["Success"] = company.Status == CompanyStatus.Active ? _t["WelcomeEmployer"] : _t["WelcomeEmployerPending"];
        return RedirectToAction("Index", "Dashboard", new { area = "Employer" });
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    public IActionResult AccessDenied() => View();

    private async Task<bool> CreateAsync(AppUser user, string password, string role)
    {
        var result = await _users.CreateAsync(user, password);
        if (result.Succeeded) result = await _users.AddToRoleAsync(user, role);
        if (result.Succeeded) return true;

        foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
        return false;
    }

    private async Task<IActionResult> HomeForAsync(AppUser user)
    {
        if (await _users.IsInRoleAsync(user, AppRoles.Admin))
            return RedirectToAction("Index", "Dashboard", new { area = "Admin" });
        if (await _users.IsInRoleAsync(user, AppRoles.Recruiter))
            return RedirectToAction("Index", "Dashboard", new { area = "Employer" });
        return RedirectToAction("Index", "Dashboard", new { area = "Seeker" });
    }
}
