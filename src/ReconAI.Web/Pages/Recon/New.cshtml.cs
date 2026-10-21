using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReconAI.Core.Importing;
using ReconAI.Data;
using ReconAI.Web.Services;

namespace ReconAI.Web.Pages.Recon;

// Body limit is a little above the 5 MB file limit so an oversized file gets a clear message instead of a bare HTTP 413.
[RequestSizeLimit(12 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 12 * 1024 * 1024)]
public class NewModel(ImportService import, SampleData samples) : PageModel
{
    public static readonly SourceKind[] Kinds = [SourceKind.Ledger, SourceKind.Gateway, SourceKind.Bank];

    public IReadOnlyList<SourceFile> Files { get; private set; } = [];
    public IReadOnlyList<SampleScenario> Samples { get; private set; } = [];
    public Dictionary<SourceKind, string> Errors { get; } = new();

    [TempData] public string? Notice { get; set; }

    /// <summary>The newest file of this type that has not been voided.</summary>
    public SourceFile? Latest(SourceKind kind) => Files.FirstOrDefault(f => f.Kind == kind && !f.IsVoided);

    public static string Hint(SourceKind kind) => kind switch
    {
        SourceKind.Ledger => "Your internal orders: one row per recharge with the order reference, amount and date.",
        SourceKind.Gateway => "The payment gateway's statement: payment reference, gross amount, fee, payout reference (UTR) and date.",
        _ => "The bank statement: credit date, bank reference (UTR) and credited amount.",
    };

    public static string ScenarioTitle(string name) => name switch
    {
        "scenario1_main" => "Scenario 1 - main demo, every exception type",
        "scenario2_clean" => "Scenario 2 - clean day, everything reconciles",
        "scenario3_changed_headers" => "Scenario 3 - same data, different headings and formats",
        _ => name,
    };

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostUploadAsync(SourceKind kind, IFormFile? file, CancellationToken ct)
    {
        if (!Enum.IsDefined(kind)) return BadRequest();
        var outcome = await import.UploadAsync(kind, file, User, ct);
        if (outcome.Ok) return RedirectToPage("Mapping", new { id = outcome.FileId });

        Errors[kind] = outcome.Error ?? "The upload failed.";
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Downloads a synthetic dataset file (whitelisted against the scenario folders; Development only).</summary>
    public IActionResult OnGetSample(string? scenario, string? file)
    {
        var path = samples.Resolve(scenario, file);
        return path is null ? NotFound() : PhysicalFile(path, "application/octet-stream", file!);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Files = await import.ListAsync(includeVoided: true, ct);
        Samples = samples.Scenarios();
    }
}
