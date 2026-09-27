using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.AiService;
using TalentLens.Infrastructure.Data;
using TalentLens.Infrastructure.Identity;
using TalentLens.Web.Services;

namespace TalentLens.Tests;

/// <summary>Company (tenant) isolation, public job visibility and profile mapping (SQLite in-memory).</summary>
public sealed class CompanyIsolationTests : IDisposable
{
    private sealed class FixedCompany(Guid? id) : ICurrentCompany
    {
        public Guid? CompanyId { get; } = id;
    }

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Guid _coA = Guid.NewGuid();
    private readonly Guid _coB = Guid.NewGuid();
    private readonly Guid _coPending = Guid.NewGuid();

    public CompanyIsolationTests()
    {
        _connection.Open();
        using var db = Context(null);
        db.Database.EnsureCreated();
        db.Companies.AddRange(
            new Company { Id = _coA, Name = "Alpha Soft", Status = CompanyStatus.Active },
            new Company { Id = _coB, Name = "Beta Labs", Status = CompanyStatus.Active },
            new Company { Id = _coPending, Name = "Pending Co", Status = CompanyStatus.Pending });
        db.Candidates.AddRange(NewCandidate(_coA, "Alice"), NewCandidate(_coB, "Bob"));
        db.Jobs.AddRange(
            NewJob(_coA, "Open job", JobStatus.Open),
            NewJob(_coA, "Draft job", JobStatus.Draft),
            NewJob(_coPending, "Pending company job", JobStatus.Open));
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext Context(Guid? company) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options, new FixedCompany(company));

    private static Candidate NewCandidate(Guid co, string name) => new()
    {
        CompanyId = co, FullName = name, OriginalFileName = $"{name}.pdf", StoredFileName = $"{name}.pdf",
        ContentType = "application/pdf", Skills = ["Python"], Education = [new Education { Degree = "B.Sc." }],
    };

    private static Job NewJob(Guid co, string title, JobStatus status) => new()
    {
        CompanyId = co, Title = title, Description = "d", Status = status, Skills = ["Python"],
        PublishedAt = status == JobStatus.Open ? DateTime.UtcNow : null,
    };

    [Fact]
    public async Task Recruiters_only_see_their_own_talent_pool()
    {
        await using var db = Context(_coA);
        Assert.Equal(["Alice"], await db.Candidates.Select(c => c.FullName).ToListAsync());
    }

    [Fact]
    public async Task No_company_sees_no_private_data_unless_filters_are_ignored()
    {
        await using var db = Context(null);
        Assert.Empty(await db.Candidates.ToListAsync());
        Assert.Equal(2, await db.Candidates.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Saving_into_another_company_is_refused()
    {
        await using var db = Context(_coA);
        db.Candidates.Add(NewCandidate(_coB, "Mallory"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task New_private_rows_get_the_current_company()
    {
        await using var db = Context(_coA);
        var log = new SearchLog { Query = "python" };
        db.SearchLogs.Add(log);
        await db.SaveChangesAsync();
        Assert.Equal(_coA, log.CompanyId);
    }

    [Fact]
    public async Task Public_job_board_shows_only_open_jobs_of_active_companies()
    {
        await using var db = Context(null); // anonymous visitor
        var titles = await db.PublicJobs().Select(j => j.Title).ToListAsync();
        Assert.Equal(["Open job"], titles);
    }

    [Fact]
    public async Task Json_columns_round_trip()
    {
        await using var db = Context(_coA);
        var alice = await db.Candidates.SingleAsync();
        Assert.Equal(["Python"], alice.Skills);
        Assert.Equal("B.Sc.", Assert.Single(alice.Education).Degree);
    }

    [Fact]
    public async Task A_seeker_can_apply_only_once_per_job()
    {
        await using var db = Context(null);
        var user = new AppUser { UserName = "s@example.com", Email = "s@example.com" };
        db.Users.Add(user);
        var profile = new JobSeekerProfile { UserId = user.Id };
        db.SeekerProfiles.Add(profile);
        var job = await db.Jobs.FirstAsync(j => j.Title == "Open job");
        db.Applications.Add(new JobApplication { JobId = job.Id, SeekerProfileId = profile.Id });
        await db.SaveChangesAsync();

        db.Applications.Add(new JobApplication { JobId = job.Id, SeekerProfileId = profile.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}

public class ProfileAndMatchingTests
{
    private static CandidateProfileDto Extracted() => new(
        "Lina", "lina@example.com", "+962 79 000 0000", "Amman", 4, ["Python", "SQL"], ["Arabic"],
        ["Backend Developer"],
        [new Dictionary<string, string?> { ["degree"] = "B.Sc.", ["year"] = "2019" }]);

    [Fact]
    public void Talent_pool_mapping_overwrites_everything()
    {
        var c = new Candidate { OriginalFileName = "x", StoredFileName = "x", ContentType = "x", FullName = "old" };
        ProfileMapper.Overwrite(c, Extracted());
        Assert.Equal("Lina", c.FullName);
        Assert.Equal(["Python", "SQL"], c.Skills);
        Assert.Equal("2019", Assert.Single(c.Education).Year);
    }

    [Fact]
    public void Seeker_mapping_keeps_what_the_person_typed_and_merges_lists()
    {
        var p = new JobSeekerProfile { UserId = "u", FullName = "Lina H.", Phone = null, Skills = ["python", "Docker"] };
        ProfileMapper.FillMissing(p, Extracted());

        Assert.Equal("Lina H.", p.FullName);            // typed by the seeker → kept
        Assert.Equal("+962 79 000 0000", p.Phone);      // empty → filled
        Assert.Equal("Backend Developer", p.Headline);  // from first job title
        Assert.Equal(["python", "Docker", "SQL"], p.Skills); // merged, case-insensitive
    }

    [Fact]
    public void Recommendations_rank_by_skill_overlap_and_title()
    {
        var profile = new JobSeekerProfile
        {
            UserId = "u", Headline = "Backend Developer", Skills = ["Python", "Django"], YearsOfExperience = 6,
        };
        var jobs = new[]
        {
            new Job { Title = "Sales Manager", Description = "d", Skills = ["Sales"] },
            new Job { Title = "Python Backend Developer", Description = "d", Skills = ["Python", "Django"], MinYearsExperience = 5 },
            new Job { Title = "Data Analyst", Description = "d", Skills = ["Python"] },
        };

        var recs = JobMatchService.Recommend(profile, jobs, 5);

        Assert.Equal(["Python Backend Developer", "Data Analyst"], recs.Select(r => r.Job.Title));
        Assert.Equal(["Python", "Django"], recs[0].MatchedSkills);
    }

    [Fact]
    public void Job_search_query_includes_skills_and_years()
    {
        var job = new Job { Title = ".NET Developer", Description = "d", Skills = ["C#", "SQL Server"], MinYearsExperience = 3 };
        Assert.Equal(".NET Developer. C#, SQL Server. 3+ years of experience", job.ToSearchQuery());
    }

    [Fact]
    public void Profile_completeness_counts_filled_fields()
    {
        var p = new JobSeekerProfile { UserId = "u" };
        Assert.Equal(0, p.Completeness());
        p.FullName = "x";
        p.Skills = ["a"];
        Assert.Equal(20, p.Completeness());
    }

    [Fact]
    public void Company_initials()
    {
        Assert.Equal("NA", new Company { Name = "Nimbus Apps Group" }.Initials);
        Assert.Equal("م", new Company { Name = "مجموعة" }.Initials);
    }
}
