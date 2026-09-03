using System.Net;
using System.Text;
using System.Text.Json;
using RelicFrame.Core;

var fixturePath = args.FirstOrDefault() ?? "csharp/fixtures.generated.json";
using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
var count = 0;
foreach (var test in fixture.RootElement.EnumerateArray())
{
    var kind = test.Get("kind").Text();
    object? actual;
    if (kind is "profit" or "batch" or "compare")
    {
        var relic = test.Get("relic").Deserialize<Relic>(Json.Options)!;
        var prices = test.Get("prices").Deserialize<Dictionary<string, double?>>(Json.Options)!;
        var tier = Enum.Parse<Refinement>(test.Get("tier").Text(), true);
        var cost = test.Get("cost").Number(); var rate = test.Get("rate").Number() ?? 0;
        actual = kind switch
        {
            "profit" => relic.Profitability(tier, prices, cost, rate),
            "batch" => relic.BuyN(tier, prices, cost, rate, (int)test.Get("n").Number()!),
            _ => relic.Compare(prices, cost, rate)
        };
    }
    else if (kind == "trade_parse")
    {
        var chat = new RivenTradeChat(Path.Combine(Path.GetTempPath(), "unused-test-log.jsonl"), test.Get("weapons").Rows().Select(v => v.Text()));
        var parsed = chat.Parse(test.Get("text").Text(), DateTimeOffset.Parse(test.Get("observed").Text()), "synthetic");
        actual = new { offers = parsed.Offers, unparsed = parsed.Unparsed, summary = RivenTradeChat.Summary(parsed.Offers) };
    }
    else if (kind == "ranking")
    {
        var scope = test.Get("scope").Text(); var tier = Enum.Parse<Refinement>(test.Get("tier").Text(), true);
        var rows = test.Get("rows").Rows().Select(c => RelicRow.Compute(c.Get("relic").Deserialize<Relic>(Json.Options)!, tier,
            c.Get("prices").Deserialize<Dictionary<string, double?>>(Json.Options)!, c.Get("info").Deserialize<PriceInfo>(Json.Options)!,
            c.Get("ducats").Deserialize<Dictionary<string, int>>(Json.Options)!, .05, scope)).ToArray();
        var f = test.Get("filters");
        actual = new { ranks = test.Get("expected").Get("ranks").EnumerateObject().ToDictionary(p => p.Name, p => RelicRow.Rank(rows, p.Name, scope).Select(r => r.Relic.RelicName).ToArray()),
            passes = rows.Select(r => r.Passes(scope, f.Get("vault_filter").Text(), f.Get("min_roi").Number(), f.Get("max_cost").Number(), f.Get("min_reward").Number(), f.Get("green_only").Bool())).ToArray(),
            risk = rows.Select(r => r.BestRisk).ToArray(), category = rows.Select(r => r.OverallCategory).ToArray() };
    }
    else actual = kind switch
    {
        "orders" => OrderMath.Matching(test.Get("orders").Rows(), test.Get("tier").ValueKind == JsonValueKind.Null ? null : test.Get("tier").Text()),
        "purchase" => OrderMath.Cheapest(test.Get("entries").Deserialize<OrderEntry[]>(Json.Options)!, (int)test.Get("n").Number()!),
        "rule_parse" => RivenRules.Parse(test.Get("expression").Text()),
        "rule" => RivenRules.Evaluate(test.Get("positives").Rows().Select(r => r.Text()), test.Get("negatives").Rows().Select(r => r.Text()), RivenRules.Read(test.Get("rule"))),
        "colors" => CompanionColors.Rarity(test.Get("colors").Rows().Select(r => r.Text())),
        "riven_ranges" => RivenPricing.Ranges(test.Get("positives").Rows().Select(r => r.Text()).ToArray(), test.Get("negative").ValueKind == JsonValueKind.Null ? null : test.Get("negative").Text(), test.Get("stat_class").Text(), test.Get("disposition").Number()!.Value),
        "riven_quality" => RivenPricing.RollQuality(test.Get("auction"), test.Get("stat_class").Text(), test.Get("disposition").Number()!.Value),
        "auction_price" => RivenPricing.AuctionPrice(test.Get("auction")),
        "riven_deals" => RivenPricing.FindDeals(test.Get("auctions").Rows(), "test", "Test", test.Get("weekly").Deserialize<WeeklyPrice>(Json.Options), test.Get("stat_class").Text(), test.Get("disposition").Number(), 10, (int?)test.Get("max_price").Number(), test.Get("online_only").Bool(), 100, test.Get("rule").ValueKind == JsonValueKind.Null ? null : RivenRules.Read(test.Get("rule")), test.Get("curated_only").Bool(), test.Get("rule").Get("positive_expression").Text(), test.Get("rule").Get("notes").Text()),
        _ => throw new Exception($"Unknown fixture {kind}")
    };
    var result = JsonSerializer.SerializeToElement(actual, Json.Options);
    Compare(test.Get("expected"), result, $"case {count} ({kind})"); count++;
}
Console.WriteLine($"PASS: {count} cross-language parity cases.");

