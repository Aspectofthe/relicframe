using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

if (args.Contains("--profile-orders"))
{
    ProfileOrders(); return;
}
if (args.Contains("--profile-host"))
{
    var reference = Path.GetFullPath(Environment.GetEnvironmentVariable("RELICFRAME_DATA_DIR") ?? "relicframe/data");
    var isolated = Path.Combine(Path.GetTempPath(), "relicframe-profile-" + Guid.NewGuid().ToString("N"));
    var catalog = Relic.LoadCsv(Path.Combine(reference, "relics_from_official_data.csv"));
    using var profileHttp = new MarketHttp();
    await using var profileMarket = new LiveMarket(profileHttp, catalog, isolated);
    await using var profileRivens = new RivenMarket(profileHttp, isolated, Path.Combine(reference, "rivens", "roll_rules.json"));
    using var profileSocket = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds, MessageCacheSize = 0, AlwaysDownloadUsers = false });
    var commands = PreviewCommands.Build().Append(WorldManager.BuildCommand()).ToArray();
    GC.Collect(); GC.WaitForPendingFinalizers(); using var process = Process.GetCurrentProcess();
    Console.WriteLine($"[profile-host] disconnected preview; relics={catalog.Count}, command_groups={commands.Length}, rss_mb={process.WorkingSet64 / 1048576d:F1}, peak_mb={process.PeakWorkingSet64 / 1048576d:F1}");
    Console.WriteLine("No Discord login, live API request, private evidence load or channel changes performed. Not full-load RAM.");
    return;
}
if (!args.Contains("--test-bot"))
{
    Console.WriteLine("RelicFrame C# migration preview (.NET 10). Not a production replacement yet.");
    Console.WriteLine("--profile-orders: isolated synthetic memory benchmark; no network or secrets.");
    Console.WriteLine("--profile-host: disconnected Discord client plus public reference data; no login or requests.");
    Console.WriteLine("--test-bot: requires RELICFRAME_CSHARP_TOKEN and RELICFRAME_TEST_GUILD_ID.");
    Console.WriteLine("Only adds rf-* commands to the selected test guild; no global command replacement.");
    return;
}
var token = Environment.GetEnvironmentVariable("RELICFRAME_CSHARP_TOKEN");
if (string.IsNullOrWhiteSpace(token) || !ulong.TryParse(Environment.GetEnvironmentVariable("RELICFRAME_TEST_GUILD_ID"), out var guildId) || guildId == 0)
    throw new InvalidOperationException("Set RELICFRAME_CSHARP_TOKEN and RELICFRAME_TEST_GUILD_ID. Do not paste credentials into source or chat.");
