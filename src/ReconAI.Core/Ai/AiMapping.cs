using ReconAI.Core.Importing;

namespace ReconAI.Core.Ai;

public enum AiCallStatus
{
    Ok = 1,
    Unavailable = 2,
    Timeout = 3,
    InvalidResponse = 4,
}

/// <summary>One column offered to the AI: its heading and a few sample values (used for profiling only).</summary>
public sealed record AiColumnSample(string Header, IReadOnlyList<string> Samples);

public sealed record AiAlternative(CanonicalField? Field, double Confidence);

/// <param name="Field">Null means "ignore this column".</param>
public sealed record AiSuggestion(
    int ColumnIndex,
    string Header,
    CanonicalField? Field,
    double Confidence,
    string Band,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<AiAlternative> Alternatives);

public sealed record AiModelInfo(string Name, string Version, string Algorithm, int TrainingRows);

// ---- what the AI service sends back, exactly as received: UNTRUSTED until AiMappingValidator has checked it ----
public sealed record RawAlternative(string? Field, double Confidence);

public sealed record RawSuggestion(int ColumnIndex, string? Header, string? Field, double Confidence, string? Band, List<string>? Reasons, List<RawAlternative>? Alternatives);

public sealed record RawModel(string? Name, string? Version, string? Algorithm, int TrainingRows);

public sealed record RawMappingResponse(RawModel? Model, int LatencyMs, List<RawSuggestion>? Suggestions);

public sealed record ValidatedMapping(AiModelInfo Model, IReadOnlyList<AiSuggestion> Suggestions, IReadOnlyList<string> Problems);

public sealed class AiResponseException(string message) : Exception(message);

/// <summary>
/// Server-side check of an AI mapping response. The AI only proposes: anything that is not a known canonical field allowed
/// for the source, anything out of range, and any second column claiming the same field is dropped and reported, and the
/// confidence band is recomputed here rather than trusted.
/// </summary>
public static class AiMappingValidator
{
    public const int MaxReasons = 4;
    public const int MaxReasonLength = 240;
    public const int MaxAlternatives = 3;

    public static string BandOf(double confidence) => confidence >= 0.80 ? "high" : confidence >= 0.50 ? "medium" : "low";

    public static ValidatedMapping Validate(SourceKind kind, IReadOnlyList<string> headings, RawMappingResponse? raw)
    {
        if (raw?.Model is null || string.IsNullOrWhiteSpace(raw.Model.Name) || string.IsNullOrWhiteSpace(raw.Model.Version))
            throw new AiResponseException("The response did not identify the model that produced it.");
        if (raw.Suggestions is null)
            throw new AiResponseException("The response contained no suggestions list.");

        var problems = new List<string>();
        var allowed = SourceSchema.Allowed(kind);
        var byColumn = new Dictionary<int, AiSuggestion>();

        foreach (var s in raw.Suggestions)
        {
            if (s.ColumnIndex < 0 || s.ColumnIndex >= headings.Count) { problems.Add($"Ignored a suggestion for non-existent column {s.ColumnIndex}."); continue; }
            if (byColumn.ContainsKey(s.ColumnIndex)) { problems.Add($"Ignored a second suggestion for column {s.ColumnIndex + 1}."); continue; }
            if (!string.Equals(s.Header, headings[s.ColumnIndex], StringComparison.Ordinal)) { problems.Add($"Ignored the suggestion for column {s.ColumnIndex + 1}: its heading did not match."); continue; }
            if (double.IsNaN(s.Confidence) || s.Confidence < 0 || s.Confidence > 1) { problems.Add($"Ignored the suggestion for column {s.ColumnIndex + 1}: confidence out of range."); continue; }

            if (!TryField(s.Field, kind, allowed, out var field)) { problems.Add($"Ignored the suggestion for column {s.ColumnIndex + 1}: '{Clip(s.Field)}' is not a field this source can map."); continue; }

            var alternatives = (s.Alternatives ?? [])
                .Where(a => !double.IsNaN(a.Confidence) && a.Confidence is >= 0 and <= 1 && TryField(a.Field, kind, allowed, out _))
                .Select(a => { TryField(a.Field, kind, allowed, out var f); return new AiAlternative(f, a.Confidence); })
                .Take(MaxAlternatives).ToList();

            byColumn[s.ColumnIndex] = new AiSuggestion(s.ColumnIndex, headings[s.ColumnIndex], field, s.Confidence, BandOf(s.Confidence), CleanReasons(s.Reasons), alternatives);
        }

        // one column per field: if the AI broke that rule, keep its most confident claim only
        foreach (var group in byColumn.Values.Where(x => x.Field is not null).GroupBy(x => x.Field).Where(g => g.Count() > 1).ToList())
        {
            foreach (var loser in group.OrderByDescending(x => x.Confidence).Skip(1))
            {
                problems.Add($"{SourceSchema.Label(group.Key!.Value)} was suggested for several columns; kept only the most confident.");
                byColumn[loser.ColumnIndex] = loser with { Field = null, Reasons = ["Another column was a better match for the same field."] };
            }
        }

        var all = new List<AiSuggestion>();
        for (var i = 0; i < headings.Count; i++)
        {
            all.Add(byColumn.TryGetValue(i, out var s) ? s
                : new AiSuggestion(i, headings[i], null, 0, "low", ["No usable suggestion was returned for this column."], []));
        }

        var model = new AiModelInfo(Clip(raw.Model.Name, 80), Clip(raw.Model.Version, 40), Clip(raw.Model.Algorithm ?? "", 120), Math.Max(0, raw.Model.TrainingRows));
        return new ValidatedMapping(model, all, problems);
    }

    private static bool TryField(string? name, SourceKind kind, IReadOnlyList<CanonicalField> allowed, out CanonicalField? field)
    {
        field = null;
        if (name is null) return true;                          // null = "ignore this column"
        if (!Enum.TryParse<CanonicalField>(name, ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed) || !allowed.Contains(parsed)) return false;
        field = parsed;
        return true;
    }

    private static IReadOnlyList<string> CleanReasons(List<string>? reasons) =>
        (reasons ?? []).Select(r => Clip(new string(r.Where(c => !char.IsControl(c)).ToArray()).Trim(), MaxReasonLength))
                       .Where(r => r.Length > 0).Take(MaxReasons).ToList();

    private static string Clip(string? s, int max = 40) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "...";
}
