using System.Net;
using ReconAI.Core.Security;
using ReconAI.Tests.Support;

namespace ReconAI.Tests;

/// <summary>
/// Step 2 exit test: "authenticated operator cannot access Admin approval operation".
/// These run against the real pipeline (cookie auth, antiforgery, policies) - not against mocks.
/// </summary>
public class AuthorizationTests(ReconWebFactory factory) : IClassFixture<ReconWebFactory>
{
    private async Task<HttpClient> SignedInAsync(UserRole role)
    {
        var client = factory.NewClient();
        var creds = factory.Seeded(role);
        var res = await client.LoginAsync(creds.Email, creds.Password);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);   // 302 to the dashboard = signed in
        return client;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Admin/Approvals")]
    [InlineData("/Admin/Audit")]
    public async Task Anonymous_requests_are_sent_to_the_sign_in_page(string url)
    {
        var res = await factory.NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/Account/Login", res.Location());
    }

    [Fact]
    public async Task Operator_reaches_the_dashboard()
    {
        var client = await SignedInAsync(UserRole.Operator);
        var res = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Welcome", await res.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/Admin/Approvals")]
    [InlineData("/Admin/Audit")]
    public async Task Operator_cannot_open_admin_pages(string url)
    {
        var client = await SignedInAsync(UserRole.Operator);
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", res.Location());

        var denied = await client.GetAsync(res.Location());
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    public async Task Operator_cannot_run_the_admin_approval_operation(string handler)
    {
        var client = await SignedInAsync(UserRole.Operator);
        var token = await client.TokenFromAsync("/");            // a perfectly valid antiforgery token

        var res = await client.PostFormAsync($"/Admin/Approvals?handler={handler}&id=1", token);

        // Blocked by authorization (denied redirect), not merely "rule not found" (404) and certainly not 200.
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", res.Location());
    }

    [Theory]
    [InlineData("Approve")]
    [InlineData("Reject")]
    public async Task Company_admin_passes_authorization_for_the_approval_operation(string handler)
    {
        var client = await SignedInAsync(UserRole.CompanyAdmin);
        var token = await client.TokenFromAsync("/");

        var res = await client.PostFormAsync($"/Admin/Approvals?handler={handler}&id=1", token);

        // Authorized, then 404 because no rule exists yet (rule storage arrives in Step 5).
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Company_admin_can_open_admin_pages()
    {
        var client = await SignedInAsync(UserRole.CompanyAdmin);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Audit")).StatusCode);
    }

    [Fact]
    public async Task Approval_operation_requires_an_antiforgery_token()
    {
        var client = await SignedInAsync(UserRole.CompanyAdmin);
        var res = await client.PostAsync("/Admin/Approvals?handler=Approve&id=1", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Navigation_shows_admin_links_only_to_admins()
    {
        var op = await (await (await SignedInAsync(UserRole.Operator)).GetAsync("/")).Content.ReadAsStringAsync();
        var admin = await (await (await SignedInAsync(UserRole.CompanyAdmin)).GetAsync("/")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("href=\"/Admin/", op);
        Assert.Contains("href=\"/Admin/Approvals\"", admin);
        Assert.Contains("href=\"/Admin/Audit\"", admin);
    }

    [Fact]
    public async Task Sign_out_ends_the_session()
    {
        var client = await SignedInAsync(UserRole.Operator);
        var token = await client.TokenFromAsync("/");
        var res = await client.PostFormAsync("/Account/Logout", token);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var after = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/Account/Login", after.Location());
    }

    [Fact]
    public async Task Pages_send_security_headers()
    {
        var res = await factory.NewClient().GetAsync("/Account/Login");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
    }
}
