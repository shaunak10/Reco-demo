using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Security;
using ReconAI.Data;

namespace ReconAI.Tests;

/// <summary>Tenant isolation and audit immutability, against a SQLite database built by the real EF migration.</summary>
public sealed class DataLayerTests : IDisposable
{
    private sealed class FixedTenant(int? id) : ITenantContext { public int? TenantId => id; }

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"reconai-data-{Guid.NewGuid():N}.db");
    private readonly int _tenantA, _tenantB;

    public DataLayerTests()
    {
        using var db = Open(null);
        db.Database.Migrate();                                  // proves the generated migration applies cleanly
        var a = new DemoTenant { Name = "Tenant A", CreatedUtc = DateTime.UtcNow };
        var b = new DemoTenant { Name = "Tenant B", CreatedUtc = DateTime.UtcNow };
        db.Tenants.AddRange(a, b);
        db.SaveChanges();
        _tenantA = a.Id; _tenantB = b.Id;
        db.Users.Add(User(_tenantA, "a@demo.local"));
        db.Users.Add(User(_tenantB, "b@demo.local"));
        db.SaveChanges();
    }

    private ReconDbContext Open(int? tenantId) =>
        new(new DbContextOptionsBuilder<ReconDbContext>().UseSqlite($"Data Source={_path}").Options, new FixedTenant(tenantId));

    private static DemoUser User(int tenantId, string email) => new()
    {
        TenantId = tenantId, Email = email, DisplayName = email, Role = UserRole.Operator,
        PasswordHash = PasswordHasher.Hash("x-Synthetic-1"), CreatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public void Each_tenant_sees_only_its_own_rows()
    {
        using var a = Open(_tenantA);
        using var b = Open(_tenantB);
        Assert.Equal(["a@demo.local"], a.Users.Select(u => u.Email).ToList());
        Assert.Equal(["b@demo.local"], b.Users.Select(u => u.Email).ToList());
    }

    [Fact]
    public void With_no_tenant_in_context_nothing_is_visible()
    {
        using var none = Open(null);
        Assert.Empty(none.Users);
        Assert.Empty(none.AuditEvents);
    }

    [Fact]
    public void Writing_into_another_tenant_is_blocked()
    {
        using var a = Open(_tenantA);
        a.Users.Add(User(_tenantB, "intruder@demo.local"));
        Assert.Throws<InvalidOperationException>(() => a.SaveChanges());
    }

    [Fact]
    public void New_rows_are_stamped_with_the_current_tenant()
    {
        using var a = Open(_tenantA);
        a.AuditEvents.Add(new AuditEvent { Action = "Probe", OccurredUtc = DateTime.UtcNow });
        a.SaveChanges();
        Assert.Equal(_tenantA, a.AuditEvents.Single(e => e.Action == "Probe").TenantId);
    }

    [Fact]
    public void A_row_with_no_tenant_and_no_context_is_refused()
    {
        using var none = Open(null);
        none.AuditEvents.Add(new AuditEvent { Action = "Orphan", OccurredUtc = DateTime.UtcNow });
        Assert.Throws<InvalidOperationException>(() => none.SaveChanges());
    }

    [Fact]
    public void Audit_events_cannot_be_modified_or_deleted()
    {
        using (var seed = Open(_tenantA))
        {
            seed.AuditEvents.Add(new AuditEvent { Action = "Original", OccurredUtc = DateTime.UtcNow });
            seed.SaveChanges();
        }

        using (var edit = Open(_tenantA))
        {
            edit.AuditEvents.Single(e => e.Action == "Original").Detail = "tampered";
            Assert.Throws<InvalidOperationException>(() => edit.SaveChanges());
        }
        using (var delete = Open(_tenantA))
        {
            delete.AuditEvents.Remove(delete.AuditEvents.Single(e => e.Action == "Original"));
            Assert.Throws<InvalidOperationException>(() => delete.SaveChanges());
        }
        using var verify = Open(_tenantA);
        Assert.Equal("", verify.AuditEvents.Single(e => e.Action == "Original").Detail);
    }

    [Fact]
    public async Task Seeding_is_idempotent()
    {
        var options = new DemoSeedOptions
        {
            Enabled = true, TenantName = "Seed Co",
            Users = [new DemoSeedUser { Email = "Seed@Demo.Local", DisplayName = "S", Role = UserRole.Operator, Password = "x-Synthetic-1" }],
        };
        using var db = Open(null);
        await DemoSeeder.SeedAsync(db, options, TimeProvider.System);
        await DemoSeeder.SeedAsync(db, options, TimeProvider.System);

        Assert.Equal(1, db.Tenants.Count(t => t.Name == "Seed Co"));
        Assert.Equal(1, db.Users.IgnoreQueryFilters().Count(u => u.Email == "seed@demo.local"));   // emails are normalised
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