var dataPath = Path.GetFullPath(Environment.GetEnvironmentVariable("RELICFRAME_DATA_DIR") ?? "relicframe/data");
var runtimePath = Path.GetFullPath(Environment.GetEnvironmentVariable("RELICFRAME_RUNTIME_DIR") ?? "csharp/runtime");
var relics = Relic.LoadCsv(Path.Combine(dataPath, "relics_from_official_data.csv"));
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
using var http = new MarketHttp();
await using var market = new LiveMarket(http, relics, runtimePath);
await using var rivens = new RivenMarket(http, runtimePath, Path.Combine(dataPath, "rivens", "roll_rules.json"));
using var socket = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds,
    MessageCacheSize = 0, AlwaysDownloadUsers = false, LogLevel = LogSeverity.Warning
});
await using var world = new WorldManager(socket, guildId, runtimePath, Environment.GetEnvironmentVariable("ARBITRATION_SCHEDULE_PATH") ?? "Untitled.txt");
socket.Log += message => { Console.WriteLine($"[discord] {message.Severity}: {message.Exception?.GetType().Name ?? message.Message}"); return Task.CompletedTask; };
var jobs = Channel.CreateBounded<SocketSlashCommand>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait });
var dispatcher = new PreviewCommands(relics, market, rivens, world, lifetime.Token, dataPath);
var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
{
    await foreach (var command in jobs.Reader.ReadAllAsync())
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var result = await dispatcher.ExecuteAsync(command, timeout.Token);
            await command.ModifyOriginalResponseAsync(m => { m.Content = result.Length <= 1900 ? result : result[..1897] + "…"; m.AllowedMentions = AllowedMentions.None; });
        }
        catch (Exception e)
        {
            Console.WriteLine($"[command] {command.CommandName}: {e.GetType().Name}");
            try { await command.ModifyOriginalResponseAsync(m => m.Content = e is OperationCanceledException
                ? "Operation timed out or was stopped. The bot is still available; use /rf-status."
                : "Operation failed. Check /rf-status and the host log. No result was fabricated."); } catch (Exception) { }
        }
    }
})).ToArray();
socket.SlashCommandExecuted += async command =>
{
    if (command.GuildId != guildId || command.CommandName is not ("rf-status" or "rf-relics" or "rf-companion" or "rf-riven" or "rf-world")) return;
    if (command.CommandName == "rf-status")
    {
        using var process = Process.GetCurrentProcess();
        await command.RespondAsync($"C# preview is responding. Gateway latency: {socket.Latency} ms.\n" +
            $"RAM: {process.WorkingSet64 / 1048576d:F1} MiB. Market: {market.Status}; books {market.ReadyBooks}/{market.TotalBooks}.\n" +
            $"Rivens: {rivens.Status}.\nWorld: {world.Status}.", ephemeral: true, allowedMentions: AllowedMentions.None);
        return;
    }
    // Acknowledge before dispatch, including when market initialization or calculations are busy.
    await command.DeferAsync(ephemeral: true);
    if (!jobs.Writer.TryWrite(command)) await command.ModifyOriginalResponseAsync(m => m.Content = "The calculation queue is full. Try again shortly; /rf-status remains available.");
};
var registration = new SemaphoreSlim(1, 1); bool registered = false;
async Task ConfigureGuild(SocketGuild guild)
{
    await registration.WaitAsync();
    try
    {
        if (registered) return;
        var existing = await guild.GetApplicationCommandsAsync();
        foreach (var command in PreviewCommands.Build().Append(WorldManager.BuildCommand()))
        {
            var slash = (SlashCommandProperties)command;
            var old = existing.FirstOrDefault(c => c.Name == slash.Name.Value);
            // Only replace this preview's exact rf-* names; never bulk-overwrite or touch production commands.
            if (old is not null) await old.DeleteAsync();
            await guild.CreateApplicationCommandAsync(command);
        }
        var result = await world.SetupAsync(guild, lifetime.Token); Console.WriteLine("[setup] " + result);
        if (result.StartsWith("Configured", StringComparison.Ordinal)) world.Start(lifetime.Token);
        registered = true; Console.WriteLine("[ready] C# preview commands registered in test guild only; market waits for /rf-relics refresh.");
    }
    finally { registration.Release(); }
}
socket.Ready += async () =>
{
    try { await ConfigureGuild(socket.GetGuild(guildId) ?? throw new InvalidOperationException("Test guild unavailable to this bot.")); }
    catch (Exception e) { Console.WriteLine($"[setup] failed: {e.GetType().Name}; check test guild and bot permissions"); }
};
socket.JoinedGuild += async guild =>
{
    if (guild.Id != guildId) return;
    try { await ConfigureGuild(guild); }
    catch (Exception e) { Console.WriteLine($"[guild-join] setup failed: {e.GetType().Name}; run /rf-world setup after fixing permissions"); }
};
try
{
    await socket.LoginAsync(TokenType.Bot, token); await socket.StartAsync();
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while (await timer.WaitForNextTickAsync(lifetime.Token))
    {
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"[memory] rss_mb={process.WorkingSet64 / 1048576d:F1} peak_mb={process.PeakWorkingSet64 / 1048576d:F1} market={market.Status}");
    }
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
finally
{
    lifetime.Cancel(); jobs.Writer.TryComplete();
    await market.StopAsync(); await rivens.StopAsync(); await world.StopAsync(); await Task.WhenAll(workers);
    await socket.StopAsync(); await socket.LogoutAsync(); registration.Dispose();
}

