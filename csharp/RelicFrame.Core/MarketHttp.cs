using System.Net;
using System.Text;
using System.Text.Json;

namespace RelicFrame.Core;

// A single application-owned client shares its pacing across all WFM consumers.
// All waits are cancellable; no Thread.Sleep and no lock held during Retry-After.
public sealed class MarketHttp : IDisposable
{
    private static readonly JsonSerializerOptions AccountJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient client;
    private readonly SemaphoreSlim slots;
    private readonly SemaphoreSlim schedule = new(1, 1);
    private readonly TimeSpan interval;
    private DateTimeOffset nextStart;
    private int priorityWork;
    public double RequestsPerSecond { get; }
    public bool PriorityWorkActive => Volatile.Read(ref priorityWork) > 0;
    public MarketHttp(HttpMessageHandler? handler = null, int concurrency = 4, double requestsPerSecond = 5)
    {
        if (concurrency < 1 || requestsPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(concurrency));
        slots = new(concurrency, concurrency); RequestsPerSecond = requestsPerSecond; interval = TimeSpan.FromSeconds(1 / requestsPerSecond);
        client = handler is null ? new(new SocketsHttpHandler { MaxConnectionsPerServer = concurrency, PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All }) : new(handler);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RelicFrame-CSharp/0.1");
        client.DefaultRequestHeaders.Add("Platform", "pc"); client.DefaultRequestHeaders.Add("Language", "en");
        client.DefaultRequestHeaders.Add("Crossplay", "true");
    }
    public IDisposable BeginPriorityWork()
    {
        Interlocked.Increment(ref priorityWork);
        return new PriorityLease(this);
    }
    private async Task AcquireSlotAsync(bool lowPriority, CancellationToken ct)
    {
        while (true)
        {
            while (lowPriority && PriorityWorkActive) await Task.Delay(50, ct);
            await slots.WaitAsync(ct);
            if (!lowPriority || !PriorityWorkActive) return;
            slots.Release();
        }
    }
    public async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct, bool allowObjectLiteral = false, bool lowPriority = false)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await AcquireSlotAsync(lowPriority, ct);
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
                    if (attempt == 3) response.EnsureSuccessStatusCode();
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
    public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string url, object? body, string bearerToken, CancellationToken ct, bool lowPriority = true)
    {
        if (string.IsNullOrWhiteSpace(bearerToken)) throw new InvalidOperationException("Warframe.market session token is empty.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await AcquireSlotAsync(lowPriority, ct); TimeSpan? retry = null;
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
                using var request = new HttpRequestMessage(method, url);
                request.Headers.Authorization = new("Bearer", bearerToken.Trim());
                // Warframe.market v2 uses camelCase request fields (not the snake_case
                // format used by RelicFrame's local state files).
                if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, AccountJsonOptions), Encoding.UTF8, "application/json");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new UnauthorizedAccessException("Warframe.market rejected the local session token. Reconnect AlecaFrame to Warframe.market, then restart RelicFrame.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    if (attempt == 3) response.EnsureSuccessStatusCode();
                    retry = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    retry = TimeSpan.FromSeconds(Math.Clamp(retry.Value.TotalSeconds, 0, 120));
                    await schedule.WaitAsync(ct);
                    try { if (nextStart < DateTimeOffset.UtcNow + retry.Value) nextStart = DateTimeOffset.UtcNow + retry.Value; }
                    finally { schedule.Release(); }
                }
                else
                {
                    await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, deadline.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        var detail = (await response.Content.ReadAsStringAsync(deadline.Token)).Replace('\r', ' ').Replace('\n', ' ').Trim();
                        if (detail.Length > 800) detail = detail[..800] + "…";
                        throw new HttpRequestException($"Warframe.market returned {(int)response.StatusCode} ({response.ReasonPhrase}): {detail}", null, response.StatusCode);
                    }
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
                }
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is TaskCanceledException or HttpRequestException { StatusCode: null })
            {
                if (attempt == 3) throw; retry = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            }
            finally { slots.Release(); }
            if (retry.HasValue) await Task.Delay(retry.Value, ct);
        }
        throw new HttpRequestException("Authenticated API remained unavailable after four bounded attempts.");
    }
    private sealed class PriorityLease(MarketHttp owner) : IDisposable
    {
        private MarketHttp? current = owner;
        public void Dispose()
        {
            var released = Interlocked.Exchange(ref current, null);
            if (released is not null) Interlocked.Decrement(ref released.priorityWork);
        }
    }
    public void Dispose() { client.Dispose(); slots.Dispose(); schedule.Dispose(); }
}
