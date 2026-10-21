using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReconAI.Web.Services;

namespace ReconAI.Web.Pages.Account;

public class LogoutModel(AuthService auth) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await auth.SignOutAuditAsync(User);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToPage("/Account/Login");
    }
}
