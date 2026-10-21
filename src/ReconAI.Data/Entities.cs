using ReconAI.Core.Security;

namespace ReconAI.Data;

/// <summary>Every business record carries a TenantId; the DbContext enforces it on read and write.</summary>
public interface ITenantScoped
{
    int TenantId { get; set; }
}

/// <summary>Marks records that may be inserted but never updated or deleted (audit trail, imported rows, confirmed mappings).</summary>
public interface IAppendOnly
{
}

/// <summary>Tells the data layer which tenant the current request belongs to. Null means "no tenant" and fails closed.</summary>
public interface ITenantContext
{
    int? TenantId { get; }
}

public sealed class NoTenantContext : ITenantContext
{
    public int? TenantId => null;
}

public class DemoTenant
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

public class DemoUser : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public DemoTenant? Tenant { get; set; }

    /// <summary>Stored lower-case; unique across the whole demo.</summary>
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public UserRole Role { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// Append-only audit trail. <see cref="ReconDbContext"/> refuses to update or delete rows of this type.
/// Never put source financial values or secrets in <see cref="Detail"/>.
/// </summary>
public class AuditEvent : ITenantScoped, IAppendOnly
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int? UserId { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime OccurredUtc { get; set; }
}
