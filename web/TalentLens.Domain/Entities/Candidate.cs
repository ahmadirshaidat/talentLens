namespace TalentLens.Domain.Entities;

/// <summary>
/// 🔒 MANUAL — a CV a recruiter uploaded into their company's private talent pool, plus the
/// profile the AI extracted from it.
///
/// 🧸 ELI5: a card for one person. The top half is about the FILE (name, size, is it
/// processed yet?). The bottom half is what the AI FOUND in it (name, skills, years…).
/// </summary>
public class Candidate : ICompanyScoped, IExtractedProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CompanyId { get; set; }
    public Company? Company { get; set; }

    // ---- the file ----
    public required string OriginalFileName { get; set; }
    public required string StoredFileName { get; set; }
    public required string ContentType { get; set; }
    public long FileSizeBytes { get; set; }
    public string? UploadedByUserId { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    // ---- processing ----
    public ProcessingStatus Status { get; set; } = ProcessingStatus.Pending;
    public string? Error { get; set; }
    public DateTime? ProcessedAt { get; set; }

    // ---- extracted profile (filled when Status == Ready) ----
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Location { get; set; }
    public double? YearsOfExperience { get; set; }
    public List<string> Skills { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public List<string> JobTitles { get; set; } = new();
    public List<Education> Education { get; set; } = new();

    public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? OriginalFileName : FullName;
}

/// <summary>Fields the AI fills from a CV — shared by talent-pool candidates and job seekers.</summary>
public interface IExtractedProfile
{
    string? FullName { get; set; }
    string? Email { get; set; }
    string? Phone { get; set; }
    string? Location { get; set; }
    double? YearsOfExperience { get; set; }
    List<string> Skills { get; set; }
    List<string> Languages { get; set; }
    List<string> JobTitles { get; set; }
    List<Education> Education { get; set; }
}

/// <summary>One degree. Stored as JSON inside the owner's row.</summary>
public class Education
{
    public string? Degree { get; set; }
    public string? Field { get; set; }
    public string? Institution { get; set; }
    public string? Year { get; set; }
}

/// <summary>
/// 🔒 MANUAL — where a CV is in the AI processing journey (like a parcel tracker).
/// Pending → waiting in line · Processing → the AI is reading it · Ready → searchable ·
/// Failed → see Error, press "Retry".
/// </summary>
public enum ProcessingStatus
{
    Pending = 0,
    Processing = 1,
    Ready = 2,
    Failed = 3,
}
