using Microsoft.AspNetCore.Identity;
using TalentLens.Domain.Entities;

namespace TalentLens.Infrastructure.Identity;

/// <summary>
/// 🔒 MANUAL — anyone who can sign in: platform admin, recruiter, or job seeker.
///
/// 🧸 ELI5: IdentityUser already knows email + password. We add a display name and, for
/// recruiters only, "which company do you work for?". WHAT they may do comes from their
/// role (Admin / Recruiter / JobSeeker), stored by Identity in AspNetUserRoles.
/// </summary>
public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";

    /// <summary>Set for recruiters; null for admins and job seekers.</summary>
    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
