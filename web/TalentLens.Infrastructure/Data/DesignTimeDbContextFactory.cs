using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TalentLens.Infrastructure.Data;

/// <summary>
/// Lets `dotnet ef migrations add ...` build the context without starting the web app.
/// The connection string only matters for `dotnet ef database update`; override it with
/// the TALENTLENS_DB environment variable.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("TALENTLENS_DB")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=TalentLens;Trusted_Connection=True;MultipleActiveResultSets=true";
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new AppDbContext(options, new NoCompany());
    }
}
