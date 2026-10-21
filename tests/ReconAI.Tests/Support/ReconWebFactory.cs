using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReconAI.Core.Importing;
using ReconAI.Core.Security;
using ReconAI.Data;

namespace ReconAI.Tests.Support;

/// <summary>Hosts the real web app against a throw-away SQLite file, so each test class starts from a clean seeded database.</summary>
public sealed class ReconWebFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"reconai-test-{Guid.NewGuid():N}.db");
    private readonly string _uploads = Path.Combine(Path.GetTempPath(), $"reconai-uploads-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
        builder.UseSetting("Ai:BaseUrl", "http://127.0.0.1:9");   // closed port: tests never depend on the AI service
        builder.UseSetting("Storage:UploadsPath", _uploads);
    }

    public HttpClient NewClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public DemoSeedUser Seeded(UserRole role) =>
        (Services.GetRequiredService<IConfiguration>().GetSection("DemoSeed:Users").Get<List<DemoSeedUser>>() ?? [])
        .First(u => u.Role == role);

    /// <summary>Runs a block against a scoped DbContext with no tenant filter, for arranging and asserting.</summary>
    public async Task<T> WithDbAsync<T>(Func<ReconDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<ReconDbContext>());
    }

    public Task<int> AddUserAsync(string email, string password, UserRole role) => WithDbAsync(async db =>
    {
        var tenant = await db.Tenants.OrderBy(t => t.Id).FirstAsync();
        var user = new DemoUser
        {
            TenantId = tenant.Id, Email = email, DisplayName = email, Role = role,
            PasswordHash = PasswordHasher.Hash(password), CreatedUtc = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    });

    /// <summary>Creates a second company with one operator, to prove tenants cannot see each other's data.</summary>
    public Task<int> AddTenantUserAsync(string tenantName, string email, string password, UserRole role) => WithDbAsync(async db =>
    {
        var tenant = new DemoTenant { Name = tenantName, CreatedUtc = DateTime.UtcNow };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var user = new DemoUser
        {
            TenantId = tenant.Id, Email = email, DisplayName = email, Role = role,
            PasswordHash = PasswordHasher.Hash(password), CreatedUtc = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return tenant.Id;
    });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { /* temp file; best effort */ }
        try { if (Directory.Exists(_uploads)) Directory.Delete(_uploads, recursive: true); } catch (IOException) { }
    }

    public string UploadsPath => _uploads;
}

public static class WebTestHelpers
{
    private static readonly Regex TokenRegex = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    public static async Task<string> TokenFromAsync(this HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        res.EnsureSuccessStatusCode();
        var m = TokenRegex.Match(await res.Content.ReadAsStringAsync());
        return m.Success ? m.Groups[1].Value : throw new InvalidOperationException($"No antiforgery token on {url}");
    }

    public static async Task<HttpResponseMessage> LoginAsync(this HttpClient client, string email, string password)
    {
        var token = await client.TokenFromAsync("/Account/Login");
        return await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["__RequestVerificationToken"] = token,
        }));
    }

    public static async Task<HttpResponseMessage> PostFormAsync(this HttpClient client, string url, string token) =>
        await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

    /// <summary>The redirect target as a path + query, whether the server sent it relative or absolute.</summary>
    public static async Task<HttpResponseMessage> UploadAsync(this HttpClient client, SourceKind kind, string fileName, byte[] bytes, bool withToken = true)
    {
        var form = new MultipartFormDataContent();
        if (withToken) form.Add(new StringContent(await client.TokenFromAsync("/Recon/New")), "__RequestVerificationToken");
        form.Add(new StringContent(kind.ToString()), "kind");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync("/Recon/New?handler=Upload", form);
    }

    /// <summary>Posts the mapping form (handler Confirm or Preview) with one Fields value per column.</summary>
    public static async Task<HttpResponseMessage> PostMappingAsync(this HttpClient client, int fileId, string handler, IEnumerable<string> fields, string? tokenPage = null)
    {
        var token = await client.TokenFromAsync(tokenPage ?? $"/Recon/Mapping/{fileId}");
        var pairs = fields.Select(f => new KeyValuePair<string, string>("Fields", f))
            .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token));
        return await client.PostAsync($"/Recon/Mapping/{fileId}?handler={handler}", new FormUrlEncodedContent(pairs));
    }

    public static int FileIdFrom(this HttpResponseMessage res) => int.Parse(res.Location().Split('?')[0].Split('/').Last());

    public static string Location(this HttpResponseMessage res) =>
        res.Headers.Location is { } l ? (l.IsAbsoluteUri ? l.PathAndQuery : l.OriginalString) : "";
}
