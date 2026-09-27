namespace TalentLens.Domain.Entities;

/// <summary>
/// 🔒 MANUAL — a job seeker applying to a job.
///
/// 🧸 ELI5: a letter from the seeker to the company: "I want this job, here's my CV".
/// The recruiter moves it through the hiring steps (Status), and the AI can give it a
/// match score + reason + quotes from the CV, saved here so we don't ask the AI again.
/// One seeker can apply to one job only once.
/// </summary>
public class JobApplication
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid JobId { get; set; }
    public Job? Job { get; set; }

    public Guid SeekerProfileId { get; set; }
    public JobSeekerProfile? SeekerProfile { get; set; }

    public string? CoverLetter { get; set; }
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Submitted;
    public string? RecruiterNote { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ---- AI match (filled by "Rank with AI") ----
    public double? MatchScore { get; set; }
    public string? MatchReason { get; set; }
    public List<EvidenceQuote> MatchEvidence { get; set; } = new();
    public DateTime? MatchedAt { get; set; }
}

/// <summary>🧸 The hiring steps, in order. Rejected can happen at any step.</summary>
public enum ApplicationStatus
{
    Submitted = 0,
    Reviewed = 1,
    Shortlisted = 2,
    Interview = 3,
    Offered = 4,
    Hired = 5,
    Rejected = 6,
    Withdrawn = 7,
}

/// <summary>A quote from the CV that supports the match (stored as JSON).</summary>
public class EvidenceQuote
{
    public string Section { get; set; } = "";
    public string Quote { get; set; } = "";
}

/// <summary>A job a seeker bookmarked.</summary>
public class SavedJob
{
    public Guid SeekerProfileId { get; set; }
    public Guid JobId { get; set; }
    public Job? Job { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}
