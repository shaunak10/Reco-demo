using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Security;
using ReconAI.Data;

namespace ReconAI.Web.Services;

public static class AppClaims
{
    public const string TenantId = "tenant_id";
    public const string TenantName = "tenant_name";
}

public sealed record SignInResult(bool Succeeded, string? Error, ClaimsPrincipal? Principal);

/// <summary>
/// Demo sign-in: PBKDF2 verification, a short lockout after repeated failures, a uniform error message so the
/// response does not reveal which emails exist, and an audit event for every outcome of a known account.
/// </summary>
public sealed class AuthService(ReconDbContext db, TimeProvider clock, ILogger<AuthService> log)
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);
    private const string InvalidCredentials = "Invalid email or password.";

    public async Task<SignInResult> SignInAsync(string? email, string? password)
    {
        var normalized = (email ?? "").Trim().ToLowerInvariant();
        var now = clock.GetUtcNow().UtcDateTime;

        // Login is the one place that must look across tenants, so the tenant filter is bypassed here (and only here).
        var user = normalized.Length == 0
            ? null
            : await db.Users.IgnoreQueryFilters().Include(u => u.Tenant).FirstOrDefaultAsync(u => u.Email == normalized);

        if (user is null || !user.IsActive)
        {
            PasswordHasher.Verify(password ?? "", PasswordHasher.DummyHash);   // keep timing similar
            log.LogWarning("Sign-in failed for an unknown or inactive account");
            return new SignInResult(false, InvalidCredentials, null);
        }

        if (user.LockoutEndUtc is { } lockedUntil && lockedUntil > now)
        {
            Audit(user, "LoginBlocked", "Account temporarily locked after repeated failures", now);
            await db.SaveChangesAsync();
            return new SignInResult(false, "Too many failed attempts. Please try again in a few minutes.", null);
        }

        if (!PasswordHasher.Verify(password ?? "", user.PasswordHash))
        {
            user.FailedLoginCount++;
            var detail = $"Failed attempt {user.FailedLoginCount} of {MaxFailedAttempts}";
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockoutEndUtc = now + LockoutDuration;
                user.FailedLoginCount = 0;
                detail += $"; locked until {user.LockoutEndUtc:HH:mm} UTC";
            }
            Audit(user, "LoginFailed", detail, now);
            await db.SaveChangesAsync();
            return new SignInResult(false, InvalidCredentials, null);
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        Audit(user, "LoginSucceeded", $"Signed in as {user.Role}", now);
        await db.SaveChangesAsync();
        return new SignInResult(true, null, BuildPrincipal(user));
    }

    public async Task SignOutAuditAsync(ClaimsPrincipal principal)
    {
        if (!int.TryParse(principal.FindFirstValue(AppClaims.TenantId), out var tenantId)) return;
        int.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId);
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = tenantId,
            UserId = userId == 0 ? null : userId,
            Actor = principal.FindFirstValue(ClaimTypes.Email) ?? "",
            Action = "Logout",
            EntityType = nameof(DemoUser),
            EntityId = userId.ToString(),
            OccurredUtc = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync();
    }

    private void Audit(DemoUser user, string action, string detail, DateTime now) =>
        db.AuditEvents.Add(new AuditEvent
        {
            TenantId = user.TenantId,
            UserId = user.Id,
            Actor = user.Email,
            Action = action,
            EntityType = nameof(DemoUser),
            EntityId = user.Id.ToString(),
            Detail = detail,
            OccurredUtc = now,
        });

    private static ClaimsPrincipal BuildPrincipal(DemoUser user)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, Roles.ToClaim(user.Role)),
            new Claim(AppClaims.TenantId, user.TenantId.ToString()),
            new Claim(AppClaims.TenantName, user.Tenant?.Name ?? ""),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}
