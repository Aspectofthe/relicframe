using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace RelicFrame.Core;

public sealed class WfmWebSocket
{
    public const string Url = "wss://ws.warframe.market/socket";
    public const string Protocol = "wfm";
    private readonly IReadOnlyDictionary<string, string> idToSlug;
    private readonly ISet<string> tracked;
    private readonly Action<string, JsonElement> apply;
    private int connected;
    private long lastEventTicks;
    public bool Connected => Volatile.Read(ref connected) != 0;
    public DateTimeOffset? LastEventAt => Interlocked.Read(ref lastEventTicks) is var ticks && ticks > 0 ? new(ticks, TimeSpan.Zero) : null;
    public WfmWebSocket(IReadOnlyDictionary<string, string> idToSlug, ISet<string> tracked, Action<string, JsonElement> apply)
    { this.idToSlug = idToSlug; this.tracked = tracked; this.apply = apply; }

    public bool Handle(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw); var message = doc.RootElement; var route = message.Get("route").Text();
            if (!route.Contains("neworder", StringComparison.OrdinalIgnoreCase)) return false;
            var payload = message.Get("payload"); var order = payload.Get("order").ValueKind == JsonValueKind.Object ? payload.Get("order") : payload;
            var itemId = order.Get("itemId").Text(); if (!idToSlug.TryGetValue(itemId, out var slug) || !tracked.Contains(slug)) return false;
            apply(slug, order); Interlocked.Exchange(ref lastEventTicks, DateTimeOffset.UtcNow.UtcTicks); return true;
        }
        catch (JsonException) { return false; }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var waits = new[] { 1, 2, 5, 10, 30, 60 }; var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket(); socket.Options.AddSubProtocol(Protocol); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await socket.ConnectAsync(new Uri(Url), ct);
                var subscribe = Encoding.UTF8.GetBytes("{\"route\":\"@wfm|cmd/subscribe/newOrders\",\"id\":\"relicframe-csharp-new-orders\",\"payload\":{\"platform\":\"pc\",\"crossplay\":true}}");
                await socket.SendAsync(subscribe, WebSocketMessageType.Text, true, ct); Volatile.Write(ref connected, 1); attempt = 0;
                var buffer = new byte[16 * 1024]; using var message = new MemoryStream();
                while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, ct); if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text) { if (result.EndOfMessage) message.SetLength(0); continue; }
                    if (message.Length + result.Count > 1024 * 1024) { message.SetLength(0); throw new InvalidDataException("WebSocket frame exceeded 1 MiB"); }
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) { Handle(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)); message.SetLength(0); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { Console.WriteLine($"[market-ws] {e.GetType().Name}; REST reconciliation remains active"); }
            finally { Volatile.Write(ref connected, 0); }
            if (!ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(waits[Math.Min(attempt, waits.Length - 1)]), ct); attempt++; }
        }
    }
}
