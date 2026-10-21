using System.Security.Claims;
using ReconAI.Data;

namespace ReconAI.Web.Services;

/// <summary>
/// Writes an audit event for the signed-in user. The event is added to the current unit of work and saved together
/// with the change it describes. Details must never contain source financial values or secrets.
/// </summary>
public sealed class AuditLog(ReconDbContext db, TimeProvider clock)
{
    public void Add(ClaimsPrincipal user, string action, string entityType, string entityId, string detail)
    {
        _ = int.TryParse(user.FindFirstValue(AppClaims.TenantId), out var tenantId);
        int? userId = int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : null;
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            UserId = userId,
            Actor = user.FindFirstValue(ClaimTypes.Email) ?? "",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Detail = detail.Length > 1000 ? detail[..1000] : detail,
            OccurredUtc = clock.GetUtcNow().UtcDateTime,
        });
    }
}
