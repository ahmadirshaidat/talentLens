using System.ComponentModel.DataAnnotations;
using TalentLens.Domain.Entities;
using TalentLens.Web.Services;

namespace TalentLens.Web.Models;

// ---------------------------------------------------------------- account

public sealed class LoginViewModel
{
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class RegisterSeekerViewModel
{
    [Required, StringLength(200)] public string FullName { get; set; } = "";
    [Required, EmailAddress] public string Email { get; set; } = "";
    [Required, StringLength(100, MinimumLength = 8), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, DataType(DataType.Password), Compare(nameof(Password))] public string ConfirmPassword { get; set; } = "";
}

public sealed class RegisterEmployerViewModel : RegisterSeekerViewModel
{
    [Required, StringLength(200)] public string CompanyName { get; set; } = "";
    [StringLength(100)] public string? Industry { get; set; }
    [StringLength(200)] public string? Location { get; set; }
}

// ---------------------------------------------------------------- public

public sealed record CompanyCard(Company Company, int OpenJobs);

public sealed class HomeViewModel
{
    public IReadOnlyList<Job> LatestJobs { get; init; } = [];
    public IReadOnlyList<CompanyCard> TopCompanies { get; init; } = [];
    public int JobCount { get; init; }
    public int CompanyCount { get; init; }
    public int SeekerCount { get; init; }
}

public sealed class JobSearchViewModel
{
    public string? Q { get; init; }
    public string? Location { get; init; }
    public EmploymentType? Type { get; init; }
    public WorkplaceType? Workplace { get; init; }
    public IReadOnlyList<Job> Jobs { get; init; } = [];
    public int Page { get; init; } = 1;
    public int TotalPages { get; init; } = 1;
    public int TotalCount { get; init; }
    public IReadOnlySet<Guid> SavedIds { get; init; } = new HashSet<Guid>();
    public IReadOnlySet<Guid> AppliedIds { get; init; } = new HashSet<Guid>();
}

public sealed class JobDetailsViewModel
{
    public required Job Job { get; init; }
    public JobSeekerProfile? Profile { get; init; }
    public JobApplication? MyApplication { get; init; }
    public bool IsSaved { get; init; }
    public bool IsSeeker { get; init; }
    public IReadOnlyList<Job> MoreFromCompany { get; init; } = [];
}

public sealed class CompanyDetailsViewModel
{
    public required Company Company { get; init; }
    public IReadOnlyList<Job> Jobs { get; init; } = [];
}

// ---------------------------------------------------------------- seeker

public sealed class SeekerDashboardViewModel
{
    public required JobSeekerProfile Profile { get; init; }
    public IReadOnlyList<JobRecommendation> Recommendations { get; init; } = [];
    public IReadOnlyList<JobApplication> RecentApplications { get; init; } = [];
    public int ApplicationCount { get; init; }
    public int SavedCount { get; init; }
    public int InterviewCount { get; init; }
}

public sealed class SeekerProfileEditModel
{
    [Required, StringLength(200)] public string FullName { get; set; } = "";
    [StringLength(200)] public string? Headline { get; set; }
    [StringLength(4000)] public string? Summary { get; set; }
    [StringLength(50)] public string? Phone { get; set; }
    [StringLength(200)] public string? Location { get; set; }
    [Range(0, 60)] public double? YearsOfExperience { get; set; }
    [StringLength(1000)] public string? SkillsText { get; set; }
    [StringLength(500)] public string? LanguagesText { get; set; }
    public bool IsSearchable { get; set; }
}

public sealed class SeekerProfilePageModel
{
    public required JobSeekerProfile Profile { get; init; }
    public required SeekerProfileEditModel Edit { get; init; }
}

// ---------------------------------------------------------------- employer

public sealed record EmployerJobRow(Job Job, int Applicants, int NewApplicants);

public sealed class EmployerDashboardViewModel
{
    public required Company Company { get; init; }
    public int OpenJobs { get; init; }
    public int TotalApplicants { get; init; }
    public int NewApplicants { get; init; }
    public int TalentPool { get; init; }
    public IReadOnlyList<EmployerJobRow> Jobs { get; init; } = [];
    public IReadOnlyList<JobApplication> RecentApplications { get; init; } = [];
}

public sealed class JobEditModel
{
    public Guid? Id { get; set; }
    [Required, StringLength(200)] public string Title { get; set; } = "";
    [Required, StringLength(8000)] public string Description { get; set; } = "";
    [StringLength(4000)] public string? Requirements { get; set; }
    [StringLength(200)] public string? Location { get; set; }
    public EmploymentType EmploymentType { get; set; }
    public WorkplaceType WorkplaceType { get; set; }
    [Range(0, 40)] public int? MinYearsExperience { get; set; }
    [Range(0, 10_000_000)] public decimal? SalaryMin { get; set; }
    [Range(0, 10_000_000)] public decimal? SalaryMax { get; set; }
    [StringLength(10)] public string? Currency { get; set; } = "JOD";
    [StringLength(1000)] public string? SkillsText { get; set; }
    public bool Publish { get; set; }
}

public sealed class ApplicantsViewModel
{
    public required Job Job { get; init; }
    public IReadOnlyList<JobApplication> Applications { get; init; } = [];
    public ApplicationStatus? Status { get; init; }
    public string Sort { get; init; } = "match";
}

public sealed class CandidateListViewModel
{
    public IReadOnlyList<Candidate> Candidates { get; init; } = [];
    public string? Query { get; init; }
    public ProcessingStatus? Status { get; init; }
    public int Page { get; init; }
    public int TotalPages { get; init; }
    public int TotalCount { get; init; }
    public bool HasInProgress { get; init; }
}

public sealed class CvSearchViewModel
{
    public string? Query { get; init; }
    public SearchScope Scope { get; init; }
    public int TopK { get; init; } = 10;
    public IReadOnlyList<CvHit> Hits { get; init; } = [];
    public IReadOnlyList<SearchLog> RecentSearches { get; init; } = [];
    public string? Error { get; init; }
    public bool Searched => !string.IsNullOrWhiteSpace(Query);
}

public sealed class CompanyEditModel
{
    [Required, StringLength(200)] public string Name { get; set; } = "";
    [StringLength(100)] public string? Industry { get; set; }
    [StringLength(50)] public string? Size { get; set; }
    [StringLength(200)] public string? Location { get; set; }
    [Url, StringLength(300)] public string? Website { get; set; }
    [StringLength(4000)] public string? Description { get; set; }
}

public sealed class SeekerViewModel
{
    public required JobSeekerProfile Profile { get; init; }
    public IReadOnlyList<JobApplication> ApplicationsToUs { get; init; } = [];
}

// ---------------------------------------------------------------- admin

public sealed class AdminDashboardViewModel
{
    public int Seekers { get; init; }
    public int Recruiters { get; init; }
    public int CompaniesActive { get; init; }
    public int CompaniesPending { get; init; }
    public int JobsOpen { get; init; }
    public int Applications { get; init; }
    public int CvsProcessed { get; init; }
    public bool AiHealthy { get; init; }
    public IReadOnlyList<Company> PendingCompanies { get; init; } = [];
}

public sealed record AdminCompanyRow(Company Company, int Recruiters, int Jobs, int Applicants);

public sealed record AdminUserRow(
    string Id, string DisplayName, string Email, string Role, string? CompanyName,
    DateTime CreatedAt, bool IsLocked);

public sealed class AdminUsersViewModel
{
    public IReadOnlyList<AdminUserRow> Users { get; init; } = [];
    public string? Role { get; init; }
    public string? Q { get; init; }
}

public sealed record AdminJobRow(Job Job, int Applicants);
