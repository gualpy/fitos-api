using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using FitOS.Application.Dashboard;
using Microsoft.Extensions.Options;

namespace FitOS.Infrastructure.Dashboard;

public sealed class GeminiOptions
{
    /// <summary>Server-side only (user-secrets or environment). Empty disables the feature.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>How long a generated answer is reused for the same gym.</summary>
    public int CacheHours { get; set; } = 6;
}

public sealed class GeminiInsightsProvider(HttpClient http, IOptions<GeminiOptions> options) : IInsightsProvider
{
    private readonly GeminiOptions _options = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.Model);

    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{Uri.EscapeDataString(_options.Model)}:generateContent")
        {
            Content = JsonContent.Create(new { contents = new[] { new { parts = new[] { new { text = prompt } } } } }),
        };
        // In a header, not the URL: query strings end up in logs.
        request.Headers.Add("x-goog-api-key", _options.ApiKey);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Status only: the body may echo the request, and the key is never to be logged or returned.
                throw new InsightsFailedException($"The insights provider answered {(int)response.StatusCode}.");
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return ExtractText(json.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !cancellationToken.IsCancellationRequested)
        {
            throw new InsightsFailedException("The insights provider could not be reached or answered badly.", ex);
        }
    }

    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0
            || !candidates[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Concat(parts.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : null));
    }
}

public sealed class MemoryInsightsCache(IOptions<GeminiOptions> options, TimeProvider time) : IInsightsCache
{
    private readonly ConcurrentDictionary<Guid, Insights> _entries = new();

    public Insights? Find(Guid tenantId) =>
        _entries.TryGetValue(tenantId, out var entry) && time.GetUtcNow() - entry.GeneratedAt < Ttl ? entry : null;

    public void Store(Guid tenantId, Insights insights) => _entries[tenantId] = insights;

    private TimeSpan Ttl => TimeSpan.FromHours(Math.Max(options.Value.CacheHours, 0));
}
