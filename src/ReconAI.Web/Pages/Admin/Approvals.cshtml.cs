using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReconAI.Core.Security;

namespace ReconAI.Web.Pages.Admin;

/// <summary>
/// Rule approval and activation. Company Admin only - enforced twice: by the /Admin folder convention in
/// Program.cs and by the attribute below, so moving the page cannot silently open it to Operators.
/// </summary>
[Authorize(Policy = Policies.CompanyAdminOnly)]
public class ApprovalsModel : PageModel
{
    public void OnGet() { }

    // The approve/reject operations. Rule storage and the Draft -> Tested -> Approved -> Active lifecycle arrive in
    // Step 5, so no rule can exist yet and both report 404 - but only AFTER the Admin-only authorization check.
    public IActionResult OnPostApprove(int id) => NotFound();

    public IActionResult OnPostReject(int id) => NotFound();
}
