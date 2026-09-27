namespace TalentLens.Infrastructure.Data;

/// <summary>
/// The company of the recruiter making the current request (null for admins, job seekers,
/// anonymous visitors and background jobs). AppDbContext uses it to hide other companies'
/// private data (talent pool, searches).
/// </summary>
public interface ICurrentCompany
{
    Guid? CompanyId { get; }
}

/// <summary>Used outside recruiter requests (design-time tools, background jobs).</summary>
public sealed class NoCompany : ICurrentCompany
{
    public Guid? CompanyId => null;
}
