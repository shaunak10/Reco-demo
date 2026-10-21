using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Ai;
using ReconAI.Core.Security;
using ReconAI.Data;

namespace ReconAI.Web.Pages.Admin;

/// <summary>What the AI actually did: every call, which model version, how long it took, and how often operators accepted it.</summary>
[Authorize(Policy = Policies.CompanyAdminOnly)]
public class AiActivityModel(ReconDbContext db) : PageModel
{
    public IReadOnlyList<AIInferenceLog> Calls { get; private set; } = [];
    public int TotalCalls { get; private set; }
    public int OkCalls { get; private set; }
    public double? MedianLatencyMs { get; private set; }
    public int MappingsConfirmed { get; private set; }
    public int AiAssistedMappings { get; private set; }
    public int Suggested { get; private set; }
    public int Accepted { get; private set; }
    public IReadOnlyList<string> ModelVersions { get; private set; } = [];

    public double? AcceptanceRate => Suggested == 0 ? null : (double)Accepted / Suggested;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Calls = await db.AIInferenceLogs.OrderByDescending(c => c.CreatedUtc).ThenByDescending(c => c.Id).Take(50).ToListAsync(ct);
        TotalCalls = await db.AIInferenceLogs.CountAsync(ct);
        OkCalls = await db.AIInferenceLogs.CountAsync(c => c.Status == AiCallStatus.Ok, ct);

        var latencies = await db.AIInferenceLogs.Where(c => c.Status == AiCallStatus.Ok)
            .OrderByDescending(c => c.Id).Take(500).Select(c => c.LatencyMs).ToListAsync(ct);
        latencies.Sort();
        MedianLatencyMs = latencies.Count == 0 ? null : latencies.Count % 2 == 1 ? latencies[latencies.Count / 2] : (latencies[latencies.Count / 2 - 1] + latencies[latencies.Count / 2]) / 2.0;

        MappingsConfirmed = await db.ColumnMappings.CountAsync(ct);
        var ai = db.ColumnMappings.Where(m => m.AiInferenceId != null);
        AiAssistedMappings = await ai.CountAsync(ct);
        Suggested = await ai.SumAsync(m => (int?)m.AiSuggestedCount, ct) ?? 0;
        Accepted = await ai.SumAsync(m => (int?)m.AiAcceptedCount, ct) ?? 0;
        ModelVersions = await db.AIInferenceLogs.Where(c => c.ModelVersion != "")
            .Select(c => c.ModelName + " v" + c.ModelVersion).Distinct().ToListAsync(ct);
    }
}