using (var doc = JsonDocument.Parse("[{\"id\":\"1\",\"platinum\":3.125,\"note\":\"雪\",\"user\":{\"status\":\"ingame\"}}]"))
{
    var book = new OrderBook(); book.Replace(doc.RootElement);
    using var read = book.Read(); Compare(doc.RootElement, read.RootElement, "lossless compressed book");
    await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        for (var i = 0; i < 100; i++) { book.Replace(doc.RootElement); using var d = book.Read(); Compare(doc.RootElement, d.RootElement, "concurrent book"); }
    })));
}
var handler = new FakeHandler();
using (var http = new MarketHttp(handler, 2, 200))
{
    await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ => { using var d = await http.GetJsonAsync("https://example.invalid/orders", default); }));
    Assert(handler.Peak <= 2, "HTTP concurrency bound");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { using var d = await http.GetJsonAsync("https://example.invalid/cancel", cancelled.Token); throw new Exception("Cancellation not observed"); }
    catch (OperationCanceledException) { }
    using var after = await http.GetJsonAsync("https://example.invalid/after", default);
}
var temp = Path.Combine(Path.GetTempPath(), "relicframe-csharp-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var path = Path.Combine(temp, "evidence.jsonl");
    var row = new { text = "TEST ONLY", timestamp = "2026-01-01T00:00:00+00:00", traits = new { species = new[] { "kubrow" } }, amounts = new[] { new { low = 150, high = 250 } }, classification = "confirmed_sale", attachments = Array.Empty<string>() };
    File.WriteAllText(path, JsonSerializer.Serialize(row));
    var original = File.ReadAllBytes(path);
    var appraisal = new CompanionAppraiser(CompanionAppraiser.Load(path)).Appraise(new Dictionary<string, string?> { ["species"] = "kubrow" });
    Assert(appraisal.Estimate == 200 && appraisal.ComparableCount == 1, "companion evidence appraisal");
    Assert(original.SequenceEqual(File.ReadAllBytes(path)), "evidence remains untouched");
    var csv = Path.Combine(temp, "relics.csv");
    File.WriteAllText(csv, "relic_name,reward_name,rarity,vaulted\nLith Test,\"Reward, comma\",rare,\n");
    var relic = Relic.LoadCsv(csv)["Lith Test"];
    Assert(relic.Vaulted is null && relic.Rewards[0].RewardName == "Reward, comma", "quoted CSV and unknown vault");
    using (var literal = PublicPayload.Parse("[{compatibility:'Test \\'one\\'', rerolled:true, avg:50.2, empty:null, tail:[1,2,],}]"))
        Assert(literal.RootElement[0].Get("compatibility").Text() == "Test 'one'" && literal.RootElement[0].Get("rerolled").Bool(), "safe DE literal parser");
    foreach (var bad in new[] { "[{x:process.exit()}]", "[{x:NaN}]", "[{x:new Date()}]", "[];run()", "42" })
    {
        try { using var invalid = PublicPayload.Parse(bad); throw new Exception("Executable/invalid literal was accepted"); }
        catch (JsonException) { }
    }
    using (var pool = new AuctionPool(temp))
    {
        using var first = JsonDocument.Parse("[{\"id\":\"x\",\"item\":{\"weapon_url_name\":\"../../outside\"},\"buyout_price\":10}]");
        using var second = JsonDocument.Parse("[{\"id\":\"x\",\"item\":{\"weapon_url_name\":\"../../outside\"},\"buyout_price\":20}]");
        pool.Add(first.RootElement.Rows()); pool.Add(second.RootElement.Rows());
        var pooled = pool.Read("../../outside");
        Assert(pooled.Length == 1 && pooled[0].Get("buyout_price").Number() == 20, "disk pool deduplicates latest records and hashes paths");
    }
    Assert(!Directory.EnumerateDirectories(temp, "auction-pool-*").Any(), "auction pool cleans up its owned directory");
    var feedHandler = new RivenFeedHandler();
    using (var feedHttp = new MarketHttp(feedHandler, 2, 10000))
    {
        var rulePath = Path.Combine(temp, "rules.json");
        Json.WriteAtomic(rulePath, new { rows = new[] { new { weapon = "Test", alternatives = RivenRules.Parse("CC CD MS"), harmless_negatives = new[] { "zoom" }, positive_expression = "CC CD MS" } } });
        await using var service = new RivenMarket(feedHttp, temp, rulePath);
        await service.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (service.Index.CompletedAt == default) await Task.Delay(10, deadline.Token);
        Assert(service.Index.ScannedFamilies == 2 && service.Index.Deals.Length > 0, "Riven scan visits every catalog family and publishes candidates");
        Assert(feedHandler.Searches == (RivenPricing.Bases.Count - 1) * 2, "both price directions searched for every positive stat");
        Assert(service.Index.Deals.All(d => d.PositiveRolls.Length == 3 && d.NegativeRolls.Length == 1), "full roll values retained in deal index");
        await service.StopAsync();
        var before = service.Index;
        Assert(before == service.Index && service.Status.StartsWith("Stopped"), "stop retains previous completed index");
        await using var restored = new RivenMarket(feedHttp, temp, rulePath);
        Assert(restored.Index.Deals.Length == before.Deals.Length, "persisted C# index survives restart");
    }
    using (var cancelHttp = new MarketHttp(new BlockedHandler()))
    {
        await using var service = new RivenMarket(cancelHttp, Path.Combine(temp, "cancel"));
        await service.StartAsync(default); await Task.Delay(25); await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert(service.Index.CompletedAt == default, "cancelled scan does not publish an empty complete index");
    }
    var retryHandler = new RetryHandler();
    using (var retryHttp = new MarketHttp(retryHandler, 1, 10000))
    {
        using var result = await retryHttp.GetJsonAsync("https://example.invalid/retry", default);
        Assert(retryHandler.Calls == 3 && result.RootElement.Get("ok").Bool(), "429 and 503 retry successfully");
    }
    var catalogRelics = new Dictionary<string, Relic> { ["Lith Test"] = new("Lith Test", [new("Reward", "rare")]) };
    var panelSnapshot = new MarketSnapshot(DateTimeOffset.UtcNow, Refinement.Radiant,
        new Dictionary<string, double?> { ["reward"] = 20 },
        new Dictionary<string, PriceInfo> { ["Lith Test"] = new(10, 10, true, false, 10, 10) },
        new Dictionary<string, int> { ["reward"] = 100 }, 2, 2);
    var panelRows = RelicPanel.Select(catalogRelics, panelSnapshot, new RelicPanelOptions());
    Assert(panelRows.Length == 1 && Math.Abs(panelRows[0].Online.Profit.ExpectedValue - 2) < .0001, "persistent panel defaults to Radiant and selects ranked rows");
    Assert(RelicPanel.Select(catalogRelics, panelSnapshot, new RelicPanelOptions(MinReward: 999)).Length == 0, "persistent panel applies saved filters");
    using (var marketHttp = new MarketHttp(new MarketFeedHandler(), 2, 10000))
    {
        await using var market = new LiveMarket(marketHttp, catalogRelics, Path.Combine(temp, "market"));
        market.Blacklist.Change("  BADseller ", true);
        await market.StartAsync(true, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (market.Status != "ready") await Task.Delay(10, timeout.Token);
        Assert(market.ReadyBooks == 2 && market.TotalBooks == 2, "catalog resolves and deduplicates required relic/reward slugs");
        Assert(market.Resolve("Lith Test", true) == "correct_relic", "relic suffix resolution matches Python priority");
        var snapshot = market.Snapshot(Refinement.Radiant);
        Assert(snapshot.Prices["reward"] == 20 && snapshot.RelicPrices["Lith Test"].Online == 20, "blacklist affects EV input and relic acquisition cost");
        Assert(snapshot.Ducats["reward"] == 100, "WFInfo fills missing ducats");
        var stamp = snapshot.FetchedAt; await Task.Delay(10);
        Assert(market.Snapshot(Refinement.Radiant).FetchedAt == stamp, "reading a snapshot does not freshen old data");
        market.Blacklist.Change("Badseller", false);
        Assert(market.Snapshot(Refinement.Radiant).Prices["reward"] == 1, "removing seller exclusion restores retained order evidence");
        await market.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
    using (var invalidHttp = new MarketHttp(new MarketFeedHandler { InvalidOrders = true }, 2, 10000))
    {
        await using var market = new LiveMarket(invalidHttp, catalogRelics, Path.Combine(temp, "invalid-market"));
        await market.StartAsync(true, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!market.Status.StartsWith("partial")) await Task.Delay(10, timeout.Token);
        Assert(market.ReadyBooks == 0, "failed order fetches are not reported as loaded books");
        await market.StopAsync();
    }
    var schedulePath = Path.Combine(temp, "schedule.txt");
    File.WriteAllText(schedulePath, "Mon, August 24\n0400 • Void Cascade - Corpus @ Tuvul Commons, Zariman (S tier, 25% resource bonus)\n0500 • Defense - Grineer @ Hydron, Sedna (C tier)\ninvalid line\n");
    var schedule = ArbitrationSchedule.Load(schedulePath, "America/New_York", 2026);
    Assert(schedule.Length == 2 && schedule[0].Tier == "S" && schedule[0].ResourceBonus == "25% resource bonus", "arbitration schedule parser");
    Assert(ArbitrationSchedule.TierFor(schedule, "Tuvul Commons (Zariman)", "Void Cascade") == "S", "schedule tier matches normalized fissure node");
    var future = DateTimeOffset.UtcNow.AddHours(1).ToString("O"); var old = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
    using (var worldDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, fissures = new object[] {
        new { id="normal", tier="Lith", tierNum=1, missionType="Void Cascade", node="Tuvul Commons (Zariman)", expiry=future, isHard=false, isStorm=false },
        new { id="steel", tier="Axi", tierNum=4, missionType="Void Cascade", node="Tuvul Commons (Zariman)", expiry=future, isHard=true, isStorm=false },
        new { id="storm", tier="Neo", tierNum=3, missionType="Survival", node="Railjack", expiry=future, isHard=false, isStorm=true } },
        cetusCycle = new { state="day", expiry=future }, vallisCycle = new { state="warm", expiry=future }, cambionCycle = new { state="fass", expiry=future }, duviriCycle = new { state="joy", expiry=future }, zarimanCycle = new { state="corpus", expiry=future },
        news = new[] { new { id="news", message="Test news", date=old, link="https://example.invalid/news" } }, alerts=Array.Empty<object>(), events=Array.Empty<object>(), invasions=Array.Empty<object>(), dailyDeals=Array.Empty<object>(),
        sortie = new { id="sortie", boss="Test Boss", expiry=future, variants=new[] { new { missionType="Defense", modifier="Test", node="Hydron" } }, activation=old },
        archonHunt = new { id="archon", boss="Test Archon", expiry=future, missions=new[] { new { type="Survival" } }, activation=old }, steelPath = new { expiry=future, currentReward=new { name="Umbra Forma", cost=150 } },
        archimedeas=Array.Empty<object>(), voidTrader=new { activation=future, expiry=future, location="Test Relay" }, kinepage=new { message="Test transmission", timestamp=old } })))
    using (var bountyDoc = JsonDocument.Parse("{\"rot\":\"A\",\"vaultRot\":\"B\",\"expiry\":123,\"bounties\":{\"HexSyndicate\":[{}]}}"))
    {
        var snapshot = new WorldSnapshot(worldDoc.RootElement, bountyDoc.RootElement, DateTimeOffset.UtcNow, false, "synthetic", []);
        foreach (var key in new[] { "bot-guide", "world-cycles", "world-news", "world-alerts", "world-sortie", "world-archon", "world-steel-path", "world-weekly", "world-archimedea", "world-vendors", "world-bounties", "world-fissures", "world-steel-fissures", "world-void-storms", "world-invasions", "world-arbitration", "world-cascade" })
        { var boards = WorldRender.Build(key, snapshot, schedule); Assert(boards is { Length: > 0 and <= 10 } && boards.All(b => b.Description.Length <= 4096), "world board " + key); }
        var fissure = WorldRender.Build("world-fissures", snapshot, schedule)[0].Description;
        Assert(fissure.Contains("Lvl S tier") && !fissure.Contains("Arbitration S tier"), "fissure displays Lvl tier wording");
        var cascade = WorldRender.Build("world-cascade", snapshot, schedule)[0].Description;
        Assert(cascade.Contains("Normal Void Cascade") && cascade.Contains("Steel Path Void Cascade") && !cascade.Contains("Upcoming Void Cascade Arbitrations"), "cascade board sections");
        var signatures = WorldRender.Signatures(snapshot, schedule);
        Assert(signatures["cascade"].SequenceEqual(["normal"]) && signatures["steel_cascade"].SequenceEqual(["steel"]) && signatures["void_storm_neo"].SequenceEqual(["storm"]), "separate cascade and fissure-tier signatures");
        using var empty = JsonDocument.Parse("{\"fissures\":[]}"); var stale = snapshot with { Stale = true, World = empty.RootElement };
        Assert(WorldRender.Build("world-fissures", stale, schedule)[0].Description.Contains("source is delayed"), "stale mission source is not called empty");
    }
    using (var worldClient = new WorldStateClient(new WorldFeedHandler()))
    {
        var fallback = await worldClient.FetchAsync(default);
        Assert(fallback.Stale && fallback.RepairedSections.SequenceEqual(["fissures"]) && fallback.Source.StartsWith("official"), "fresh official data repairs stale parsed fissures");
        var officialRow = fallback.World.Get("fissures").Rows().Single();
        Assert(officialRow.Get("id").Text() == "fresh" && officialRow.Get("tier").Text() == "Neo" && officialRow.Get("missionType").Text() == "Defense" && officialRow.Get("node").Text() == "Hydron (Sedna)", "official fissure normalization: " + officialRow.GetRawText());
        Assert(!WorldRender.Build("world-fissures", fallback, schedule)[0].Description.Contains("source is delayed"), "repaired fissures are not labelled delayed");
    }
}
finally { Directory.Delete(temp, true); } // exact, freshly created test-only directory
Console.WriteLine("PASS: compression, concurrent snapshots, HTTP retry/cancellation, evidence, CSV, safe literals, disk pool, synthetic Riven/relic/world services, exclusions, tier/ping rendering and stale-data repair.");

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Compare(JsonElement expected, JsonElement actual, string path)
{
    if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
    {
        var e = expected.GetDouble(); var a = actual.GetDouble();
        Assert(Math.Abs(e - a) <= 1e-8 * Math.Max(1, Math.Abs(e)), $"{path}: {e} != {a}"); return;
    }
    Assert(expected.ValueKind == actual.ValueKind, $"{path}: expected {expected.ValueKind}, got {actual.ValueKind}");
    if (expected.ValueKind == JsonValueKind.Object)
        foreach (var prop in expected.EnumerateObject()) Compare(prop.Value, actual.Get(prop.Name), path + "." + prop.Name);
    else if (expected.ValueKind == JsonValueKind.Array)
    {
        Assert(expected.GetArrayLength() == actual.GetArrayLength(), path + ": array length");
        for (var i = 0; i < expected.GetArrayLength(); i++) Compare(expected[i], actual[i], path + $"[{i}]");
    }
    else Assert(expected.ToString() == actual.ToString(), $"{path}: {expected} != {actual}");
}
sealed class FakeHandler : HttpMessageHandler
{
    private int active;
    public int Peak;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var current = Interlocked.Increment(ref active); int old;
        do { old = Peak; } while (current > old && Interlocked.CompareExchange(ref Peak, current, old) != old);
        try { await Task.Delay(15, ct); return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json") }; }
        finally { Interlocked.Decrement(ref active); }
    }
}
sealed class RivenFeedHandler : HttpMessageHandler
{
    public int Searches;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var uri = request.RequestUri!.ToString(); string json;
        if (uri.Contains("weeklyRivens")) json = "[{compatibility:'Test',rerolled:true,median:500,avg:500,min:10,max:2000,pop:40}]";
        else if (uri.EndsWith("/riven/weapons")) json = "{\"data\":[{\"slug\":\"test\",\"rivenType\":\"rifle\",\"disposition\":1,\"i18n\":{\"en\":{\"name\":\"Test\"}}},{\"slug\":\"empty\",\"i18n\":{\"en\":{\"name\":\"Empty\"}}}]}";
        else
        {
            Interlocked.Increment(ref Searches);
            var rows = new[] { 20, 100, 200, 300, 400, 500 }.Select((p, i) => new { id = $"synthetic-{i}", buyout_price = p,
                owner = new { ingame_name = "TestOnly", slug = "test-only", status = "online" },
                item = new { weapon_url_name = "test", attributes = new[] { new { url_name = "critical_chance", positive = true, value = 140d }, new { url_name = "critical_damage", positive = true, value = 115d }, new { url_name = "multishot", positive = true, value = 85d }, new { url_name = "zoom", positive = false, value = -40d } } } });
            json = JsonSerializer.Serialize(new { payload = new { auctions = rows } });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
sealed class BlockedHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException("Unreachable"); }
}
sealed class RetryHandler : HttpMessageHandler
{
    public int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++; var response = new HttpResponseMessage(Calls == 1 ? HttpStatusCode.TooManyRequests : Calls == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
        { Content = new StringContent("{\"ok\":true}") };
        response.Headers.RetryAfter = new(TimeSpan.Zero); return Task.FromResult(response);
    }
}
sealed class MarketFeedHandler : HttpMessageHandler
{
    public bool InvalidOrders;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var uri = request.RequestUri!.ToString(); string json;
        if (uri.EndsWith("/items")) json = "{\"data\":[{\"slug\":\"wrong_relic\",\"i18n\":{\"en\":{\"name\":\"Lith Test\"}}},{\"slug\":\"correct_relic\",\"i18n\":{\"en\":{\"name\":\"Lith Test Relic\"}}},{\"slug\":\"reward\",\"i18n\":{\"en\":{\"name\":\"Reward\"}}}]}";
        else if (uri.EndsWith("/filtered_items")) json = "{\"eqmt\":{\"Test\":{\"parts\":{\"Reward\":{\"ducats\":100}}}}}";
        else if (InvalidOrders) json = "{\"data\":\"invalid\"}";
        else json = "{\"data\":[{\"id\":\"1\",\"type\":\"sell\",\"platinum\":1,\"quantity\":5,\"subtype\":\"radiant\",\"user\":{\"ingameName\":\"Badseller\",\"status\":\"online\"}},{\"id\":\"2\",\"type\":\"sell\",\"platinum\":20,\"quantity\":5,\"subtype\":\"radiant\",\"user\":{\"ingameName\":\"GoodSeller\",\"status\":\"online\"}}]}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
sealed class WorldFeedHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!.ToString(); var now = DateTimeOffset.UtcNow;
        string Mongo(DateTimeOffset value) => $"{{\"$date\":{{\"$numberLong\":\"{value.ToUnixTimeMilliseconds()}\"}}}}";
        var json = uri.EndsWith("/pc") ? "{\"timestamp\":\"2020-01-01T00:00:00Z\",\"fissures\":[]}" : uri.Contains("bounty-cycle") ? "{}" : uri.EndsWith("/solNodes") ? "{\"SolNode1\":{\"value\":\"Hydron (Sedna)\",\"type\":\"Defense\"}}" :
            $"{{\"Time\":{now.ToUnixTimeSeconds()},\"ActiveMissions\":[{{\"_id\":{{\"$oid\":\"fresh\"}},\"Activation\":{Mongo(now.AddMinutes(-1))},\"Expiry\":{Mongo(now.AddMinutes(10))},\"Node\":\"SolNode1\",\"MissionType\":\"MT_DEFENSE\",\"Modifier\":\"VoidT3\",\"Hard\":false}}]}}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
