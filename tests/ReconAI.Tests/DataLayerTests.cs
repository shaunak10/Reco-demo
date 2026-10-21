using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Importing;
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

    // ---------------------------------------------------------------- Step 3: imported rows and money
    private SourceFile NewFile(int tenantId, string sha) => new()
    {
        TenantId = tenantId, Kind = SourceKind.Ledger, OriginalFileName = "f.csv", Format = "csv", Sha256 = sha, StoredPath = $"{tenantId}/{sha}.csv",
        Status = SourceFileStatus.Imported, UploadedByUserId = 1, UploadedByEmail = "a@demo.local", UploadedUtc = DateTime.UtcNow,
    };

    private static SourceRow NewRow(SourceFile file, int number, decimal? amount, decimal? fees = null) => new()
    {
        TenantId = file.TenantId, SourceFile = file, Kind = file.Kind, RowNumber = number, RowKey = $"L-x-{number:D6}", RowSha256 = "h",
        ImportStatus = RowImportStatus.Valid, TransactionRef = $"R{number}", Amount = amount, Fees = fees, TxnDate = new DateOnly(2026, 10, 15),
    };

    [Fact]
    public void Money_round_trips_exactly_and_refuses_to_round()
    {
        Assert.Equal(59900L, Money.ToPaise(599.00m));
        Assert.Equal(1L, Money.ToPaise(0.01m));
        Assert.Equal(599.00m, Money.FromPaise(59900));
        Assert.Equal("599.00", Money.FromPaise(59900).ToString());       // scale 2 is preserved
        Assert.Throws<InvalidOperationException>(() => Money.ToPaise(599.005m));
        Assert.Throws<InvalidOperationException>(() => Money.ToPaise(0.001m));
    }

    [Fact]
    public async Task Amounts_are_stored_as_paise_and_sql_sums_are_exact()
    {
        int fileId;
        using (var db = Open(_tenantA))
        {
            var file = NewFile(_tenantA, "aa11");
            db.SourceFiles.Add(file);
            db.SourceRows.AddRange(NewRow(file, 1, 0.10m, 0.01m), NewRow(file, 2, 0.20m, 0.02m), NewRow(file, 3, 1234567.89m, 21849.13m));
            db.SaveChanges();
            fileId = file.Id;
        }

        using var read = Open(_tenantA);
        var rows = read.SourceRows.OrderBy(r => r.RowNumber).ToList();
        Assert.Equal([0.10m, 0.20m, 1234567.89m], rows.Select(r => r.Amount!.Value));   // exact round trip
        var sums = await read.Database.SqlQuery<long>($"SELECT COALESCE(SUM(Amount), 0) AS Value FROM SourceRows WHERE SourceFileId = {fileId}").SingleAsync();
        Assert.Equal(123456819L, sums);                                                    // 0.10 + 0.20 + 1,234,567.89 = 1,234,568.19 -> 123,456,819 paise, exactly
    }

    [Fact]
    public void A_row_that_would_need_rounding_cannot_be_saved()
    {
        using var db = Open(_tenantA);
        var file = NewFile(_tenantA, "bb22");
        db.SourceFiles.Add(file);
        db.SourceRows.Add(NewRow(file, 1, 10.005m));
        var ex = Assert.Throws<DbUpdateException>(() => db.SaveChanges());      // EF wraps the converter's refusal
        Assert.Contains("rounded", ex.InnerException?.Message);
    }

    [Fact]
    public void Imported_rows_and_mappings_are_append_only()
    {
        using (var seed = Open(_tenantA))
        {
            var file = NewFile(_tenantA, "cc33");
            seed.SourceFiles.Add(file);
            seed.SourceRows.Add(NewRow(file, 1, 100.00m));
            seed.SaveChanges();
        }
        using (var edit = Open(_tenantA))
        {
            edit.SourceRows.Single().Amount = 999.00m;
            Assert.Throws<InvalidOperationException>(() => edit.SaveChanges());
        }
        using (var delete = Open(_tenantA))
        {
            delete.SourceRows.Remove(delete.SourceRows.Single());
            Assert.Throws<InvalidOperationException>(() => delete.SaveChanges());
        }
        using var verify = Open(_tenantA);
        Assert.Equal(100.00m, verify.SourceRows.Single().Amount);
    }

    [Fact]
    public void The_database_itself_blocks_two_active_copies_of_the_same_file_per_tenant()
    {
        using (var db = Open(_tenantA))
        {
            db.SourceFiles.Add(NewFile(_tenantA, "dd44"));
            db.SaveChanges();
        }

        using (var dup = Open(_tenantA))
        {
            dup.SourceFiles.Add(NewFile(_tenantA, "dd44"));
            Assert.Throws<DbUpdateException>(() => dup.SaveChanges());               // unique (tenant, kind, checksum) where not voided
        }

        using (var other = Open(_tenantB))
        {
            other.SourceFiles.Add(NewFile(_tenantB, "dd44"));                         // another company may hold the same bytes
            other.SaveChanges();
        }

        using (var voidIt = Open(_tenantA))
        {
            voidIt.SourceFiles.Single(f => f.Sha256 == "dd44").IsVoided = true;      // voiding frees the checksum
            voidIt.SaveChanges();
            voidIt.SourceFiles.Add(NewFile(_tenantA, "dd44"));
            voidIt.SaveChanges();
            Assert.Equal(2, voidIt.SourceFiles.Count(f => f.Sha256 == "dd44"));
        }
    }

    [Fact]
    public void Row_numbers_are_unique_within_a_file()
    {
        using var db = Open(_tenantA);
        var file = NewFile(_tenantA, "ee55");
        db.SourceFiles.Add(file);
        db.SourceRows.AddRange(NewRow(file, 1, 1.00m), NewRow(file, 1, 2.00m));
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
