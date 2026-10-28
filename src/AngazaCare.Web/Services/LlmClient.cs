using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AngazaCare.Services;

public sealed record LlmSign(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("evidence")] string? Evidence);

public sealed record LlmParse(
    [property: JsonPropertyName("signs")] List<LlmSign> Signs,
    [property: JsonPropertyName("wellbeing")] string Wellbeing,          // well | unwell | unknown
    [property: JsonPropertyName("wellbeing_confidence")] double WellbeingConfidence,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("prompt_version")] string PromptVersion);

public sealed record LlmOutcome(LlmParse? Parse, string? RawJson, int LatencyMs, string? Error);

/// <summary>
/// Calls the separate AI parsing service (ai/ folder). Only the reply text and
/// language are sent: never a name, phone number or location.
/// </summary>
public sealed class LlmClient(HttpClient http, IConfiguration config, ILogger<LlmClient> log)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(config["AiService:BaseUrl"]);

    public async Task<LlmOutcome> ParseAsync(string text, string language, IEnumerable<string> allowedCodes, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var res = await http.PostAsJsonAsync("v1/parse", new { text, language, allowed_codes = allowedCodes }, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return new(null, raw, (int)sw.ElapsedMilliseconds, $"HTTP {(int)res.StatusCode}");
            var parse = JsonSerializer.Deserialize<LlmParse>(raw);
            return new(parse, raw, (int)sw.ElapsedMilliseconds, parse is null ? "empty response" : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(ex, "AI service call failed; reply will go to a human");
            return new(null, null, (int)sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }
}
