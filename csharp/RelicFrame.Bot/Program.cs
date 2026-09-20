using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

if (args is ["--profile-ocr", var ocrImage])
{
    var profile = TradeChatScreenCollector.ProfileFile(ocrImage);
    Console.WriteLine($"[profile-ocr] confidence={profile.Confidence:P0}, candidate_trade_messages={profile.CandidateMessages}. No text or image was retained.");
    return;
}
if (args is ["--profile-riven-ocr", var rivenImage])
{
    var weaponPath = Path.GetFullPath(Path.Combine("relicframe", "data", "rivens", "weapons.json"));
    using var weaponDocument = JsonDocument.Parse(await File.ReadAllTextAsync(weaponPath));
    var weaponNames = weaponDocument.RootElement.Get("rows").Rows().Select(row => row.Get("i18n").Get("en").Get("name").Text()).Where(name => name.Length > 0).ToArray();
    using var rivenOcr = new RivenLocalOcr();
    var draft = await rivenOcr.AnalyzeBytesAsync(await File.ReadAllBytesAsync(Path.GetFullPath(rivenImage)), weaponNames, default);
    Console.WriteLine($"[profile-riven-ocr] confidence={draft.Confidence:P0} weapon={draft.WeaponName} rank={draft.ModRank?.ToString() ?? "?"}/8 mr={draft.MasteryRank?.ToString() ?? "?"} rerolls={draft.Rerolls?.ToString() ?? "?"}");
    Console.WriteLine("positives=" + string.Join(" | ", draft.Positives.Select(stat => $"{stat.Value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?"} {RivenPricing.DisplayName(stat.Name)}")));
    Console.WriteLine("negative=" + (draft.Negative is null ? "none" : $"{draft.Negative.Value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?"} {RivenPricing.DisplayName(draft.Negative.Name)}"));
    Console.WriteLine("notes=" + string.Join(' ', draft.Notes));
    return;
}
if (args is ["--profile-inventory", var inventoryFile])
{
    var rows = PrimeInventory.Load(Path.GetFullPath(inventoryFile));
    Console.WriteLine($"[profile-inventory] rows={rows.Count}, total_units={rows.Sum(row => row.Quantity)}. No item names, inventory contents, or account tokens were printed.");
    return;
}
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
    var commands = PreviewCommands.Build().Concat(PreviewCommands.BuildLegacy()).Append(WorldManager.BuildCommand()).Append(WorldManager.BuildCommand("world")).Append(RelicPanelManager.BuildCommand()).ToArray();
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
    Console.WriteLine("--profile-ocr <image>: local OCR confidence/candidate diagnostic; does not print or retain recognized text.");
    Console.WriteLine("--profile-riven-ocr <image>: local multi-pass Riven OCR diagnostic; prints structured fields and retains nothing.");
    Console.WriteLine("--profile-inventory <json-or-lastData.dat>: local inventory parser diagnostic; prints counts only.");
    Console.WriteLine("--test-bot: requires RELICFRAME_CSHARP_TOKEN and RELICFRAME_TEST_GUILD_ID.");
    Console.WriteLine("Only adds rf-* commands to the selected test guild; no global command replacement.");
    return;
}
var token = Environment.GetEnvironmentVariable("RELICFRAME_CSHARP_TOKEN");
if (string.IsNullOrWhiteSpace(token) || !ulong.TryParse(Environment.GetEnvironmentVariable("RELICFRAME_TEST_GUILD_ID"), out var guildId) || guildId == 0)
    throw new InvalidOperationException("Set RELICFRAME_CSHARP_TOKEN and RELICFRAME_TEST_GUILD_ID. Do not paste credentials into source or chat.");
var dataPath = Path.GetFullPath(Environment.GetEnvironmentVariable("RELICFRAME_DATA_DIR") ?? "relicframe/data");
var runtimePath = Path.GetFullPath(Environment.GetEnvironmentVariable("RELICFRAME_RUNTIME_DIR") ?? "csharp/runtime");
var relics = Relic.LoadCsv(Path.Combine(dataPath, "relics_from_official_data.csv"), includeRequiem: false);
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
using var http = new MarketHttp();
await using var market = new LiveMarket(http, relics, runtimePath,
    !string.Equals(Environment.GetEnvironmentVariable("RELICFRAME_WFM_WEBSOCKET"), "false", StringComparison.OrdinalIgnoreCase));
await using var rivens = new RivenMarket(http, runtimePath, Path.Combine(dataPath, "rivens", "roll_rules.json"));
var tradeChat = new RivenTradeChat(Path.Combine(runtimePath, "riven_trade_chat.jsonl"), () => rivens.WeaponNames);
var eeLogEnabled = string.Equals(Environment.GetEnvironmentVariable("RELICFRAME_EE_LOG"), "true", StringComparison.OrdinalIgnoreCase);
var eeLogPath = HostPaths.EeLog(runtimePath);
await using var eeTradeChat = new EeLogTradeChatCollector(eeLogPath, Path.Combine(runtimePath, "ee_log_cursor.json"), tradeChat);
if (eeLogEnabled) eeTradeChat.Start(lifetime.Token);
var screenOcrEnabled = string.Equals(Environment.GetEnvironmentVariable("RELICFRAME_TRADE_OCR"), "true", StringComparison.OrdinalIgnoreCase);
var screenOcrSeconds = int.TryParse(Environment.GetEnvironmentVariable("RELICFRAME_TRADE_OCR_SECONDS"), out var parsedOcrSeconds) ? parsedOcrSeconds : 5;
await using var screenTradeChat = new TradeChatScreenCollector(tradeChat, Path.Combine(runtimePath, "trade_chat_screen.jsonl"), Environment.GetEnvironmentVariable("RELICFRAME_TRADE_OCR_REGION"), screenOcrSeconds);
if (screenOcrEnabled) screenTradeChat.Start(lifetime.Token);
using var socket = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
    MessageCacheSize = 0, AlwaysDownloadUsers = false, LogLevel = LogSeverity.Warning
});
await using var world = new WorldManager(socket, guildId, runtimePath, Environment.GetEnvironmentVariable("ARBITRATION_SCHEDULE_PATH") ?? "Untitled.txt");
await using var panel = new RelicPanelManager(socket, market, relics, guildId, runtimePath);
await using var personalMarket = new PersonalMarketManager(socket, market, http, guildId, runtimePath);
await using var primeSetCompletion = new PrimeSetCompletionManager(socket, market, http, personalMarket, guildId, runtimePath);
await using var arcaneEconomy = new ArcaneEconomyManager(socket, market, http, guildId, runtimePath);
await using var primeVendors = new PrimeVendorManager(socket, market, http, relics, guildId, runtimePath);
await using var maxedMods = new MaxedModManager(socket, market, http, guildId, runtimePath);
await using var syndicates = new SyndicateManager(socket, market, http, guildId, runtimePath);
var rivenEvidence = new RivenEvidenceControls(socket, rivens, guildId);
using var rivenImages = new RivenImageWorkflow(socket, rivens, tradeChat, rivenEvidence, guildId);
var rivenPanel = new RivenAppraisalPanel(socket, rivens, tradeChat, rivenEvidence, guildId, runtimePath);

