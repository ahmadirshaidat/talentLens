using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TalentLens.Infrastructure.Data;
using TalentLens.Infrastructure.Identity;

namespace TalentLens.Web.Services;

/// <summary>Claim names added to the sign-in cookie, and helpers to read them.</summary>
public static class AppClaims
{
    public const string CompanyId = "company_id";
    public const string DisplayName = "display_name";

    public static Guid GetCompanyId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(CompanyId), out var id)
            ? id
            : throw new InvalidOperationException("Signed-in user has no company.");

    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Not signed in.");

    public static string GetDisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue(DisplayName) is { Length: > 0 } name ? name : user.Identity?.Name ?? "";
}

/// <summary>Puts the recruiter's company id and the display name into the auth cookie.</summary>
public sealed class AppClaimsPrincipalFactory : UserClaimsPrincipalFactory<AppUser, IdentityRole>
{
    public AppClaimsPrincipalFactory(
        UserManager<AppUser> userManager, RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user); // includes role claims
        if (user.CompanyId is Guid companyId)
            identity.AddClaim(new Claim(AppClaims.CompanyId, companyId.ToString()));
        identity.AddClaim(new Claim(AppClaims.DisplayName, user.DisplayName));
        return identity;
    }
}

/// <summary>ICurrentCompany for web requests: the signed-in recruiter's company claim.</summary>
public sealed class HttpCurrentCompany : ICurrentCompany
{
    private readonly IHttpContextAccessor _accessor;

    public HttpCurrentCompany(IHttpContextAccessor accessor)
    {
        _accessor = accessor;
    }

    public Guid? CompanyId =>
        Guid.TryParse(_accessor.HttpContext?.User.FindFirstValue(AppClaims.CompanyId), out var id)
            ? id
            : null;
}

/// <summary>
/// How ids are written when talking to the AI service.
/// 🧸 The AI keeps one "box" (workspace) per company for its private talent pool, and one
/// shared box called "seekers" for every job seeker's CV.
/// </summary>
public static class AiIds
{
    public const string SeekersWorkspace = "seekers";

    public static string For(Guid id) => id.ToString("D");
}
