using System.Net;
using System.Text.Json;

namespace RelicFrame.Core;

// An optional private cache between RelicFrame installations. It never forwards
// arbitrary URLs or authenticated account requests to Warframe.market.
public sealed class SharedMarketCache : IDisposable
{
    private readonly HttpClient client;
    private readonly Uri root;
    private string status = "not contacted";
    private long retryAfterTicks;
    public string Status => Volatile.Read(ref status);
    public SharedMarketCache(string baseUrl, string key, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || parsed.UserInfo.Length > 0
            || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            throw new ArgumentException("Shared market hub URL must be an absolute HTTP(S) URL without credentials, query or fragment.", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Shared market hub key is required.", nameof(key));
        root = parsed;
        client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(4);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelicFrame-SharedCache/1.0");
        client.DefaultRequestHeaders.Add("X-RelicFrame-Key", key);
    }
    public async Task<JsonDocument?> CatalogAsync(CancellationToken ct)
    {
        var doc = await GetAsync("v1/catalog", ct);
        if (doc is null) return null;
        if (doc.RootElement.Get("data") is { ValueKind: JsonValueKind.Array } rows && rows.GetArrayLength() > 0) return doc;
        doc.Dispose(); return null;
    }
    public async Task<(DateTimeOffset FetchedAt, JsonDocument Rows)?> BookAsync(string slug, CancellationToken ct)
    {
        if (slug.Length is < 1 or > 160 || slug.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-')) return null;
        using var doc = await GetAsync("v1/books/" + Uri.EscapeDataString(slug), ct);
        if (doc is null) return null;
        var fetched = doc.RootElement.Get("fetchedAt").Text();
        var rows = doc.RootElement.Get("rows");
        if (!DateTimeOffset.TryParse(fetched, out var at) || rows.ValueKind != JsonValueKind.Array
            || at > DateTimeOffset.UtcNow.AddMinutes(1) || DateTimeOffset.UtcNow - at > TimeSpan.FromMinutes(15)) return null;
        return (at, JsonDocument.Parse(rows.GetRawText()));
    }
    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        if (Interlocked.Read(ref retryAfterTicks) > DateTimeOffset.UtcNow.UtcTicks) return null;
        try
        {
            using var response = await client.GetAsync(new Uri(root, path), HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Volatile.Write(ref status, "key rejected");
                Interlocked.Exchange(ref retryAfterTicks, DateTimeOffset.UtcNow.AddMinutes(5).UtcTicks);
                return null;
            }
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                Volatile.Write(ref status, "unavailable");
                Interlocked.Exchange(ref retryAfterTicks, DateTimeOffset.UtcNow.AddMinutes(1).UtcTicks);
                return null;
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent) return null;
            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            Volatile.Write(ref status, "connected");
            return document;
        }
        catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Volatile.Write(ref status, "unavailable");
            Interlocked.Exchange(ref retryAfterTicks, DateTimeOffset.UtcNow.AddMinutes(1).UtcTicks);
            return null;
        }
    }
    public void Dispose() => client.Dispose();
}
