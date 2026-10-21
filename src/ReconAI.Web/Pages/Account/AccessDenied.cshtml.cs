using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ReconAI.Web.Pages.Account;

[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public void OnGet() => Response.StatusCode = StatusCodes.Status403Forbidden;
}
