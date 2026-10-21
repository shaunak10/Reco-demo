using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Security;
using ReconAI.Data;
using ReconAI.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Database (SQLite stand-in for SQL Server; the file lives under App_Data and is never committed) ----
var csb = new SqliteConnectionStringBuilder(builder.Configuration.GetConnectionString("Default") ?? "Data Source=App_Data/reconai.db");
if (csb.DataSource != ":memory:" && !Path.IsPathRooted(csb.DataSource))
    csb.DataSource = Path.Combine(builder.Environment.ContentRootPath, csb.DataSource);
if (csb.DataSource != ":memory:")
    Directory.CreateDirectory(Path.GetDirectoryName(csb.DataSource)!);

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
builder.Services.AddDbContext<ReconDbContext>(o => o.UseSqlite(csb.ToString()));
builder.Services.AddScoped<AuthService>();

// ---- AI service (localhost only). The deterministic engine must keep working when this is down. ----
builder.Services.AddHttpClient<AiHealthClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Ai:BaseUrl"] ?? "http://127.0.0.1:8100");
    c.Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Ai:HealthTimeoutSeconds", 2));
});

// ---- AuthN / AuthZ: secure by default (fallback policy = signed in), admin-only operations via policy ----
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.Cookie.Name = "reconai.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });

builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Policies.CompanyAdminOnly, p => p.RequireRole(Roles.CompanyAdmin));
    o.AddPolicy(Policies.OperatorOrAdmin, p => p.RequireRole(Roles.Operator, Roles.CompanyAdmin));
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin", Policies.CompanyAdminOnly);
});

var app = builder.Build();

// ---- Migrate and seed the demo tenant/users ----
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ReconDbContext>();
    await db.Database.MigrateAsync();
    var seed = app.Configuration.GetSection("DemoSeed").Get<DemoSeedOptions>() ?? new DemoSeedOptions();
    await DemoSeeder.SeedAsync(db, seed, TimeProvider.System);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'; frame-ancestors 'none'; form-action 'self'";
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();

// Lets the test project host the app with WebApplicationFactory<Program>.
public partial class Program;
