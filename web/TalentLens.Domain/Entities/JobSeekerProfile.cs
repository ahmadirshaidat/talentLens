namespace TalentLens.Domain.Entities;

/// <summary>
/// 🔒 MANUAL — a job seeker's profile and CV.
///
/// 🧸 ELI5: the job seeker's "about me" page. They upload a CV once; the AI reads it and
/// fills in the boxes (skills, years, titles…), and they can fix anything by hand.
/// If IsSearchable is on, recruiters can find them in the CV database even before they
/// apply. When they apply to a job, the company sees this profile + CV.
/// </summary>
public class JobSeekerProfile : IExtractedProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string UserId { get; set; }

    public string? FullName { get; set; }
    public string? Headline { get; set; }
    public string? Summary { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Location { get; set; }
    public double? YearsOfExperience { get; set; }
    public List<string> Skills { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public List<string> JobTitles { get; set; } = new();
    public List<Education> Education { get; set; } = new();

    /// <summary>Visible to recruiters in the CV database.</summary>
    public bool IsSearchable { get; set; } = true;

    // ---- CV file ----
    public string? CvOriginalFileName { get; set; }
    public string? CvStoredFileName { get; set; }
    public string? CvContentType { get; set; }
    public long CvFileSizeBytes { get; set; }
    public DateTime? CvUploadedAt { get; set; }
    public ProcessingStatus? CvStatus { get; set; }
    public string? CvError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<JobApplication> Applications { get; set; } = new List<JobApplication>();

    public bool HasCv => CvStoredFileName is not null;
    public bool CvReady => CvStatus == ProcessingStatus.Ready;
    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? "—" : FullName;

    /// <summary>🧸 How "complete" the profile is, for the progress bar (0–100).</summary>
    public int Completeness()
    {
        var checks = new[]
        {
            !string.IsNullOrWhiteSpace(FullName), !string.IsNullOrWhiteSpace(Headline),
            !string.IsNullOrWhiteSpace(Location), !string.IsNullOrWhiteSpace(Phone),
            YearsOfExperience is not null, Skills.Count > 0, Languages.Count > 0,
            Education.Count > 0, CvReady, !string.IsNullOrWhiteSpace(Summary),
        };
        return checks.Count(c => c) * 100 / checks.Length;
    }
}
