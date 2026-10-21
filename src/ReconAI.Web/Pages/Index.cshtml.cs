using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ReconAI.Data;
using ReconAI.Web.Services;

namespace ReconAI.Web.Pages;

public class IndexModel(ReconDbContext db, AiHealthClient ai) : PageModel
{
    public AiHealth Ai { get; private set; } = new(false, null, null, "");
    public int TenantUsers { get; private set; }
    public int AuditEvents { get; private set; }
    public DateTime? PreviousSignInUtc { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Ai = await ai.CheckAsync(ct);
        TenantUsers = await db.Users.CountAsync(ct);          // tenant filter applies automatically
        AuditEvents = await db.AuditEvents.CountAsync(ct);

        if (int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid))
        {
            PreviousSignInUtc = await db.AuditEvents
                .Where(a => a.UserId == uid && a.Action == "LoginSucceeded")
                .OrderByDescending(a => a.OccurredUtc)
                .Skip(1)
                .Select(a => (DateTime?)a.OccurredUtc)
                .FirstOrDefaultAsync(ct);
        }
    }
}
