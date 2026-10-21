using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Security;
using ReconAI.Data;

namespace ReconAI.Web.Pages.Admin;

[Authorize(Policy = Policies.CompanyAdminOnly)]
public class AuditModel(ReconDbContext db) : PageModel
{
    public IReadOnlyList<AuditEvent> Events { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Events = await db.AuditEvents                       // tenant filter applies automatically
            .OrderByDescending(a => a.OccurredUtc).ThenByDescending(a => a.Id)
            .Take(100)
            .ToListAsync(ct);
    }
}
