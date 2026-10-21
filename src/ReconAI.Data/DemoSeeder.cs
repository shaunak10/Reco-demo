using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Security;

namespace ReconAI.Data;

public sealed class DemoSeedOptions
{
    public bool Enabled { get; set; }
    public string TenantName { get; set; } = "Demo Company (Synthetic)";
    public List<DemoSeedUser> Users { get; set; } = new();
}

public sealed class DemoSeedUser
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; }
    /// <summary>Synthetic demo password from configuration; only its PBKDF2 hash is stored.</summary>
    public string Password { get; set; } = "";
}

public static class DemoSeeder
{
    /// <summary>Idempotent: creates the demo tenant and users that do not exist yet.</summary>
    public static async Task SeedAsync(ReconDbContext db, DemoSeedOptions options, TimeProvider clock)
    {
        if (!options.Enabled) return;

        var tenant = await db.Tenants.OrderBy(t => t.Id).FirstOrDefaultAsync(t => t.Name == options.TenantName);
        if (tenant is null)
        {
            tenant = new DemoTenant { Name = options.TenantName, CreatedUtc = clock.GetUtcNow().UtcDateTime };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        }

        foreach (var u in options.Users)
        {
            var email = u.Email.Trim().ToLowerInvariant();
            if (email.Length == 0 || string.IsNullOrEmpty(u.Password)) continue;
            if (await db.Users.IgnoreQueryFilters().AnyAsync(x => x.Email == email)) continue;

            db.Users.Add(new DemoUser
            {
                TenantId = tenant.Id,
                Email = email,
                DisplayName = u.DisplayName,
                Role = u.Role,
                PasswordHash = PasswordHasher.Hash(u.Password),
                CreatedUtc = clock.GetUtcNow().UtcDateTime,
            });
        }
        await db.SaveChangesAsync();
    }
}
