using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.Identity;

namespace TalentLens.Infrastructure.Data;

/// <summary>
/// 🔒 MANUAL — EF Core context: tables, relationships, and company (tenant) query filters.
///
/// 🧸 ELI5
/// The DbContext is the "librarian" between C# and SQL Server.
///   - Each DbSet is a shelf (table): Companies, Jobs, Applications, Seeker profiles…
///     plus the Identity shelves (users, roles) that IdentityDbContext adds for us.
///   - OnModelCreating is the rule book: how shelves connect, text lengths, indexes.
///   - GLOBAL QUERY FILTERS protect PRIVATE company data. Every time code asks for the
///     talent pool (Candidates) or SearchLogs, the librarian silently adds
///         WHERE CompanyId = &lt;the signed-in recruiter's company&gt;
///     If nobody from a company is signed in, it matches NOTHING (safe default).
///     Admin pages and background jobs that must see everything call IgnoreQueryFilters().
///   - Jobs are PUBLIC (anyone can read an open job), so they have no filter; the
///     employer pages always add "CompanyId == my company" themselves.
/// </summary>
public class AppDbContext : IdentityDbContext<AppUser>
{
    private readonly ICurrentCompany _currentCompany;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentCompany currentCompany)
        : base(options)
    {
        _currentCompany = currentCompany;
    }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<SearchLog> SearchLogs => Set<SearchLog>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobSeekerProfile> SeekerProfiles => Set<JobSeekerProfile>();
    public DbSet<JobApplication> Applications => Set<JobApplication>();
    public DbSet<SavedJob> SavedJobs => Set<SavedJob>();

    /// <summary>🧸 Read on EVERY query, so each request gets its own recruiter's company.</summary>
    private Guid? CurrentCompanyId => _currentCompany.CompanyId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // 🧸 Identity's own tables first

        modelBuilder.Entity<Company>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.Industry).HasMaxLength(100);
            e.Property(c => c.Size).HasMaxLength(50);
            e.Property(c => c.Location).HasMaxLength(200);
            e.Property(c => c.Website).HasMaxLength(300);
            e.Property(c => c.Description).HasMaxLength(4000);
            // 🧸 Enums stored as readable text ("Active") instead of numbers (1).
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(c => c.Status);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(200);
            // 🧸 Deleting a company must not silently delete its users → Restrict.
            e.HasOne(u => u.Company)
                .WithMany()
                .HasForeignKey(u => u.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Candidate>(e =>
        {
            e.Property(c => c.OriginalFileName).HasMaxLength(260);
            e.Property(c => c.StoredFileName).HasMaxLength(260);
            e.Property(c => c.ContentType).HasMaxLength(100);
            e.Property(c => c.UploadedByUserId).HasMaxLength(450);
            e.Property(c => c.Error).HasMaxLength(2000);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            ConfigureProfile(e);

            e.HasOne(c => c.Company)
                .WithMany(co => co.Candidates)
                .HasForeignKey(c => c.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.CompanyId, c.UploadedAt });
            e.HasIndex(c => new { c.CompanyId, c.Status });

            // 🔐 private talent pool
            e.HasQueryFilter(c => c.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<SearchLog>(e =>
        {
            e.Property(s => s.Query).HasMaxLength(2000);
            e.Property(s => s.UserId).HasMaxLength(450);
            e.Property(s => s.Scope).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(s => new { s.CompanyId, s.CreatedAt });
            e.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Cascade);

            // 🔐 private search history
            e.HasQueryFilter(s => s.CompanyId == CurrentCompanyId);
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.Property(j => j.Title).HasMaxLength(200);
            e.Property(j => j.Description).HasMaxLength(8000);
            e.Property(j => j.Requirements).HasMaxLength(4000);
            e.Property(j => j.Location).HasMaxLength(200);
            e.Property(j => j.Currency).HasMaxLength(10);
            e.Property(j => j.CreatedByUserId).HasMaxLength(450);
            e.Property(j => j.SalaryMin).HasPrecision(12, 2);
            e.Property(j => j.SalaryMax).HasPrecision(12, 2);
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.EmploymentType).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.WorkplaceType).HasConversion<string>().HasMaxLength(20);
            e.HasOne(j => j.Company)
                .WithMany(c => c.Jobs)
                .HasForeignKey(j => j.CompanyId)
                .OnDelete(DeleteBehavior.Cascade);
            // 🧸 The public job board asks "open jobs, newest first" all the time.
            e.HasIndex(j => new { j.Status, j.PublishedAt });
            e.HasIndex(j => new { j.CompanyId, j.Status });
        });

        modelBuilder.Entity<JobSeekerProfile>(e =>
        {
            e.Property(p => p.UserId).HasMaxLength(450);
            e.HasIndex(p => p.UserId).IsUnique(); // 🧸 one profile per seeker
            e.Property(p => p.Headline).HasMaxLength(200);
            e.Property(p => p.Summary).HasMaxLength(4000);
            e.Property(p => p.CvOriginalFileName).HasMaxLength(260);
            e.Property(p => p.CvStoredFileName).HasMaxLength(260);
            e.Property(p => p.CvContentType).HasMaxLength(100);
            e.Property(p => p.CvError).HasMaxLength(2000);
            e.Property(p => p.CvStatus).HasConversion<string>().HasMaxLength(20);
            ConfigureProfile(e);
            e.HasOne<AppUser>().WithOne().HasForeignKey<JobSeekerProfile>(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.IsSearchable);
        });

        modelBuilder.Entity<JobApplication>(e =>
        {
            e.Property(a => a.CoverLetter).HasMaxLength(4000);
            e.Property(a => a.RecruiterNote).HasMaxLength(2000);
            e.Property(a => a.MatchReason).HasMaxLength(2000);
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.OwnsMany(a => a.MatchEvidence, b => b.ToJson());
            e.HasOne(a => a.Job).WithMany(j => j.Applications)
                .HasForeignKey(a => a.JobId).OnDelete(DeleteBehavior.Cascade);
            // 🧸 SQL Server refuses two cascade paths to the same row, so deleting a
            // profile doesn't cascade here; seekers withdraw instead of vanishing.
            e.HasOne(a => a.SeekerProfile).WithMany(p => p.Applications)
                .HasForeignKey(a => a.SeekerProfileId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(a => new { a.JobId, a.SeekerProfileId }).IsUnique(); // apply once
            e.HasIndex(a => new { a.SeekerProfileId, a.AppliedAt });
        });

        modelBuilder.Entity<SavedJob>(e =>
        {
            e.HasKey(s => new { s.SeekerProfileId, s.JobId });
            e.HasOne(s => s.Job).WithMany().HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<JobSeekerProfile>().WithMany().HasForeignKey(s => s.SeekerProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>Column rules shared by everything that holds an AI-extracted profile.</summary>
    private static void ConfigureProfile<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> e)
        where T : class, IExtractedProfile
    {
        e.Property(p => p.FullName).HasMaxLength(200);
        e.Property(p => p.Email).HasMaxLength(256);
        e.Property(p => p.Phone).HasMaxLength(50);
        e.Property(p => p.Location).HasMaxLength(200);
        // 🧸 Lists of strings (skills…) are saved as a JSON array inside the row;
        // Education (small objects) is an "owned" JSON column.
        e.OwnsMany(p => p.Education, b => b.ToJson());
    }

    /// <summary>
    /// 🧸 Safety net when SAVING private company data: a new row without CompanyId gets the
    /// current recruiter's company; a row pointing at ANOTHER company is refused.
    /// </summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var current = CurrentCompanyId;
        if (current is not null)
        {
            foreach (var entry in ChangeTracker.Entries<ICompanyScoped>())
            {
                if (entry.State == EntityState.Added && entry.Entity.CompanyId == Guid.Empty)
                {
                    entry.Entity.CompanyId = current.Value;
                }
                else if (entry.State is EntityState.Added or EntityState.Modified
                         && entry.Entity.CompanyId != current.Value)
                {
                    throw new InvalidOperationException(
                        $"Refusing to save {entry.Entity.GetType().Name} for another company.");
                }
            }
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
