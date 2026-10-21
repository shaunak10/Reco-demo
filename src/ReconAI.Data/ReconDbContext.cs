using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Security;

namespace ReconAI.Data;

/// <summary>
/// Prototype database. Uses SQLite as a stand-in for SQL Server 2022 (SQL Server could not be installed without
/// admin rights); the table layout follows the architecture plan so only the provider and the money column type
/// change when moving to SQL Server.
/// </summary>
public class ReconDbContext : DbContext
{
    private readonly ITenantContext _tenant;

    public ReconDbContext(DbContextOptions<ReconDbContext> options, ITenantContext tenant) : base(options)
    {
        _tenant = tenant;
    }

    /// <summary>Referenced by the global query filters; EF re-evaluates it per context instance.</summary>
    public int? CurrentTenantId => _tenant.TenantId;

    public DbSet<DemoTenant> Tenants => Set<DemoTenant>();
    public DbSet<DemoUser> Users => Set<DemoUser>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SourceFile> SourceFiles => Set<SourceFile>();
    public DbSet<ColumnMapping> ColumnMappings => Set<ColumnMapping>();
    public DbSet<SourceRow> SourceRows => Set<SourceRow>();
    public DbSet<AIInferenceLog> AIInferenceLogs => Set<AIInferenceLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DemoTenant>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });

        b.Entity<DemoUser>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(120).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditEvent>(e =>
        {
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(64).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => new { x.TenantId, x.OccurredUtc });
        });

        b.Entity<SourceFile>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.OriginalFileName).HasMaxLength(260);
            e.Property(x => x.Format).HasMaxLength(8);
            e.Property(x => x.SheetName).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StoredPath).HasMaxLength(400);
            e.Property(x => x.UploadedByEmail).HasMaxLength(256);
            e.Property(x => x.VoidedByEmail).HasMaxLength(256);
            e.Property(x => x.VoidReason).HasMaxLength(300);
            // The same bytes can be active only once per source type; a voided file frees the checksum for a corrected re-import.
            // (SQLite syntax; on SQL Server the filter is [IsVoided] = 0.)
            e.HasIndex(x => new { x.TenantId, x.Kind, x.Sha256 }).IsUnique().HasFilter("\"IsVoided\" = 0");
            e.HasIndex(x => new { x.TenantId, x.UploadedUtc });
        });

        b.Entity<ColumnMapping>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.HeaderSignature).HasMaxLength(64).IsRequired();
            e.Property(x => x.Origin).HasMaxLength(32);
            e.Property(x => x.ConfirmedByEmail).HasMaxLength(256);
            e.HasOne(x => x.SourceFile).WithMany().HasForeignKey(x => x.SourceFileId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SourceFileId).IsUnique();
            e.HasIndex(x => new { x.TenantId, x.Kind, x.HeaderSignature });
        });

        b.Entity<SourceRow>(e =>
        {
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ImportStatus).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RowKey).HasMaxLength(40).IsRequired();
            e.Property(x => x.RowSha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.RejectReason).HasMaxLength(600);
            e.Property(x => x.TransactionRef).HasMaxLength(64);
            e.Property(x => x.SettlementRef).HasMaxLength(64);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Amount).HasConversion(Money.Converter);   // integer paise in SQLite
            e.Property(x => x.Fees).HasConversion(Money.Converter);
            e.HasOne(x => x.SourceFile).WithMany().HasForeignKey(x => x.SourceFileId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.SourceFileId, x.RowNumber }).IsUnique();
            e.HasIndex(x => x.RowKey);
            e.HasIndex(x => new { x.TenantId, x.Kind, x.TransactionRef });
            e.HasIndex(x => new { x.TenantId, x.Kind, x.SettlementRef });
        });

        b.Entity<AIInferenceLog>(e =>
        {
            e.Property(x => x.Task).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.UserEmail).HasMaxLength(256);
            e.Property(x => x.ModelName).HasMaxLength(80);
            e.Property(x => x.ModelVersion).HasMaxLength(40);
            e.Property(x => x.InputSummary).HasMaxLength(200);
            e.Property(x => x.Detail).HasMaxLength(400);
            e.HasIndex(x => new { x.TenantId, x.CreatedUtc });
            e.HasIndex(x => x.SourceFileId);
        });

        ApplyTenantFilter<DemoUser>(b);
        ApplyTenantFilter<AuditEvent>(b);
        ApplyTenantFilter<SourceFile>(b);
        ApplyTenantFilter<ColumnMapping>(b);
        ApplyTenantFilter<SourceRow>(b);
        ApplyTenantFilter<AIInferenceLog>(b);
    }

    /// <summary>Fail-closed tenant filter: with no tenant in context, no rows are visible.</summary>
    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantScoped
        => b.Entity<T>().HasQueryFilter(x => x.TenantId == CurrentTenantId);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Guard();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Guard();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Append-only records are immutable, and no write may cross a tenant boundary.</summary>
    private void Guard()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is IAppendOnly && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} rows are append-only and cannot be modified or deleted.");

            if (entry.Entity is not ITenantScoped scoped || entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            if (entry.State == EntityState.Added && scoped.TenantId == 0)
            {
                scoped.TenantId = CurrentTenantId
                    ?? throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} has no TenantId and there is no tenant in context.");
            }
            else if (CurrentTenantId is { } current && scoped.TenantId != current)
            {
                throw new InvalidOperationException($"Cross-tenant write blocked for {entry.Metadata.ClrType.Name}.");
            }
        }
    }
}
