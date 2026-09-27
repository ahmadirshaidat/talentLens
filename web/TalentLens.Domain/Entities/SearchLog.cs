namespace TalentLens.Domain.Entities;

/// <summary>
/// 🔒 MANUAL — a record of one recruiter CV search, used for "recent searches".
///
/// 🧸 ELI5: a diary line: "Sara searched the seekers database for 'python developer' and
/// got 7 people".
/// </summary>
public class SearchLog : ICompanyScoped
{
    public long Id { get; set; }
    public Guid CompanyId { get; set; }
    public string? UserId { get; set; }
    public required string Query { get; set; }
    public SearchScope Scope { get; set; } = SearchScope.TalentPool;
    public int ResultCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Where a recruiter searches: their own uploaded CVs, or job seekers on the platform.</summary>
public enum SearchScope
{
    TalentPool = 0,
    Seekers = 1,
}
