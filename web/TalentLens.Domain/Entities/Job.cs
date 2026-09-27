namespace TalentLens.Domain.Entities;

/// <summary>
/// 🔒 MANUAL — a job posting.
///
/// 🧸 ELI5: the "we're hiring!" poster a company pins on the board. Draft = still writing
/// it, Open = on the board and people can apply, Closed = taken down.
/// Only Open jobs of Active companies are shown to job seekers.
/// </summary>
public class Job
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }

    public required string Title { get; set; }
    public required string Description { get; set; }
    public string? Requirements { get; set; }
    public string? Location { get; set; }
    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;
    public WorkplaceType WorkplaceType { get; set; } = WorkplaceType.Onsite;
    public int? MinYearsExperience { get; set; }
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }
    public string? Currency { get; set; }
    public List<string> Skills { get; set; } = new();

    public JobStatus Status { get; set; } = JobStatus.Draft;
    public string? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }

    public ICollection<JobApplication> Applications { get; set; } = new List<JobApplication>();

    /// <summary>
    /// 🧸 The text we give the AI to find matching CVs: title, skills, experience and
    /// requirements, in one sentence-like query.
    /// </summary>
    public string ToSearchQuery()
    {
        var parts = new List<string> { Title };
        if (Skills.Count > 0) parts.Add(string.Join(", ", Skills));
        if (MinYearsExperience is > 0) parts.Add($"{MinYearsExperience}+ years of experience");
        if (!string.IsNullOrWhiteSpace(Requirements)) parts.Add(Requirements);
        var query = string.Join(". ", parts);
        return query.Length <= 1500 ? query : query[..1500];
    }
}

public enum JobStatus
{
    Draft = 0,
    Open = 1,
    Closed = 2,
}

public enum EmploymentType
{
    FullTime = 0,
    PartTime = 1,
    Contract = 2,
    Internship = 3,
}

public enum WorkplaceType
{
    Onsite = 0,
    Remote = 1,
    Hybrid = 2,
}
