using System.Text.Json;

namespace ReconAI.Web.Services;

public sealed record AiHealth(bool Reachable, string? Service, string? Version, string Detail);

/// <summary>
/// Pings the local Python AI service. A failure here is reported, never thrown: the deterministic engine and the
/// rest of the app must keep working when the AI service is down.
/// </summary>
public sealed class AiHealthClient(HttpClient http, ILogger<AiHealthClient> log)
{
    public async Task<AiHealth> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await http.GetAsync("/health", ct);
            if (!res.IsSuccessStatusCode) return new AiHealth(false, null, null, $"Responded with HTTP {(int)res.StatusCode}");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new AiHealth(true, Str("service"), Str("version"), "Online");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogInformation("AI service not reachable: {Reason}", ex.GetType().Name);
            return new AiHealth(false, null, null, "Not running - AI features are paused, reconciliation still works");
        }
    }
}
