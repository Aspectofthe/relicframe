using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using RelicFrame.Core;

namespace RelicFrame.Bot;

// Read-only snapshots from this bot's existing in-memory cache. Requests never
// cause an upstream Warframe.market fetch and never expose account or inventory state.
internal sealed class SharedMarketHub : IAsyncDisposable
{
    private readonly WebApplication app;
    private SharedMarketHub(WebApplication app) => this.app = app;
    public static async Task<SharedMarketHub> StartAsync(LiveMarket market, string listenUrl, string key, CancellationToken ct)
    {
        if (!Uri.TryCreate(listenUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")
            || address.UserInfo.Length > 0 || address.AbsolutePath != "/" || !string.IsNullOrEmpty(address.Query))
            throw new ArgumentException("RELICFRAME_SHARED_HUB_LISTEN must be a plain HTTP(S) origin, such as http://127.0.0.1:8187.");
        if (string.IsNullOrWhiteSpace(key) || key.Length < 24)
            throw new ArgumentException("RELICFRAME_SHARED_HUB_KEY must contain at least 24 characters; do not put it in source control.");
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        bool Authorized(HttpContext context)
        {
            var supplied = context.Request.Headers["X-RelicFrame-Key"].ToString();
            return supplied.Length > 0 && CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
        }
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(listenUrl);
        var app = builder.Build();
        app.MapGet("/v1/health", (HttpContext context) => Authorized(context) ? Results.Json(new { status = "ready", cache = "RelicFrame public market data only" }) : Results.Unauthorized());
        app.MapGet("/v1/catalog", (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            using var doc = market.ExportSharedCatalog();
            return doc is null ? Results.NotFound() : Results.Json(doc.RootElement.Clone());
        });
        app.MapGet("/v1/books/{slug}", (HttpContext context, string slug) =>
        {
            if (!Authorized(context)) return Results.Unauthorized();
            var result = market.ExportSharedBook(slug);
            if (result is null) return Results.NotFound();
            using var rows = result.Value.Rows;
            return Results.Json(new { fetchedAt = result.Value.FetchedAt, rows = rows.RootElement.Clone() });
        });
        await app.StartAsync(ct);
        Console.WriteLine($"[shared-market] read-only cache listening on {listenUrl}; inventory, account tokens and trades are not exposed");
        return new(app);
    }
    public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
}