static void ProfileOrders()
{
    var books = new List<OrderBook>(); var watch = Stopwatch.StartNew();
    using var process = Process.GetCurrentProcess();
    Console.WriteLine($"[memory] baseline rss_mb={process.WorkingSet64 / 1048576d:F1}");
    for (var b = 0; b < 500; b++)
    {
        var rows = Enumerable.Range(0, 200).Select(s => new
        {
            id = $"{b:D8}{s:D16}", type = "sell", platinum = s + 1, quantity = 5, perTrade = 1,
            subtype = "radiant", visible = true, createdAt = "2026-09-03T00:00:00Z", updatedAt = "2026-09-03T01:00:00Z",
            user = new { id = $"{s:D24}", ingameName = $"Seller{s}", status = "ingame", avatar = "avatar/" + new string('a', 100), reputation = 30, locale = "en", platform = "pc" }
        });
        using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(rows));
        var book = new OrderBook(); book.Replace(doc.RootElement); books.Add(book);
    }
    GC.Collect(); GC.WaitForPendingFinalizers(); process.Refresh();
    Console.WriteLine($"[memory] 100000 orders rss_mb={process.WorkingSet64 / 1048576d:F1} peak_mb={process.PeakWorkingSet64 / 1048576d:F1} build_s={watch.Elapsed.TotalSeconds:F3}");
    watch.Restart(); var checksum = books.Sum(b => b.Match("radiant", true).Best?.Price ?? 0);
    Console.WriteLine($"[profile] books={books.Count} checksum={checksum} query_s={watch.Elapsed.TotalSeconds:F3}");
}

