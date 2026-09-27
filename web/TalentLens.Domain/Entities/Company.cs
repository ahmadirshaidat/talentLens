namespace TalentLens.Domain.Entities;

/// <summary>
/// 🔒 MANUAL — a hiring company (the tenant). Recruiters belong to one company; its jobs,
/// uploaded CVs (talent pool) and searches are private to it.
///
/// 🧸 ELI5: every company gets its own locked toy box. Its recruiters can open only that
/// box. The platform admin decides which new companies are allowed in (Status).
/// </summary>
public class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Name { get; set; }
    public string? Industry { get; set; }
    public string? Size { get; set; }
    public string? Location { get; set; }
    public string? Website { get; set; }
    public string? Description { get; set; }

    public CompanyStatus Status { get; set; } = CompanyStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Job> Jobs { get; set; } = new List<Job>();
    public ICollection<Candidate> Candidates { get; set; } = new List<Candidate>();

    /// <summary>Two-letter badge for companies without a logo ("Nimbus Apps" → "NA").</summary>
    public string Initials =>
        string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => w[0]))
            .ToUpperInvariant();
}

/// <summary>
/// 🧸 Pending: just signed up, waiting for the admin. Active: can publish jobs.
/// Suspended: the admin paused them — their jobs disappear from the public board.
/// </summary>
public enum CompanyStatus
{
    Pending = 0,
    Active = 1,
    Suspended = 2,
}

/// <summary>Data that belongs to exactly one company (filtered automatically by AppDbContext).</summary>
public interface ICompanyScoped
{
    Guid CompanyId { get; set; }
}

/// <summary>Identity role names.</summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Recruiter = "Recruiter";
    public const string JobSeeker = "JobSeeker";

    public static readonly string[] All = [Admin, Recruiter, JobSeeker];
}
