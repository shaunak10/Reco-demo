using System.Net;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Security;
using ReconAI.Data;
using ReconAI.Tests.Support;
using ReconAI.Web.Services;

namespace ReconAI.Tests;

public class SignInTests(ReconWebFactory factory) : IClassFixture<ReconWebFactory>
{
    private const string Invalid = "Invalid email or password.";

    [Fact]
    public async Task Unknown_email_and_wrong_password_give_the_same_message()
    {
        var email = $"known-{Guid.NewGuid():N}@demo.local";
        await factory.AddUserAsync(email, "Correct-Horse-9", UserRole.Operator);

        var wrongPassword = await (await factory.NewClient().LoginAsync(email, "nope")).Content.ReadAsStringAsync();
        var unknownEmail = await (await factory.NewClient().LoginAsync("nobody@demo.local", "nope")).Content.ReadAsStringAsync();

        Assert.Contains(Invalid, wrongPassword);
        Assert.Contains(Invalid, unknownEmail);
    }

    [Fact]
    public async Task Account_locks_after_five_failures_even_for_the_right_password()
    {
        var email = $"lock-{Guid.NewGuid():N}@demo.local";
        await factory.AddUserAsync(email, "Correct-Horse-9", UserRole.Operator);

        for (var i = 0; i < AuthService.MaxFailedAttempts; i++)
            Assert.Equal(HttpStatusCode.OK, (await factory.NewClient().LoginAsync(email, "wrong")).StatusCode);

        var client = factory.NewClient();
        var res = await client.LoginAsync(email, "Correct-Horse-9");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);                        // login page re-rendered, not a redirect
        Assert.Contains("Too many failed attempts", await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);   // still anonymous

        var actions = await factory.WithDbAsync(db => db.AuditEvents.IgnoreQueryFilters()
            .Where(a => a.Actor == email).Select(a => a.Action).ToListAsync());
        Assert.Equal(AuthService.MaxFailedAttempts, actions.Count(a => a == "LoginFailed"));
        Assert.Contains("LoginBlocked", actions);
    }

    [Fact]
    public async Task Successful_sign_in_is_audited_without_secrets()
    {
        var email = $"ok-{Guid.NewGuid():N}@demo.local";
        const string password = "Correct-Horse-9";
        await factory.AddUserAsync(email, password, UserRole.CompanyAdmin);

        var res = await factory.NewClient().LoginAsync(email, password);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var events = await factory.WithDbAsync(db => db.AuditEvents.IgnoreQueryFilters().Where(a => a.Actor == email).ToListAsync());
        var ev = Assert.Single(events);
        Assert.Equal("LoginSucceeded", ev.Action);
        Assert.NotEqual(0, ev.TenantId);
        Assert.DoesNotContain(password, ev.Detail);
    }

    [Fact]
    public async Task Passwords_are_stored_only_as_hashes()
    {
        var hashes = await factory.WithDbAsync(db => db.Users.IgnoreQueryFilters().Select(u => u.PasswordHash).ToListAsync());
        Assert.NotEmpty(hashes);
        Assert.All(hashes, h => Assert.StartsWith("v1.", h));
        Assert.DoesNotContain(hashes, h => h.Contains(factory.Seeded(UserRole.Operator).Password));
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("CompanyAdmin")]
    public async Task Demo_shortcut_signs_in_without_exposing_the_password(string role)
    {
        var client = factory.NewClient();
        var loginPage = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(factory.Seeded(Enum.Parse<UserRole>(role)).Password, loginPage);

        var token = await client.TokenFromAsync("/Account/Login");
        var res = await client.PostAsync("/Account/Login?handler=Demo", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["role"] = role, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Open_redirect_through_returnUrl_is_ignored()
    {
        var client = factory.NewClient();
        var creds = factory.Seeded(UserRole.Operator);
        var token = await client.TokenFromAsync("/Account/Login");
        var res = await client.PostAsync("/Account/Login?returnUrl=https://evil.example/steal", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = creds.Email, ["Input.Password"] = creds.Password, ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Location());
    }
}