sealed class PreviewCommands(IReadOnlyDictionary<string, Relic> relics, LiveMarket market, RivenMarket rivens, WorldManager world, CancellationToken lifetime, string dataPath)
{
    private readonly Lazy<CompanionAppraiser> companion = new(() => new CompanionAppraiser(
        CompanionAppraiser.Load(Path.Combine(dataPath, "companion_current_market", "price_evidence_deduplicated.jsonl"))
        .Concat(CompanionAppraiser.Load(Path.Combine(dataPath, "companion_sales", "price_evidence.jsonl"), "historical", .08))));
    public static ApplicationCommandProperties[] Build()
    {
        var group = new SlashCommandBuilder().WithName("rf-relics").WithDescription("C# migration preview: relic tools");
        foreach (var name in new[] { "refresh", "stop", "list", "detail", "find", "odds", "compare", "buyn", "blacklist-add", "blacklist-remove", "blacklist-list", "help" })
        {
            var sub = new SlashCommandOptionBuilder().WithName(name).WithDescription($"C# preview: {name}").WithType(ApplicationCommandOptionType.SubCommand);
            if (name is "blacklist-add" or "blacklist-remove") sub.AddOption("seller", ApplicationCommandOptionType.String, "Seller in-game name (shared C# dataset)", true);
            if (name is "detail" or "odds" or "compare" or "buyn") sub.AddOption("relic", ApplicationCommandOptionType.String, "Exact relic name", true);
            if (name is "odds" or "find") sub.AddOption("reward", ApplicationCommandOptionType.String, "Reward name or search text", true);
            if (name is "odds" or "buyn") sub.AddOption("count", ApplicationCommandOptionType.Integer, "Number of relics, 1–1000", true, minValue: 1, maxValue: 1000);
            if (name == "buyn") sub.AddOption("approximate", ApplicationCommandOptionType.Boolean, "Use Monte Carlo instead of the exact distribution");
            if (name is "list" or "detail" or "find" or "odds" or "buyn")
            {
                sub.AddOption(new SlashCommandOptionBuilder().WithName("refinement").WithDescription("Defaults to Radiant").WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Intact", "intact").AddChoice("Exceptional", "exceptional").AddChoice("Flawless", "flawless").AddChoice("Radiant", "radiant"));
            }
            if (name == "list")
            {
                sub.AddOption("page", ApplicationCommandOptionType.Integer, "Page, eight results each", minValue: 1);
                var sort = new SlashCommandOptionBuilder().WithName("sort").WithDescription("Ranking mode").WithType(ApplicationCommandOptionType.String);
                foreach (var mode in new[] { "Best Overall", "Guaranteed Profit", "Expected Profit", "Best ROI", "Cheapest", "Best Plat/Trace", "Best Ducat Farming" }) sort.AddChoice(mode, mode);
                sub.AddOption(sort);
                sub.AddOption(new SlashCommandOptionBuilder().WithName("scope").WithDescription("Seller availability").WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Online only", "Online only").AddChoice("All sellers", "Offline only").AddChoice("Best of either", "Both (best of either)"));
                sub.AddOption(new SlashCommandOptionBuilder().WithName("vault").WithDescription("Vault status").WithType(ApplicationCommandOptionType.String)
                    .AddChoice("All", "All").AddChoice("Vaulted", "Vaulted").AddChoice("Unvaulted", "Unvaulted").AddChoice("Unknown", "Unknown"));
                sub.AddOption("min_roi", ApplicationCommandOptionType.Number, "Minimum expected ROI percentage");
                sub.AddOption("max_cost", ApplicationCommandOptionType.Number, "Maximum total cost", minValue: 0);
                sub.AddOption("min_reward", ApplicationCommandOptionType.Number, "Minimum best-reward value", minValue: 0);
                sub.AddOption("guaranteed_only", ApplicationCommandOptionType.Boolean, "Only positive profit for every reward outcome at listed prices");
            }
            group.AddOption(sub);
        }
        var appraise = new SlashCommandBuilder().WithName("rf-companion").WithDescription("C# preview: manual-trait comparable appraisal");
        foreach (var field in new[] { "species", "breed", "pattern", "build", "rarity", "color" }) appraise.AddOption(field, ApplicationCommandOptionType.String, $"Natural {field}", field == "species");
        return [new SlashCommandBuilder().WithName("rf-status").WithDescription("C# responsiveness and memory diagnostic").Build(), group.Build(), appraise.Build(), RivenCommands.Build()];
    }
    private static string Get(IEnumerable<SocketSlashCommandDataOption> options, string name, string fallback = "") => options.FirstOrDefault(o => o.Name == name)?.Value?.ToString() ?? fallback;
    private static string P(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unknown";
    public async Task<string> ExecuteAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (command.CommandName == "rf-world") return await world.CommandAsync(command, ct, lifetime);
        if (command.CommandName == "rf-riven") return await RivenCommands.ExecuteAsync(command, rivens, ct);
        if (command.CommandName == "rf-companion")
        {
            var request = command.Data.Options.ToDictionary(o => o.Name, o => (string?)o.Value.ToString());
            var appraisal = companion.Value.Appraise(request);
            return $"Imprint estimate: {appraisal.Low}–{appraisal.High} platinum (midpoint {appraisal.Estimate}).\n" +
                $"{appraisal.ComparableCount} comparables; confidence {appraisal.Confidence}. Listings are not confirmed sales.\n" +
                "Manual natural traits only in this preview; screenshot identification has not been ported.";
        }
        var sub = command.Data.Options.Single(); var options = sub.Options; var name = sub.Name;
        if (name.StartsWith("blacklist-"))
        {
            if (name == "blacklist-list") return "Excluded sellers: " + string.Join(", ", market.Blacklist.Names.DefaultIfEmpty("none"));
            if (command.User is not SocketGuildUser manager || !manager.GuildPermissions.ManageGuild) return "Manage Server permission is required.";
            return market.Blacklist.Change(Get(options, "seller"), name == "blacklist-add") ? "Seller exclusion updated for all C# relic calculations. Python data was not changed." : "Seller exclusion was already in that state.";
        }
        if (name is "refresh" or "stop")
        {
            if (command.User is not SocketGuildUser user || !user.GuildPermissions.ManageGuild) return "Manage Server permission is required.";
            if (name == "stop") { await market.StopAsync(); return "Shared C# market refresh stopped. Existing Python deployment was not touched."; }
            await market.StartAsync(true, CancellationToken.None); return "C# market refresh started in background. /rf-status stays available during bootstrap.";
        }
        if (name == "help") return "C# preview: /rf-status, /rf-relics refresh/stop/list/detail/find/odds/compare/buyn, /rf-companion.\n" +
            "Also available: /rf-riven refresh/stop/flips/price/top/guide.\nStill being ported: production auto-setup, live panels, opt-in pings, screenshot recognition, trade-chat imports and desktop tooling.\n" +
            "Do not replace the Python deployment with this preview yet.";
        var tier = Enum.Parse<Refinement>(Get(options, "refinement", "radiant"), true);
        var reward = Get(options, "reward"); var relicName = Get(options, "relic");
        var relic = relics.Values.FirstOrDefault(r => r.RelicName.Equals(relicName, StringComparison.OrdinalIgnoreCase));
        var n = int.Parse(Get(options, "count", "1"), CultureInfo.InvariantCulture);
        if (name == "find")
        {
            var results = relics.Values.SelectMany(r => r.Rewards.Where(w => w.RewardName.Contains(reward, StringComparison.OrdinalIgnoreCase)
                || reward.Contains(w.RewardName, StringComparison.OrdinalIgnoreCase)).Select(w => $"{r.RelicName}: {w.RewardName} ({Relic.Chance(tier, w.Rarity):0.##}%)"));
            return string.Join('\n', results.Take(20).DefaultIfEmpty("No matching rewards."));
        }
        if (name != "list" && relic is null) return "Relic not found. Use its exact name from the CSV, such as Lith A1.";
        if (name == "odds")
        {
            var hit = relic!.Rewards.FirstOrDefault(r => r.RewardName.Equals(reward, StringComparison.OrdinalIgnoreCase));
            return hit is null ? "That reward isn't in this relic." : $"{Relic.AtLeastOne(Relic.Chance(tier, hit.Rarity), n):0.##}% chance of at least one {hit.RewardName} in {n} independent opens.";
        }
        var snap = market.Snapshot(tier);
        if (snap.ReadyBooks == 0) return "No live prices yet. A server manager can start /rf-relics refresh; /rf-status reports progress.";
        var ageNote = $"\nMarket: {market.Status}; {snap.ReadyBooks}/{snap.TotalBooks} books loaded; oldest book <t:{snap.FetchedAt.ToUnixTimeSeconds()}:R>. Asks can change; profit is not guaranteed.";
        if (name == "list")
        {
            var scope = Get(options, "scope", "Both (best of either)");
            double? Number(string key) => double.TryParse(Get(options, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
            var rows = relics.Values.Select(r => RelicRow.Compute(r, tier, snap.Prices, snap.RelicPrices[r.RelicName], snap.Ducats, scope: scope))
                .Where(r => r.Passes(scope, Get(options, "vault", "All"), Number("min_roi"), Number("max_cost"), Number("min_reward"), bool.Parse(Get(options, "guaranteed_only", "false"))));
            var ranked = RelicRow.Rank(rows, Get(options, "sort", "Best Overall"), scope);
            var page = int.Parse(Get(options, "page", "1"), CultureInfo.InvariantCulture);
            return string.Join('\n', ranked.Skip((page - 1) * 8).Take(8).Select(r => $"{r.Relic.RelicName} · {r.OverallCategory} · online {P(r.Online.Cost)}p · EV {P(r.Online.Profit.ExpectedValue)}p · risk {r.BestRisk}/100").DefaultIfEmpty("No results on this page with those filters.")) + ageNote;
        }
        var info = snap.RelicPrices[relic!.RelicName];
        if (name == "compare") return string.Join('\n', relic.Compare(snap.Prices, info.Online ?? info.OfflineIncluded).Select(r => $"{r.Tier}: EV {P(r.ExpectedValue)}p, expected profit {P(r.ExpectedProfit)}p, traces {r.TraceCost}")) + ageNote;
        if (name == "buyn")
        {
            var entries = market.Match(relic.RelicName, tier.ToString(), true, true);
            var purchase = OrderMath.Cheapest(entries.Entries, n);
            if (!purchase.FullyFilled) return $"Only {purchase.UnitsFilled}/{n} units available in known online listings; no full-batch profit calculated.";
            BatchAnalysis analysis;
            try { analysis = relic.BuyN(tier, snap.Prices, info.Online, 0, n, purchase.TotalCost, exactThreshold: bool.Parse(Get(options, "approximate", "false")) ? 0 : 120, ct: ct); }
            catch (InvalidOperationException) { return "The exact distribution exceeded its memory budget. Retry /rf-relics buyn with approximate: True for a labelled Monte Carlo estimate."; }
            return $"{n} × {relic.RelicName}: total {P(purchase.TotalCost)}p, expected profit {P(analysis.ExpectedProfit)}p, loss chance {analysis.ProbLossPct:0.##}% ({(analysis.Approx ? "Monte Carlo estimate" : "exact distribution")}).\n" +
                $"Refinement match: {entries.SubtypeMatched}." + ageNote;
        }
        var row = RelicRow.Compute(relic, tier, snap.Prices, info, snap.Ducats);
        return $"{relic.RelicName} — {tier}\nOnline: {P(row.Online.Cost)}p; all sellers: {P(row.Offline.Cost)}p\n" +
            $"Expected return: {P(row.Online.Profit.ExpectedValue)}p; online expected profit: {(row.Online.Profit.PriceKnown ? P(row.Online.Profit.ExpectedProfit) : "unknown")}p\n" +
            $"Online refinement match: {row.Online.Matched}; outlier: {row.Online.Outlier}\n" +
            string.Join('\n', relic.TopRewards(tier, snap.Prices, 6).Select(r => $"{r.Name}: {P(r.Price)}p / {r.ChancePct:0.##}%")) + ageNote;
    }
}
