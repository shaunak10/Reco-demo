using System.Security.Claims;
using ReconAI.Data;

namespace ReconAI.Web.Services;

/// <summary>Resolves the tenant from the signed-in user's cookie claim; anonymous requests have no tenant.</summary>
public sealed class HttpTenantContext(IHttpContextAccessor accessor) : ITenantContext
{
    public int? TenantId =>
        int.TryParse(accessor.HttpContext?.User.FindFirstValue(AppClaims.TenantId), out var id) ? id : null;
}
