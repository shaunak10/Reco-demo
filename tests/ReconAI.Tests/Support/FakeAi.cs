using ReconAI.Core.Ai;
using ReconAI.Core.Importing;
using ReconAI.Web.Services;
using static ReconAI.Tests.Support.Scenarios;

namespace ReconAI.Tests.Support;

/// <summary>A controllable stand-in for the Python service, so the web app's handling of AI answers can be tested exactly.</summary>
public sealed class FakeAiMappingClient : IAiMappingClient
{
    public Func<SourceKind, IReadOnlyList<AiColumnSample>, AiCallResult<ValidatedMapping>> Respond { get; set; } = (_, _) =>
        new(AiCallStatus.Unavailable, null, "The AI service is not running.", 1);

    public List<(SourceKind Kind, IReadOnlyList<AiColumnSample> Columns)> Calls { get; } = [];

    public Task<AiCallResult<ValidatedMapping>> SuggestMappingAsync(SourceKind kind, IReadOnlyList<AiColumnSample> columns, CancellationToken ct)
    {
        Calls.Add((kind, columns));
        return Task.FromResult(Respond(kind, columns));
    }

    public static readonly AiModelInfo Model = new("reconai-column-ranker", "test0001", "fake", 1000);

    /// <summary>Answers with the human ground-truth mapping of a scenario file, at the given confidence.</summary>
    public static Func<SourceKind, IReadOnlyList<AiColumnSample>, AiCallResult<ValidatedMapping>> GroundTruth(string scenario, double confidence = 0.95) =>
        (kind, columns) =>
        {
            var fields = Fields(scenario, kind);
            var suggestions = columns.Select((c, i) => new AiSuggestion(i, c.Header,
                fields[i].Length == 0 ? null : Enum.Parse<CanonicalField>(fields[i]), confidence, AiMappingValidator.BandOf(confidence),
                [$"test evidence for {c.Header}"], [])).ToList();
            return new(AiCallStatus.Ok, new ValidatedMapping(Model, suggestions, []), "", 7);
        };
}
