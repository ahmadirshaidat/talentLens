using Microsoft.EntityFrameworkCore;

namespace TalentLens.Infrastructure.Data;

/// <summary>
/// 🔒 MANUAL — EF Core context: DbSets for the Domain entities, relationships,
/// and workspace (tenant) global query filters.
/// Change the base class (e.g. IdentityDbContext) as your design needs.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    /// <summary>Configure entities, relationships, and per-workspace query filters.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        throw new NotImplementedException("MANUAL: entity relationships + workspace global query filters");
    }
}
