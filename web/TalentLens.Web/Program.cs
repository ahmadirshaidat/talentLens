using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalentLens.Infrastructure.AiService;
using TalentLens.Infrastructure.Data;
using TalentLens.Infrastructure.Identity;
using TalentLens.Infrastructure.Security;
using TalentLens.Infrastructure.Storage;
using TalentLens.Web.Localization;
using TalentLens.Web.Security;
using TalentLens.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

// ---- MVC + antiforgery on every POST ----
builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");
builder.Services.AddSingleton<UiText>();

// ---- Database (company-filtered private data) ----
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentCompany, HttpCurrentCompany>();
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlServer(connectionString));

// ---- Identity (cookie auth) ----
builder.Services
    .AddIdentity<AppUser, IdentityRole>(o =>
    {
        o.User.RequireUniqueEmail = true;
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/Account/Login";
    o.AccessDeniedPath = "/Account/AccessDenied";
    o.SlidingExpiration = true;
});

// ---- AI service client ----
builder.Services.Configure<AiServiceOptions>(builder.Configuration.GetSection(AiServiceOptions.SectionName));
builder.Services.AddTransient<AiServiceHandler>(); // adds X-Api-Key + X-Request-ID
builder.Services.AddHttpClient<AiServiceClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<AiServiceOptions>>().Value;
    http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
}).AddHttpMessageHandler<AiServiceHandler>();

// ---- File storage ----
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddSingleton<IFileStorage>(sp =>
{
    var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    return new LocalFileStorage(Path.Combine(env.ContentRootPath, options.UploadsPath));
});

// ---- Upload security: validation + malware scan hook ----
builder.Services.Configure<MalwareScanOptions>(builder.Configuration.GetSection(MalwareScanOptions.SectionName));
var scanOptions = builder.Configuration.GetSection(MalwareScanOptions.SectionName).Get<MalwareScanOptions>()
                  ?? new MalwareScanOptions();
if (scanOptions.IsClamAv)
    builder.Services.AddSingleton<IMalwareScanner, ClamAvMalwareScanner>();
else
    builder.Services.AddSingleton<IMalwareScanner, NoOpMalwareScanner>();
builder.Services.AddScoped<UploadScreener>();

// ---- Rate limiting (per user, or per IP when anonymous) ----
builder.Services.AddAppRateLimiting(builder.Configuration);

// ---- App services + background jobs ----
builder.Services.AddScoped<CvSearchService>();
builder.Services.AddScoped<ApplicantRankingService>();
builder.Services.AddScoped<JobMatchService>();
builder.Services.AddScoped<CandidateIngestionJob>();
builder.Services.AddScoped<SeekerCvIngestionJob>();
builder.Services.AddScoped<AiCleanupJob>();
builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));
builder.Services.AddHangfireServer(o =>
    o.WorkerCount = builder.Configuration.GetValue("Hangfire:WorkerCount", 2));

// ---- Localization (English / Arabic with RTL) ----
builder.Services.AddLocalization();

var app = builder.Build();

var aiKey = app.Configuration[$"{AiServiceOptions.SectionName}:ApiKey"];
app.Logger.LogInformation("AI service {Url}: service key {KeyState}",
    app.Configuration[$"{AiServiceOptions.SectionName}:BaseUrl"],
    string.IsNullOrEmpty(aiKey) ? "NOT configured" : $"configured ({aiKey.Length} chars)");
if (!app.Environment.IsDevelopment() && !scanOptions.IsClamAv)
{
    app.Logger.LogWarning("Uploads are NOT malware-scanned (Security:MalwareScanning:Provider = {Provider})",
        scanOptions.Provider);
}
if (!app.Environment.IsDevelopment() && string.IsNullOrEmpty(aiKey))
{
    app.Logger.LogWarning("AiService:ApiKey is not set; the AI service will reject calls if it requires a key");
}

using (var scope = app.Services.CreateScope())
{
    if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
        scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
    await SeedData.RunAsync(scope.ServiceProvider, app.Configuration, app.Logger);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseRequestLocalization(o => o
    .SetDefaultCulture(UiText.English)
    .AddSupportedCultures(UiText.English, UiText.Arabic)
    .AddSupportedUICultures(UiText.English, UiText.Arabic));
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter(); // after auth so limits can be per signed-in user

app.MapStaticAssets();
app.MapControllerRoute(
        name: "areas",
        pattern: "{area:exists}/{controller=Dashboard}/{action=Index}/{id?}")
    .WithStaticAssets();
app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

if (app.Environment.IsDevelopment())
{
    // Background-job dashboard (admins only, dev only): it shows every company's jobs.
    app.MapHangfireDashboard("/hangfire", new DashboardOptions
    {
        Authorization = [new SignedInDashboardFilter()],
    });
}

app.Run();

internal sealed class SignedInDashboardFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) =>
        context.GetHttpContext().User.IsInRole(TalentLens.Domain.Entities.AppRoles.Admin);
}
