using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TalentLens.Domain.Entities;
using TalentLens.Infrastructure.Data;
using TalentLens.Infrastructure.Identity;

namespace TalentLens.Web.Services;

/// <summary>
/// Startup seeding: roles (always), the platform admin (from configuration, never from
/// code), and optional demo companies + jobs so the job board isn't empty on day one.
///
/// Configure the admin with user-secrets or environment variables:
///   dotnet user-secrets set "Seed:AdminEmail" "admin@example.com"
///   dotnet user-secrets set "Seed:AdminPassword" "..."
/// </summary>
public static class SeedData
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in AppRoles.All)
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));
        }

        await SeedAdminAsync(services, config, logger);

        if (config.GetValue("Seed:DemoData", false))
            await SeedDemoJobsAsync(services.GetRequiredService<AppDbContext>(), logger);
    }

    private static async Task SeedAdminAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogInformation("No Seed:AdminEmail/Seed:AdminPassword configured; skipping admin seed");
            return;
        }

        var users = services.GetRequiredService<UserManager<AppUser>>();
        var admin = await users.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new AppUser { UserName = email, Email = email, DisplayName = "Platform Admin", EmailConfirmed = true };
            var result = await users.CreateAsync(admin, password);
            if (!result.Succeeded)
            {
                logger.LogError("Admin seed failed: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }
            logger.LogInformation("Seeded platform admin {Email}", email);
        }
        if (!await users.IsInRoleAsync(admin, AppRoles.Admin))
            await users.AddToRoleAsync(admin, AppRoles.Admin);
    }

    /// <summary>Fake companies and jobs (invented names) for demos.</summary>
    private static async Task SeedDemoJobsAsync(AppDbContext db, ILogger logger)
    {
        if (await db.Companies.AnyAsync()) return;

        Company Co(string name, string industry, string location, string size, string about) => new()
        {
            Name = name, Industry = industry, Location = location, Size = size,
            Description = about, Status = CompanyStatus.Active,
        };

        var nimbus = Co("Nimbus Apps", "Software", "Amman, Jordan", "51-200",
            "Nimbus Apps builds cloud products for retailers across the region.");
        var falcon = Co("BlueFalcon Systems", "FinTech", "Irbid, Jordan", "201-500",
            "Digital banking and payments platforms for banks in the Middle East.");
        var oasis = Co("مجموعة الواحة التجارية", "Retail", "عمّان، الأردن", "500+",
            "مجموعة تجارية رائدة في قطاع التجزئة والتوزيع.");
        var bright = Co("BrightAds", "Marketing", "Dubai, UAE", "11-50",
            "A performance marketing agency for Arabic and English brands.");

        var now = DateTime.UtcNow;
        Job J(Company c, string title, string description, string requirements, string location,
            int? years, string[] skills, EmploymentType type = EmploymentType.FullTime,
            WorkplaceType place = WorkplaceType.Onsite, decimal? min = null, decimal? max = null, int daysAgo = 1) => new()
        {
            Company = c, Title = title, Description = description, Requirements = requirements,
            Location = location, MinYearsExperience = years, Skills = skills.ToList(),
            EmploymentType = type, WorkplaceType = place, SalaryMin = min, SalaryMax = max,
            Currency = min is null ? null : "JOD", Status = JobStatus.Open,
            PublishedAt = now.AddDays(-daysAgo), CreatedAt = now.AddDays(-daysAgo),
        };

        db.Jobs.AddRange(
            J(nimbus, "Senior Python Backend Developer",
                "Join our platform team to design and build REST APIs that serve millions of requests.",
                "5+ years building Python web services. Django or FastAPI. PostgreSQL and Docker.",
                "Amman, Jordan", 5, ["Python", "Django", "PostgreSQL", "Docker", "REST APIs"],
                place: WorkplaceType.Hybrid, min: 1400, max: 2000, daysAgo: 1),
            J(nimbus, "Frontend React Developer",
                "Build fast, accessible interfaces for our retail dashboard in React and TypeScript.",
                "2+ years with React and TypeScript. Eye for design, Figma is a plus.",
                "Amman, Jordan", 2, ["React", "TypeScript", "JavaScript", "Figma"], daysAgo: 3),
            J(nimbus, "DevOps Engineer",
                "Own our Kubernetes clusters on AWS and our CI/CD pipelines.",
                "4+ years with Docker, Kubernetes, AWS and Linux. Python scripting.",
                "Remote", 4, ["Docker", "Kubernetes", "AWS", "Linux", "Python"],
                place: WorkplaceType.Remote, daysAgo: 6),
            J(falcon, ".NET Developer (C#)",
                "Develop secure ASP.NET Core services for our digital banking platform.",
                "3+ years in C#, ASP.NET Core and SQL Server. Microservices experience preferred.",
                "Irbid, Jordan", 3, ["C#", ".NET", "SQL Server", "Microservices", "Azure"],
                min: 1000, max: 1600, daysAgo: 2),
            J(falcon, "Data Scientist",
                "Build machine learning models for fraud detection and credit scoring.",
                "3+ years in machine learning with Python, Pandas and TensorFlow. Arabic NLP is a plus.",
                "Amman, Jordan", 3, ["Python", "Machine Learning", "Pandas", "TensorFlow", "SQL"],
                place: WorkplaceType.Hybrid, daysAgo: 4),
            J(falcon, "Java Backend Engineer",
                "Scale our payments engine built on Spring Boot microservices.",
                "5+ years with Java and Spring. MySQL and Redis.",
                "Irbid, Jordan", 5, ["Java", "Spring", "Microservices", "MySQL", "Redis"], daysAgo: 9),
            J(oasis, "محاسب أول",
                "إعداد القوائم المالية الشهرية ومتابعة الحسابات الدائنة والمدينة.",
                "خبرة 5 سنوات في المحاسبة. إتقان Excel. شهادة مهنية ميزة إضافية.",
                "عمّان، الأردن", 5, ["Accounting", "Excel"], daysAgo: 2),
            J(oasis, "أخصائي توظيف",
                "إدارة عملية التوظيف الكاملة واستقطاب الكفاءات لفروع المجموعة.",
                "خبرة 3 سنوات في التوظيف. مهارات تواصل ممتازة باللغتين العربية والإنجليزية.",
                "عمّان، الأردن", 3, ["Recruitment", "Communication"], daysAgo: 5),
            J(oasis, "مدير مبيعات",
                "قيادة فريق المبيعات وتحقيق أهداف النمو في السوق المحلي.",
                "خبرة 7 سنوات في المبيعات منها 3 سنوات في الإدارة.",
                "الزرقاء، الأردن", 7, ["Sales", "Leadership", "Customer Service"], daysAgo: 8),
            J(bright, "Digital Marketing Specialist",
                "Plan and run SEO and paid campaigns for regional brands.",
                "3+ years in digital marketing and SEO. Arabic and English; French is a plus.",
                "Dubai, UAE", 3, ["Marketing", "SEO", "Communication"],
                place: WorkplaceType.Hybrid, daysAgo: 3),
            J(bright, "UI/UX Designer",
                "Design campaign landing pages and mobile flows with our creative team.",
                "2+ years of product design. Strong Figma portfolio.",
                "Remote", 2, ["Figma", "Photoshop"], EmploymentType.Contract, WorkplaceType.Remote, daysAgo: 7),
            J(bright, "Marketing Intern",
                "Support the team with research, content and campaign reporting.",
                "Students or fresh graduates. Excel and good communication.",
                "Dubai, UAE", null, ["Marketing", "Excel", "Communication"],
                EmploymentType.Internship, daysAgo: 10));

        await db.SaveChangesAsync();
        logger.LogInformation("Seeded demo companies and jobs");
    }
}
