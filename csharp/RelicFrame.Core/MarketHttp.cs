using System.Net;
using System.Text.Json;

namespace RelicFrame.Core;

// A single application-owned client shares its pacing across all WFM consumers.
// All waits are cancellable; no Thread.Sleep and no lock held during Retry-After.
public sealed class MarketHttp : IDisposable
{
    private readonly HttpClient client;
    private readonly SemaphoreSlim slots;
    private readonly SemaphoreSlim schedule = new(1, 1);
    private readonly TimeSpan interval;
    private DateTimeOffset nextStart;
    public MarketHttp(HttpMessageHandler? handler = null, int concurrency = 4, double requestsPerSecond = 5)
    {
        if (concurrency < 1 || requestsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(concurrency));
        slots = new(concurrency, concurrency); interval = TimeSpan.FromSeconds(1 / requestsPerSecond);
        client = handler is null ? new(new SocketsHttpHandler { MaxConnectionsPerServer = concurrency, PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All }) : new(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelicFrame-CSharp/0.1");
        client.DefaultRequestHeaders.Add("Platform", "pc"); client.DefaultRequestHeaders.Add("Language", "en");
        client.DefaultRequestHeaders.Add("Crossplay", "true");
    }
    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, bool allowObjectLiteral = false)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await slots.WaitAsync(ct);
            TimeSpan? retry = null;
            try
            {
                await schedule.WaitAsync(ct);
                try
                {
                    var wait = nextStart - DateTimeOffset.UtcNow;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                    nextStart = DateTimeOffset.UtcNow + interval;
                }
                finally { schedule.Release(); }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var requestToken = deadline.Token;
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, requestToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    retry = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    retry = TimeSpan.FromSeconds(Math.Clamp(retry.Value.TotalSeconds, 0, 120));
                    await schedule.WaitAsync(ct);
                    try { if (nextStart < DateTimeOffset.UtcNow + retry.Value) nextStart = DateTimeOffset.UtcNow + retry.Value; }
                    finally { schedule.Release(); }
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    // Keep even malformed upstream bodies bounded and cover streamed body reads with the deadline.
                    await response.Content.LoadIntoBufferAsync(8 * 1024 * 1024, requestToken);
                    if (allowObjectLiteral) return PublicPayload.Parse(await response.Content.ReadAsStringAsync(requestToken));
                    await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: requestToken);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested &&
                (error is TaskCanceledException || error is HttpRequestException { StatusCode: null }))
            {
                if (attempt == 3) throw;
                retry = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            }
            finally { slots.Release(); }
            if (retry.HasValue) await Task.Delay(retry.Value, ct);
        }
        throw new HttpRequestException("API remained unavailable after four bounded attempts.");
    }
    public void Dispose() { client.Dispose(); slots.Dispose(); schedule.Dispose(); }
}
