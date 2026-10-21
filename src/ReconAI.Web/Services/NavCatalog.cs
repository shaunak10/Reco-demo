namespace ReconAI.Web.Services;

/// <param name="Page">Razor page path; null while the screen is not built yet (shown disabled with its build step).</param>
public sealed record NavItem(string Title, string Group, string Icon, string? Page, int Step, bool AdminOnly = false);

/// <summary>Single source of truth for the sidebar. Later steps only need to set <c>Page</c> to enable an entry.</summary>
public static class NavCatalog
{
    public static readonly IReadOnlyList<NavItem> Items =
    [
        new("Dashboard",          "Operations",     "grid",   "/Index",             2),
        new("New reconciliation", "Operations",     "upload", null,                 3),
        new("Rule wizard",        "Operations",     "sliders", null,                5),
        new("Runs & results",     "Operations",     "check",  null,                 6),
        new("API connections",    "Integrations",   "plug",   null,                 8),
        new("Alerts",             "Integrations",   "bell",   null,                 9),
        new("Rule approvals",     "Administration", "shield", "/Admin/Approvals",   2, AdminOnly: true),
        new("Audit log",          "Administration", "list",   "/Admin/Audit",       2, AdminOnly: true),
    ];

    /// <summary>24x24 stroke icons (path data only).</summary>
    public static readonly IReadOnlyDictionary<string, string> Icons = new Dictionary<string, string>
    {
        ["grid"]    = "M4 4h6v6H4z M14 4h6v6h-6z M4 14h6v6H4z M14 14h6v6h-6z",
        ["upload"]  = "M12 16V4 M7 9l5-5 5 5 M4 20h16",
        ["sliders"] = "M4 7h10 M18 7h2 M4 17h2 M10 17h10 M14 5v4 M6 15v4",
        ["check"]   = "M5 12l4 4 10-10",
        ["plug"]    = "M9 15l6-6 M8 8l-2 2a3.5 3.5 0 005 5l2-2 M16 16l2-2a3.5 3.5 0 00-5-5l-2 2",
        ["bell"]    = "M6 16v-5a6 6 0 1112 0v5l2 2H4z M10 21h4",
        ["shield"]  = "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z M9 12l2 2 4-4",
        ["list"]    = "M8 6h12 M8 12h12 M8 18h12 M4 6h.01 M4 12h.01 M4 18h.01",
        ["logout"]  = "M10 4H5v16h5 M15 8l4 4-4 4 M9 12h10",
    };
}
