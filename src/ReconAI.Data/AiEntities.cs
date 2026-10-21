using ReconAI.Core.Ai;

namespace ReconAI.Data;

public enum AiTask
{
    ColumnMapping = 1,
}

/// <summary>
/// One call to the local AI service, successful or not. Records WHAT ran (model, version, status, latency) and the
/// compact outcome, but never the sample values that were profiled. Append-only.
/// </summary>
public class AIInferenceLog : ITenantScoped, IAppendOnly
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int? UserId { get; set; }
    public string UserEmail { get; set; } = "";
    public AiTask Task { get; set; }
    public int? SourceFileId { get; set; }
    public string ModelName { get; set; } = "";
    public string ModelVersion { get; set; } = "";
    public AiCallStatus Status { get; set; }
    public int LatencyMs { get; set; }

    /// <summary>Counts only, e.g. "8 columns, up to 30 sample values each".</summary>
    public string InputSummary { get; set; } = "";

    /// <summary>Compact JSON of the validated suggestions: column index, field, confidence. No values, no reasons.</summary>
    public string OutputJson { get; set; } = "[]";
    public string? Detail { get; set; }
    public DateTime CreatedUtc { get; set; }
}
