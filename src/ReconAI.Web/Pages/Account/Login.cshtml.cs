using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReconAI.Core.Security;
using ReconAI.Data;
using ReconAI.Web.Services;

namespace ReconAI.Web.Pages.Account;

[AllowAnonymous]
public class LoginModel(AuthService auth, IConfiguration config) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    /// <summary>Development convenience: one-click demo sign-in. Credentials stay server-side and are never rendered.</summary>
    public bool QuickLogin => config.GetValue<bool>("Demo:QuickLogin");

    public class InputModel
    {
        [Required, EmailAddress, StringLength(256)]
        public string Email { get; set; } = "";

        [Required, StringLength(128), DataType(DataType.Password)]
        public string Password { get; set; } = "";
    }

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true ? LocalRedirect("/") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();
        return await SignInAsync(Input.Email, Input.Password);
    }

    public async Task<IActionResult> OnPostDemoAsync(string role)
    {
        if (!QuickLogin) return NotFound();
        if (!Enum.TryParse<UserRole>(role, out var wanted)) return BadRequest();
        var user = (config.GetSection("DemoSeed:Users").Get<List<DemoSeedUser>>() ?? []).FirstOrDefault(u => u.Role == wanted);
        if (user is null) return NotFound();
        return await SignInAsync(user.Email, user.Password);
    }

    private async Task<IActionResult> SignInAsync(string email, string password)
    {
        var result = await auth.SignInAsync(email, password);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Sign-in failed.");
            return Page();
        }
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, result.Principal!);
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
