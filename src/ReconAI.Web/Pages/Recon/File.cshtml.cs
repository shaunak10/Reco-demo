using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReconAI.Data;
using ReconAI.Web.Services;

namespace ReconAI.Web.Pages.Recon;

public class FileModel(ImportService import) : PageModel
{
    public const int PageSize = 50;

    public SourceFile? Source { get; private set; }
    public FileTotals Totals { get; private set; } = new();
    public ColumnMapping? Mapping { get; private set; }
    public IReadOnlyList<string> Headers { get; private set; } = [];
    public IReadOnlyList<(string Heading, string Field)> Assignments { get; private set; } = [];
    public IReadOnlyList<SourceRow> Rows { get; private set; } = [];
    public int TotalRows { get; private set; }
    public string? Error { get; private set; }

    /// <summary>all | valid | rejected</summary>
    [BindProperty(SupportsGet = true)] public string Show { get; set; } = "all";
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    [TempData] public string? Notice { get; set; }

    public int Pages => Math.Max(1, (TotalRows + PageSize - 1) / PageSize);

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostVoidAsync(int id, string? reason, CancellationToken ct)
    {
        var file = await import.FindAsync(id, ct);
        if (file is null) return NotFound();

        Error = await import.VoidAsync(file, reason, User, ct);
        if (Error is null)
        {
            TempData["Notice"] = $"File #{id} was voided. Its rows stay on record for audit, and the same file can now be uploaded again.";
            return RedirectToPage("New");
        }
        await LoadAsync(id, ct);
        return Page();
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        Source = await import.FindAsync(id, ct);
        if (Source is null) return false;

        Headers = import.HeadersOf(Source);
        Mapping = await import.MappingOfAsync(id, ct);
        if (Mapping is not null)
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string?>>(Mapping.MappingJson) ?? [];
            Assignments = Headers.Select(h => (h, map.TryGetValue(h, out var f) ? f ?? "" : "")).ToList();
        }

        if (Source.Status == SourceFileStatus.Imported)
        {
            Totals = await import.TotalsAsync(Source, ct);
            RowImportStatus? filter = Show switch { "valid" => RowImportStatus.Valid, "rejected" => RowImportStatus.Rejected, _ => null };
            P = Math.Max(1, P);
            (Rows, TotalRows) = await import.RowsAsync(id, filter, P, PageSize, ct);
        }
        return true;
    }

    public static IReadOnlyList<string> RawCells(SourceRow row) => JsonSerializer.Deserialize<string[]>(row.RawJson) ?? [];
}