static string FormatEta(TimeSpan value)
{
    var seconds = Math.Max(1, (int)Math.Ceiling(value.TotalSeconds));
    if (seconds < 60) return $"{seconds}s";
    if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
    return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
}

string OverallLoadingStatus()
{
    var marketRemaining = market.BootstrapRemaining;
    var rivenRemaining = rivens.EstimatedRemaining;
    var arcaneRemaining = arcaneEconomy.EstimatedRemaining;
    if (marketRemaining is { } marketEta)
    {
        // Market work has priority on the shared five-request-per-second budget, so
        // unfinished low-priority Riven work is conservatively treated as a tail.
        var rivenTail = rivenRemaining ?? (rivens.Status.StartsWith("Complete", StringComparison.OrdinalIgnoreCase) ? TimeSpan.Zero : TimeSpan.FromSeconds(15));
        return $"loading; ETA ~{FormatEta(marketEta + rivenTail)} (market bootstrap, then remaining Riven work)";
    }
    if (market.IsBootstrapping) return "loading; ETA calculating (market bootstrap)";
    if (rivenRemaining is { } rivenEta && arcaneRemaining is { } arcaneEta)
        return $"loading; ETA ~{FormatEta(rivenEta > arcaneEta ? rivenEta : arcaneEta)} (Riven {rivens.ProgressStage}; Arcane books in parallel)";
    if (arcaneRemaining is { } arcaneOnlyEta) return $"loading; ETA ~{FormatEta(arcaneOnlyEta)} (Arcane books)";
    if (rivenRemaining is { } rivenOnlyEta) return $"loading; ETA ~{FormatEta(rivenOnlyEta)} (Riven {rivens.ProgressStage})";
    var rivenBusy = !rivens.Status.StartsWith("Complete", StringComparison.OrdinalIgnoreCase)
        && !rivens.Status.StartsWith("Stopped", StringComparison.OrdinalIgnoreCase)
        && !rivens.Status.StartsWith("Scan failed", StringComparison.OrdinalIgnoreCase);
    return rivenBusy ? $"loading; ETA calculating (Riven {rivens.ProgressStage})"
        : maxedMods.Status.StartsWith("pricing", StringComparison.Ordinal) ? $"loading; {maxedMods.Status}"
        : syndicates.Status.StartsWith("pricing", StringComparison.Ordinal) ? $"loading; Syndicate {syndicates.Status}" : "ready";
}

