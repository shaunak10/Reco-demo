using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReconAI.Core.Importing;
using ReconAI.Data;
using ReconAI.Web.Services;

namespace ReconAI.Web.Pages.Recon;

public class MappingModel(ImportService import) : PageModel
{
    public SourceFile? Source { get; private set; }
    public MappingProposal Proposal { get; private set; } = new([], [], null, null, null);
    public IReadOnlyList<CanonicalField> Allowed => SourceSchema.Allowed(Source!.Kind);
    public IReadOnlyList<CanonicalField> Mandatory => SourceSchema.Mandatory(Source!.Kind);
    public string? Error { get; private set; }

    /// <summary>One entry per column, in file order: a canonical field name, or "" to ignore the column.</summary>
    [BindProperty] public string?[] Fields { get; set; } = [];

    /// <summary>The AI call whose suggestions the operator reviewed (set by the page script). Used only to record provenance.</summary>
    [BindProperty] public long? InferenceId { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        Source = await import.FindAsync(id, ct);
        if (Source is null) return NotFound();
        if (Source.IsVoided || Source.Status != SourceFileStatus.AwaitingMapping) return RedirectToPage("File", new { id });

        if (!await TryLoadProposalAsync(ct)) return Page();
        Fields = Proposal.Headers.Select((_, i) => Proposal.SavedFields?[i]?.ToString() ?? "").ToArray();
        return Page();
    }

    /// <summary>AJAX dry run: validates the mapping against every row without storing anything.</summary>
    public async Task<IActionResult> OnPostPreviewAsync(int id, CancellationToken ct)
    {
        var file = await import.FindAsync(id, ct);
        if (file is null) return NotFound();
        return new JsonResult(await import.PreviewAsync(file, Fields, ct));
    }

    /// <summary>AJAX: asks the local AI for a suggested mapping. Nothing is saved; the operator must confirm.</summary>
    public async Task<IActionResult> OnPostSuggestAsync(int id, CancellationToken ct)
    {
        var file = await import.FindAsync(id, ct);
        if (file is null || file.IsVoided || file.Status != SourceFileStatus.AwaitingMapping) return NotFound();

        var r = await import.SuggestAsync(file, User, ct);
        static string LabelOf(CanonicalField? f) => f is { } v ? SourceSchema.Label(v) : "Ignore this column";
        return new JsonResult(new
        {
            available = r.Available,
            message = r.Message,
            inferenceId = r.InferenceId,
            model = r.Model is null ? null : new { r.Model.Name, r.Model.Version, r.Model.Algorithm },
            latencyMs = r.LatencyMs,
            problems = r.Problems,
            suggestions = r.Suggestions.Select(s => new
            {
                columnIndex = s.ColumnIndex,
                field = s.Field?.ToString(),
                fieldLabel = LabelOf(s.Field),
                confidence = s.Confidence,
                band = s.Band,
                reasons = s.Reasons,
                alternatives = s.Alternatives.Select(a => new { field = a.Field?.ToString(), fieldLabel = LabelOf(a.Field), confidence = a.Confidence }),
            }),
        });
    }

    public async Task<IActionResult> OnPostConfirmAsync(int id, CancellationToken ct)
    {
        Source = await import.FindAsync(id, ct);
        if (Source is null) return NotFound();

        var outcome = await import.ConfirmAsync(Source, Fields, User, InferenceId, ct);
        if (outcome.Ok)
        {
            TempData["Notice"] = $"Imported {outcome.Valid} valid row(s){(outcome.Rejected > 0 ? $" and kept {outcome.Rejected} rejected row(s) with their reasons" : "")}.";
            return RedirectToPage("File", new { id });
        }

        Error = outcome.Error;
        await TryLoadProposalAsync(ct);   // keep the operator's selections; only refresh the sample values
        return Page();
    }

    private async Task<bool> TryLoadProposalAsync(CancellationToken ct)
    {
        try
        {
            Proposal = await import.ProposeAsync(Source!, ct);
            return true;
        }
        catch (ImportException ex)
        {
            Error = ex.Message;
            return false;
        }
    }
}
