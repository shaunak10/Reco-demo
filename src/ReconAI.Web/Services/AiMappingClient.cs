using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ReconAI.Core.Ai;
using ReconAI.Core.Importing;

namespace ReconAI.Web.Services;

public sealed record AiCallResult<T>(AiCallStatus Status, T? Value, string Message, int LatencyMs)
{
    public bool Ok => Status == AiCallStatus.Ok;
}

public interface IAiMappingClient
{
    Task<AiCallResult<ValidatedMapping>> SuggestMappingAsync(SourceKind kind, IReadOnlyList<AiColumnSample> columns, CancellationToken ct);
}

/// <summary>
/// Calls the local Python column ranker. It NEVER throws: an unreachable, slow or misbehaving service becomes a status
/// the screen can show, and the operator simply maps manually. Responses are untrusted and are validated before use.
/// </summary>
public sealed class AiMappingClient(HttpClient http, ILogger<AiMappingClient> log) : IAiMappingClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<AiCallResult<ValidatedMapping>> SuggestMappingAsync(SourceKind kind, IReadOnlyList<AiColumnSample> columns, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        int Ms() => (int)started.ElapsedMilliseconds;
        try
        {
            var request = new { sourceKind = kind.ToString(), columns = columns.Select(c => new { header = c.Header, samples = c.Samples }) };
            using var response = await http.PostAsJsonAsync("/v1/map-columns", request, Json, ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("AI service answered HTTP {Status} to a mapping request", (int)response.StatusCode);
                return new(AiCallStatus.Unavailable, null, "The AI service could not handle the request. Map the columns manually; reconciliation does not depend on it.", Ms());
            }

            var raw = await response.Content.ReadFromJsonAsync<RawMappingResponse>(Json, ct);
            var validated = AiMappingValidator.Validate(kind, columns.Select(c => c.Header).ToList(), raw);
            return new(AiCallStatus.Ok, validated, "", Ms());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(AiCallStatus.Timeout, null, "The AI service took too long to answer. You can try again or map the columns manually.", Ms());
        }
        catch (HttpRequestException ex)
        {
            log.LogInformation("AI service not reachable: {Reason}", ex.GetType().Name);
            return new(AiCallStatus.Unavailable, null, "The AI service is not running. Map the columns manually; reconciliation itself does not need it.", Ms());
        }
        catch (Exception ex) when (ex is JsonException or AiResponseException or NotSupportedException)
        {
            log.LogWarning("AI response rejected: {Reason}", ex.Message);
            return new(AiCallStatus.InvalidResponse, null, "The AI service returned a response that failed validation, so it was ignored. Map the columns manually.", Ms());
        }
    }
}