socket.Log += message =>
{
    var detail = message.Exception is null
        ? message.Message
        : $"{message.Message}: {message.Exception.GetType().Name}: {message.Exception.Message}";
    Console.WriteLine($"[discord] {message.Severity}: {detail}");
    return Task.CompletedTask;
};
var jobs = Channel.CreateBounded<SocketSlashCommand>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait });
var dispatcher = new PreviewCommands(relics, market, rivens, tradeChat, rivenEvidence, rivenImages, world, panel, lifetime.Token);
var previewCommands = new HashSet<string>(["rf-status", "rf-relics", "rf-riven", "rf-world", "rf-panel", "relics", "riven", "world"], StringComparer.Ordinal);
var lastCommandError = "none";
const int commandWorkerCount = 3;
var workers = Enumerable.Range(0, commandWorkerCount).Select(_ => Task.Run(async () =>
{
    await foreach (var command in jobs.Reader.ReadAllAsync())
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var result = await dispatcher.ExecuteAsync(command, timeout.Token);
            await command.ModifyOriginalResponseAsync(m =>
            {
                m.Content = result.Length <= 1900 ? result : result[..1897] + "…"; m.AllowedMentions = AllowedMentions.None;
                if (rivenEvidence.Take(command.Id) is { } components) m.Components = components;
            });
        }
        catch (Exception e)
        {
            var errorId = DateTimeOffset.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
            lastCommandError = $"{errorId} /{command.CommandName}: {e.GetType().Name}";
            Console.WriteLine($"[command:{errorId}] /{command.CommandName}\n{e}");
            var response = e switch
            {
                OperationCanceledException => "Operation timed out or was stopped. The bot is still available; use /rf-status.",
                InvalidOperationException or InvalidDataException or ArgumentException => $"Could not complete the command: {e.Message}",
                Discord.Net.HttpException => $"Discord rejected the operation. Check this bot's channel/role permissions and role position. Error reference: {errorId}.",
                _ => $"Operation failed. Error reference: {errorId}. Check /rf-status and the host log; no result was fabricated."
            };
            try { await command.ModifyOriginalResponseAsync(m => m.Content = response.Length <= 1900 ? response : response[..1897] + "…"); }
            catch (Exception responseError) { Console.WriteLine($"[command:{errorId}] Could not update the interaction response: {responseError}"); }
        }
    }
})).ToArray();
socket.SlashCommandExecuted += async command =>
{
    if (command.GuildId != guildId) return;
    try
    {
        if (!previewCommands.Contains(command.CommandName))
        {
            await command.RespondAsync($"`/{command.CommandName}` is an older command still registered to this Discord application. This C# preview uses `/rf-relics`, `/rf-world`, `/rf-panel`, `/rf-riven`, and `/rf-status`.", ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        if (command.CommandName == "rf-status")
        {
            using var process = Process.GetCurrentProcess();
            await command.RespondAsync($"C# preview is responding. Gateway latency: {socket.Latency} ms.\n" +
                $"Overall loading: {OverallLoadingStatus()}.\n" +
                $"RAM: {process.WorkingSet64 / 1048576d:F1} MiB. Commands: {(jobs.Reader.CanCount ? jobs.Reader.Count : 0)}/16 queued; {commandWorkerCount} workers. Market: {market.Status}; refreshed {market.RefreshedBooks}/{market.TotalBooks}, available {market.ReadyBooks}/{market.TotalBooks}; WebSocket {market.WebSocketStatus}.\n" +
                $"Rivens: {rivens.Status}.\nOutgoing Trade Chat: {(eeLogEnabled ? eeTradeChat.Status : "EE.log collector disabled")}\n" +
                $"Visible Trade Chat: {(screenOcrEnabled ? screenTradeChat.Status : "screen OCR disabled")}\nWorld: {world.Status}. Panel: {panel.Status}. Personal Market: {personalMarket.Status}. Arcane Economy: {arcaneEconomy.Status}. Prime vendors: {primeVendors.Status}. Maxed mods: {maxedMods.Status}. Syndicates: {syndicates.Status}.\nLast command error: {lastCommandError}.", ephemeral: true, allowedMentions: AllowedMentions.None);
            return;
        }
        // Acknowledge before dispatch, including when market initialization or calculations are busy.
        await command.DeferAsync(ephemeral: true);
        if (!jobs.Writer.TryWrite(command)) await command.ModifyOriginalResponseAsync(m => m.Content = "The calculation queue is full. Try again shortly; /rf-status remains available.");
    }
    catch (Exception e)
    {
        var errorId = DateTimeOffset.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
        lastCommandError = $"{errorId} /{command.CommandName}: {e.GetType().Name}";
        Console.WriteLine($"[interaction:{errorId}] /{command.CommandName}\n{e}");
        try
        {
            if (command.HasResponded) await command.ModifyOriginalResponseAsync(m => m.Content = $"Discord interaction failed. Error reference: {errorId}. Check the host log.");
            else await command.RespondAsync($"Discord interaction failed. Error reference: {errorId}. Check the host log.", ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        catch (Exception responseError) { Console.WriteLine($"[interaction:{errorId}] Could not send the failure response: {responseError}"); }
    }
};
socket.AutocompleteExecuted += async interaction =>
{
    try
    {
        if (interaction.GuildId != guildId) return;
        var current = interaction.Data.Current; var query = current.Value?.ToString() ?? "";
        var weaponQuery = FindInteractionOption(interaction.Data.Options, "weapon")?.ToString();
        var statClass = weaponQuery is null ? null : rivens.TryGetStatClass(weaponQuery);
        var isPositive = current.Name is "positive_1" or "positive_2" or "positive_3";
        var usedStatKeys = interaction.Data.Options.Where(option => option.Name is "positive_1" or "positive_2" or "positive_3" or "negative")
            .Select(option => RivenPricing.Key(option.Value?.ToString() ?? "")).Where(key => key.Length > 0).ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> source = current.Name switch
        {
            "relic" => relics.Keys,
            "reward" or "item" => relics.Values.SelectMany(relic => relic.Rewards).Select(reward => reward.RewardName).Distinct(StringComparer.OrdinalIgnoreCase),
            "weapon" => rivens.WeaponNames.Distinct(StringComparer.OrdinalIgnoreCase),
            "positive_1" or "positive_2" or "positive_3" or "negative" when statClass is not null =>
                RivenPricing.AllowedStats(statClass, isPositive).Select(RivenPricing.DisplayName),
            "positive_1" or "positive_2" or "positive_3" or "negative" =>
                RivenPricing.AllStats.Select(RivenPricing.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase),
            _ => []
        };
        if (current.Name is "positive_1" or "positive_2" or "positive_3" or "negative")
            source = source.Where(value => !usedStatKeys.Contains(RivenPricing.Key(value)));
        var matches = source.Where(value => value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(25).Select(value => new AutocompleteResult(value, value));
        await interaction.RespondAsync(matches);
    }
    catch (Exception e) { Console.WriteLine($"[autocomplete] {e}"); }
};

static object? FindInteractionOption(IEnumerable<AutocompleteOption> options, string name) =>
    options.FirstOrDefault(option => option.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
var registration = new SemaphoreSlim(1, 1); bool registered = false;
async Task ConfigureGuild(SocketGuild guild)
{
    await registration.WaitAsync();
    try
    {
        if (registered) return;
        var existing = await guild.GetApplicationCommandsAsync();
        foreach (var removed in existing.Where(command => command.Name is "rf-companion" or "companion")) await removed.DeleteAsync();
        foreach (var command in PreviewCommands.Build().Concat(PreviewCommands.BuildLegacy()).Append(WorldManager.BuildCommand()).Append(WorldManager.BuildCommand("world")).Append(RelicPanelManager.BuildCommand()))
        {
            var slash = (SlashCommandProperties)command;
            var old = existing.FirstOrDefault(c => c.Name == slash.Name.Value);
            // Only replace this preview's exact rf-* names; never bulk-overwrite or touch production commands.
            if (old is not null) await old.DeleteAsync();
            await guild.CreateApplicationCommandAsync(command);
        }
        var result = await world.SetupAsync(guild, lifetime.Token); Console.WriteLine("[setup] " + result);
        if (result.StartsWith("Configured", StringComparison.Ordinal)) world.Start(lifetime.Token);
        var panelResult = await panel.SetupAsync(guild, lifetime.Token, lifetime.Token); Console.WriteLine("[setup] " + panelResult);
        var rivenPanelResult = await rivenPanel.SetupAsync(guild, lifetime.Token); Console.WriteLine("[setup] " + rivenPanelResult);
        rivenImages.SetAppraisalChannel(rivenPanel.AppraisalChannelId);
        var personalResult = await personalMarket.SetupAsync(guild, lifetime.Token, lifetime.Token); Console.WriteLine("[setup] " + personalResult);
        var completionResult = await primeSetCompletion.SetupAsync(guild, lifetime.Token, lifetime.Token); Console.WriteLine("[setup] " + completionResult);
        var arcaneResult = await arcaneEconomy.SetupAsync(guild, lifetime.Token, lifetime.Token); Console.WriteLine("[setup] " + arcaneResult);
        var vendorResult = await primeVendors.SetupAsync(guild, lifetime.Token, lifetime.Token); Console.WriteLine("[setup] " + vendorResult);
        var modResult = await maxedMods.SetupAsync(guild, lifetime.Token); Console.WriteLine("[setup] " + modResult);
        var syndicateResult = await syndicates.SetupAsync(guild, lifetime.Token); Console.WriteLine("[setup] " + syndicateResult);
        await rivens.StartAsync(lifetime.Token);
        registered = true; Console.WriteLine("[ready] C# preview commands registered in test guild only; market waits for /rf-relics refresh.");
    }
    finally { registration.Release(); }
}
socket.Ready += async () =>
{
    _ = Task.Run(async () =>
    {
        try
        {
            var guild = socket.GetGuild(guildId) ?? throw new InvalidOperationException("Test guild unavailable to this bot.");
            for (var attempt = 0; attempt < 20 && (socket.CurrentUser is null || guild.GetUser(socket.CurrentUser.Id) is null); attempt++)
                await Task.Delay(250, lifetime.Token);
            await ConfigureGuild(guild);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) { Console.WriteLine($"[setup] failed\n{e}"); }
    });
    await Task.CompletedTask;
};
socket.JoinedGuild += async guild =>
{
    if (guild.Id != guildId) return;
    _ = Task.Run(async () =>
    {
        try { await ConfigureGuild(guild); }
        catch (Exception e) { Console.WriteLine($"[guild-join] setup failed\n{e}"); }
    });
    await Task.CompletedTask;
};
try
{
    await socket.LoginAsync(TokenType.Bot, token); await socket.StartAsync();
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while (await timer.WaitForNextTickAsync(lifetime.Token))
    {
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"[memory] rss_mb={process.WorkingSet64 / 1048576d:F1} peak_mb={process.PeakWorkingSet64 / 1048576d:F1} overall={OverallLoadingStatus()} market={market.Status} books_refreshed={market.RefreshedBooks}/{market.TotalBooks} books_available={market.ReadyBooks}/{market.TotalBooks} set_completion={primeSetCompletion.Status} arcane_economy={arcaneEconomy.Status}");
    }
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
finally
{
    lifetime.Cancel(); jobs.Writer.TryComplete();
    await syndicates.StopAsync(); await maxedMods.StopAsync(); await primeVendors.StopAsync(); await arcaneEconomy.StopAsync(); await primeSetCompletion.StopAsync(); await personalMarket.StopAsync(); await market.StopAsync(); await rivens.StopAsync(); await world.StopAsync(); await panel.StopAsync(); await Task.WhenAll(workers);
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

sealed class PreviewCommands(IReadOnlyDictionary<string, Relic> relics, LiveMarket market, RivenMarket rivens, RivenTradeChat tradeChat, RivenEvidenceControls rivenEvidence, RivenImageWorkflow rivenImages, WorldManager world, RelicPanelManager panel, CancellationToken lifetime)
{
    public static ApplicationCommandProperties[] Build()
        => [new SlashCommandBuilder().WithName("rf-status").WithDescription("C# responsiveness and memory diagnostic").Build(), BuildRelics("rf-relics"), RivenCommands.Build()];
    public static ApplicationCommandProperties[] BuildLegacy()
        => [BuildRelics("relics"), RivenCommands.Build("riven")];
    private static ApplicationCommandProperties BuildRelics(string commandName)
    {
        var group = new SlashCommandBuilder().WithName(commandName).WithDescription("Relic profitability tools and persistent lists");
        foreach (var name in new[] { "refresh", "stop", "status", "setup-list", "list", "detail", "find", "odds", "compare", "buyn", "blacklist-add", "blacklist-remove", "blacklist-list", "help" })
        {
            var sub = new SlashCommandOptionBuilder().WithName(name).WithDescription($"Relic tools: {name}").WithType(ApplicationCommandOptionType.SubCommand);
            if (name is "blacklist-add" or "blacklist-remove") sub.AddOption("username", ApplicationCommandOptionType.String, "Seller's exact in-game name", true);
            if (name is "detail" or "odds" or "compare" or "buyn") sub.AddOption(new SlashCommandOptionBuilder().WithName("relic").WithDescription("Relic name").WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true));
            if (name == "odds") sub.AddOption(new SlashCommandOptionBuilder().WithName("reward").WithDescription("Reward name or search text").WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true));
            if (name == "find") sub.AddOption(new SlashCommandOptionBuilder().WithName("item").WithDescription("Reward name or search text").WithType(ApplicationCommandOptionType.String).WithRequired(true).WithAutocomplete(true));
            if (name == "buyn") sub.AddOption("n", ApplicationCommandOptionType.Integer, "Number of relics, 1–500", true, minValue: 1, maxValue: 500);
            if (name == "buyn") sub.AddOption("approximate", ApplicationCommandOptionType.Boolean, "Use Monte Carlo instead of the exact distribution");
            if (name is "list" or "detail" or "find" or "odds" or "buyn")
            {
                sub.AddOption(new SlashCommandOptionBuilder().WithName("refinement").WithDescription("Defaults to Radiant").WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Intact", "intact").AddChoice("Exceptional", "exceptional").AddChoice("Flawless", "flawless").AddChoice("Radiant", "radiant"));
            }
            if (name == "list")
            {
                sub.AddOption("page", ApplicationCommandOptionType.Integer, "Page, eight results each", minValue: 1);
                var sort = new SlashCommandOptionBuilder().WithName("rank_by").WithDescription("Ranking mode").WithType(ApplicationCommandOptionType.String);
                foreach (var mode in new[] { "Best Overall", "Guaranteed Profit", "Expected Profit", "Best ROI", "Best Win Chance", "Lowest Risk", "Cheapest", "Best Plat/Trace", "Best Ducat Farming" }) sort.AddChoice(mode, mode);
                sub.AddOption(sort);
                sub.AddOption(new SlashCommandOptionBuilder().WithName("channel").WithDescription("Seller availability").WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Online + Offline", "Online + Offline").AddChoice("Online only", "Online only").AddChoice("Offline only", "Offline only"));
                sub.AddOption(new SlashCommandOptionBuilder().WithName("vault").WithDescription("Vault status").WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Any", "Any").AddChoice("Vaulted", "Vaulted").AddChoice("Unvaulted", "Unvaulted").AddChoice("Unknown", "Unknown"));
                sub.AddOption("min_roi", ApplicationCommandOptionType.Number, "Minimum expected ROI percentage");
                sub.AddOption("max_cost", ApplicationCommandOptionType.Number, "Maximum total cost", minValue: 0);
                sub.AddOption("min_reward", ApplicationCommandOptionType.Number, "Minimum best-reward value", minValue: 0);
                sub.AddOption("guaranteed_only", ApplicationCommandOptionType.Boolean, "Only positive profit for every reward outcome at listed prices");
            }
            if (name is "list" or "detail" or "compare" or "buyn")
                sub.AddOption("trace_rate", ApplicationCommandOptionType.Number, "Platinum value per Void Trace", minValue: 0);
            if (name == "find") sub.AddOption("min_roi", ApplicationCommandOptionType.Number, "Minimum expected ROI percentage");
            if (name == "compare") sub.AddOption(new SlashCommandOptionBuilder().WithName("objective").WithDescription("What to optimize").WithType(ApplicationCommandOptionType.String)
                .AddChoice("Expected Profit", "expected_profit").AddChoice("ROI", "expected_roi_pct")
                .AddChoice("Guaranteed Profit", "worst_case_profit").AddChoice("Chance of Profit", "chance_of_profit_pct"));
            if (name == "refresh")
            {
                sub.AddOption(new SlashCommandOptionBuilder().WithName("refinement").WithDescription("Default presentation refinement").WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Intact", "intact").AddChoice("Exceptional", "exceptional").AddChoice("Flawless", "flawless").AddChoice("Radiant", "radiant"));
                sub.AddOption("force_catalog_refresh", ApplicationCommandOptionType.Boolean, "Re-download the marketplace catalog");
                sub.AddOption(new SlashCommandOptionBuilder().WithName("auto").WithDescription("Start, stop, or inspect continuous reconciliation").WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Start", "on").AddChoice("Stop", "off").AddChoice("Status", "status"));
            }
            group.AddOption(sub);
        }
        return group.Build();
    }
    private static string Get(IEnumerable<SocketSlashCommandDataOption> options, string name, string fallback = "") => options.FirstOrDefault(o => o.Name == name)?.Value?.ToString() ?? fallback;
    private static string P(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unknown";
    public async Task<string> ExecuteAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (command.CommandName is "rf-world" or "world") return await world.CommandAsync(command, ct, lifetime);
        if (command.CommandName == "rf-panel") return await panel.CommandAsync(command, ct, lifetime);
        if (command.CommandName is "rf-riven" or "riven") return await RivenCommands.ExecuteAsync(command, rivens, tradeChat, rivenEvidence, rivenImages, ct);
        var sub = command.Data.Options.Single(); var options = sub.Options; var name = sub.Name;
        if (name == "setup-list")
        {
            if (command.User is not SocketGuildUser setupUser || !setupUser.GuildPermissions.ManageGuild) return "Manage Server permission is required.";
            var guild = setupUser.Guild;
            return await panel.SetupAsync(guild, ct, lifetime);
        }
        if (name.StartsWith("blacklist-"))
        {
            if (name == "blacklist-list") return "Excluded sellers: " + string.Join(", ", market.Blacklist.Names.DefaultIfEmpty("none"));
            if (command.User is not SocketGuildUser manager || !manager.GuildPermissions.ManageGuild) return "Manage Server permission is required.";
            return market.Blacklist.Change(Get(options, "username"), name == "blacklist-add") ? "Seller exclusion updated for all C# relic calculations. Python data was not changed." : "Seller exclusion was already in that state.";
        }
        if (name == "status")
        {
            var oldest = market.OldestSuccessfulBook is not { } fetched ? "never" : $"<t:{fetched.ToUnixTimeSeconds()}:R>";
            return $"Relic market: {market.Status}. Books: {market.ReadyBooks}/{market.TotalBooks}; oldest update {oldest}.\n" +
                $"WebSocket: {market.WebSocketStatus}. THE LIST: {panel.Status}. Excluded sellers: {market.Blacklist.Names.Count()}.\n" +
                "The C# market continuously reconciles while running; it does not require a separate timed auto-refresh job.";
        }
        if (name is "refresh" or "stop")
        {
            if (command.User is not SocketGuildUser user || !user.GuildPermissions.ManageGuild) return "Manage Server permission is required.";
            var auto = Get(options, "auto");
            if (name == "stop" || auto == "off") { await market.StopAsync(); return "Shared C# market refresh stopped. Existing prices and list messages were retained."; }
            if (auto == "status") return $"Continuous C# reconciliation is {market.Status}; refreshed this run {market.RefreshedBooks}/{market.TotalBooks}, available now {market.ReadyBooks}/{market.TotalBooks}; WebSocket {market.WebSocketStatus}.";
            var forceCatalog = bool.TryParse(Get(options, "force_catalog_refresh", "true"), out var force) && force;
            await market.StartAsync(forceCatalog, CancellationToken.None);
            return "C# market refresh started in the background. It remains running and continuously reconciles prices; /relics status and /rf-status stay available during bootstrap.";
        }
        if (name == "help") return "Commands: /relics refresh/stop/status/setup-list/list/detail/find/odds/compare/buyn and blacklist-add/remove/list.\n" +
            "THE LIST contains nine interactive online-only rankings, including Best Win Chance, plus #how-it-works and updates in place. Select a relic for drops or seller /w text; /relics list also supports refinement, source, vault, ROI/cost/reward filters, trace cost, and paging.\n" +
            "Also available: /riven and /world plus the rf-* aliases. Riven screenshots and visible incoming Trade Chat use local OCR only.\n" +
            "Do not replace the Python deployment with this preview yet.";
        var tier = Enum.Parse<Refinement>(Get(options, "refinement", "radiant"), true);
        var reward = Get(options, "reward", Get(options, "item")); var relicName = Get(options, "relic");
        var relic = relics.Values.FirstOrDefault(r => r.RelicName.Equals(relicName, StringComparison.OrdinalIgnoreCase));
        var n = int.Parse(Get(options, "n", "1"), CultureInfo.InvariantCulture);
        if (name == "find")
        {
            var findSnapshot = market.Snapshot(tier);
            var minimumRoi = double.TryParse(Get(options, "min_roi"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRoi) ? parsedRoi : (double?)null;
            var results = relics.Values.SelectMany(r => r.Rewards.Where(w => w.RewardName.Contains(reward, StringComparison.OrdinalIgnoreCase)
                    || reward.Contains(w.RewardName, StringComparison.OrdinalIgnoreCase)).Select(w =>
                {
                    var chance = Relic.Chance(tier, w.Rarity); var expectedOpens = chance > 0 ? 100 / chance : (double?)null;
                    var info = findSnapshot.RelicPrices.GetValueOrDefault(r.RelicName) ?? new PriceInfo();
                    var row = RelicRow.Compute(r, tier, findSnapshot.Prices, info, findSnapshot.Ducats);
                    var cost = info.Online ?? info.OfflineIncluded;
                    var profit = info.Online.HasValue ? row.Online.Profit : row.Offline.Profit;
                    return new { Relic = r, Reward = w, Chance = chance, ExpectedOpens = expectedOpens, Cost = cost,
                        CostPerCopy = cost.HasValue && expectedOpens.HasValue ? cost * expectedOpens : null, Roi = profit.ExpectedRoiPct };
                }))
                .Where(x => !minimumRoi.HasValue || x.Roi >= minimumRoi)
                .OrderBy(x => x.CostPerCopy ?? double.MaxValue).ThenBy(x => x.Relic.RelicName, StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
            return string.Join('\n', results.Select(x => $"{ApplicationEmojis.Relic(x.Relic.RelicName, tier)} **{x.Relic.RelicName}** · {x.Reward.RewardName} ({x.Reward.Rarity}, {x.Chance:0.##}%) · exp. opens {P(x.ExpectedOpens)} · relic {P(x.Cost)}p · exp. cost/copy **{P(x.CostPerCopy)}p** · ROI {P(x.Roi)}%")
                .DefaultIfEmpty("No matching rewards met those filters."));
        }
        if (name != "list" && relic is null) return "Relic not found. Use its exact name from the CSV, such as Lith A1.";
        if (name == "odds")
        {
            var hit = relic!.Rewards.FirstOrDefault(r => r.RewardName.Contains(reward, StringComparison.OrdinalIgnoreCase)
                || reward.Contains(r.RewardName, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return "That reward isn't in this relic.";
            var chance = Relic.Chance(tier, hit.Rarity);
            return $"**{hit.RewardName} from {relic.RelicName}** · {chance:0.##}% per {tier} open\n```\n" +
                string.Join('\n', new[] { 1, 3, 6, 10, 20, 50 }.Select(count => $"{count,3} opens: {Relic.AtLeastOne(chance, count),5:0.0}%")) + "\n```";
        }
        var snap = market.Snapshot(tier);
        if (snap.ReadyBooks == 0) return "No live prices yet. A server manager can start /rf-relics refresh; /rf-status reports progress.";
        var ageNote = $"\nMarket: {market.Status}; {snap.ReadyBooks}/{snap.TotalBooks} books loaded; oldest book <t:{snap.FetchedAt.ToUnixTimeSeconds()}:R>. Asks can change; profit is not guaranteed.";
        if (name == "list")
        {
            var scope = Get(options, "channel", "Online + Offline");
            double? Number(string key) => double.TryParse(Get(options, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
            var traceRate = Number("trace_rate") ?? 0;
            var rows = relics.Values.Select(r => RelicRow.Compute(r, tier, snap.Prices, snap.RelicPrices[r.RelicName], snap.Ducats, traceRate, scope))
                .Where(r => r.Passes(scope, Get(options, "vault", "Any"), Number("min_roi"), Number("max_cost"), Number("min_reward"), bool.Parse(Get(options, "guaranteed_only", "false"))));
            var ranked = RelicRow.Rank(rows, Get(options, "rank_by", "Best Overall"), scope);
            var page = int.Parse(Get(options, "page", "1"), CultureInfo.InvariantCulture);
            return string.Join('\n', ranked.Skip((page - 1) * 8).Take(8).Select(r => $"{ApplicationEmojis.Relic(r.Relic.RelicName, tier)} {r.Relic.RelicName} · {r.OverallCategory} · online {P(r.Online.Cost)}p · EV {P(r.Online.Profit.ExpectedValue)}p · risk {r.BestRisk}/100").DefaultIfEmpty("No results on this page with those filters.")) + ageNote;
        }
        var info = snap.RelicPrices[relic!.RelicName];
        var commandTraceRate = double.TryParse(Get(options, "trace_rate", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTraceRate) ? parsedTraceRate : 0;
        if (name == "compare")
        {
            var comparison = relic.Compare(snap.Prices, info.Online ?? info.OfflineIncluded, commandTraceRate);
            var objective = Get(options, "objective", "expected_profit");
            double Score(RefinementComparison row) => objective switch
            {
                "expected_roi_pct" => row.ExpectedRoiPct ?? double.NegativeInfinity,
                "worst_case_profit" => row.WorstCaseProfit,
                "chance_of_profit_pct" => row.ChanceOfProfitPct,
                _ => row.ExpectedProfit
            };
            var best = comparison.OrderByDescending(Score).First();
            return string.Join('\n', comparison.Select(r => $"{ApplicationEmojis.Relic(relic.RelicName, r.Tier)} **{r.Tier}** ({r.TraceCost} traces): EV {P(r.ExpectedValue)}p · profit {P(r.ExpectedProfit)}p ({P(r.ExpectedRoiPct)}%) · worst {P(r.WorstCaseProfit)}p · P(profit) {r.ChanceOfProfitPct:0.#}% · rare {r.RareSlotChancePct:0.#}% · marginal {P(r.IncrementalEvPerTrace)}p/trace")) +
                $"\n\nRecommended for {objective.Replace('_', ' ')}: **{best.Tier}**" + ageNote;
        }
        if (name == "buyn")
        {
            var entries = market.Match(relic.RelicName, tier.ToString(), true, true);
            var purchase = OrderMath.Cheapest(entries.Entries, n);
            if (!purchase.FullyFilled) return $"Only {purchase.UnitsFilled}/{n} units available in known online listings; no full-batch profit calculated.";
            BatchAnalysis analysis;
            try { analysis = relic.BuyN(tier, snap.Prices, info.Online, commandTraceRate, n, purchase.TotalCost, exactThreshold: bool.Parse(Get(options, "approximate", "false")) ? 0 : 120, ct: ct); }
            catch (InvalidOperationException) { return "The exact distribution exceeded its memory budget. Retry /rf-relics buyn with approximate: True for a labelled Monte Carlo estimate."; }
            return $"{ApplicationEmojis.Relic(relic.RelicName, tier)} {n} × {relic.RelicName}: total {P(purchase.TotalCost)}p, expected profit {P(analysis.ExpectedProfit)}p, loss chance {analysis.ProbLossPct:0.##}% ({(analysis.Approx ? "Monte Carlo estimate" : "exact distribution")}).\n" +
                $"Filled by {purchase.OrdersUsed.Count} listing(s) from {purchase.OrdersUsed.Select(order => order.Seller).Where(seller => seller.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count()} seller(s). Refinement match: {entries.SubtypeMatched}." + ageNote;
        }
        var row = RelicRow.Compute(relic, tier, snap.Prices, info, snap.Ducats, commandTraceRate);
        return $"{ApplicationEmojis.Relic(relic.RelicName, tier)} {relic.RelicName} — {tier}\nOnline: {P(row.Online.Cost)}p; all sellers: {P(row.Offline.Cost)}p\n" +
            $"Expected return: {P(row.Online.Profit.ExpectedValue)}p; online expected profit: {(row.Online.Profit.PriceKnown ? P(row.Online.Profit.ExpectedProfit) : "unknown")}p\n" +
            $"Online refinement match: {row.Online.Matched}; outlier: {row.Online.Outlier}\n" +
            string.Join('\n', relic.TopRewards(tier, snap.Prices, 6).Select(r => $"{r.Name}: {P(r.Price)}p / {r.ChancePct:0.##}%")) + ageNote;
    }
}
