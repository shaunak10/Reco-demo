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

        ApplyTenantFilter<DemoUser>(b);
        ApplyTenantFilter<AuditEvent>(b);
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

    /// <summary>Audit rows are immutable, and no write may cross a tenant boundary.</summary>
    private void Guard()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("AuditEvent rows are append-only and cannot be modified or deleted.");

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
