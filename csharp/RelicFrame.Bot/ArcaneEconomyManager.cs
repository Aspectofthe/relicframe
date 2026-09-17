using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record ArcaneEconomyState
{
    public ulong CollectionChannelId { get; set; }
    public ulong CollectionMessageId { get; set; }
    public ulong CalculatorChannelId { get; set; }
    public ulong CalculatorMessageId { get; set; }
    public int CalculatorPage { get; set; } = 1;
    public string CalculatorMode { get; set; } = "dissolve";
    public string CollectionHash { get; set; } = "";
    [JsonPropertyName("collection_message_ids")]
    public Dictionary<string, ulong>? LegacyCollectionMessageIds { get; set; }
    public string CalculatorHash { get; set; } = "";
    public ulong LiquidityChannelId { get; set; }
    public ulong LiquidityMessageId { get; set; }
}

internal sealed class ArcaneEconomyManager : IAsyncDisposable
{
    private const int PageSize = 8;
    private readonly DiscordSocketClient bot;
    private readonly LiveMarket market;
    private readonly ArcaneEconomy calculator;
    private readonly ArcaneLiquidity liquidity;
    private IReadOnlyDictionary<string, ArcaneSalesActivity> salesActivity = new Dictionary<string, ArcaneSalesActivity>();
    private readonly ulong targetGuild;
    private readonly string statePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ArcaneEconomyState state;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private ArcaneEconomyResult? lastResult;
    private string status = "stopped";
    private int progressCurrent;
    private int progressTotal;
    private DateTimeOffset progressStartedAt;
    private DateTimeOffset progressDeadline;
    public string Status => Volatile.Read(ref status);
    public TimeSpan? EstimatedRemaining
    {
        get
        {
            var current = Volatile.Read(ref progressCurrent); var total = Volatile.Read(ref progressTotal);
            if (current <= 0 || total <= current || progressStartedAt == default) return null;
            var remaining = progressDeadline - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
        }
    }

    public ArcaneEconomyManager(DiscordSocketClient bot, LiveMarket market, MarketHttp http, ulong targetGuild, string runtime)
    {
        this.bot = bot; this.market = market; this.targetGuild = targetGuild; calculator = new(market);
        liquidity = new(http, Path.Combine(runtime, "arcane_sales_activity.json"));
        statePath = Path.Combine(runtime, "arcane_economy_board.json");
        try { state = File.Exists(statePath) ? Json.Read<ArcaneEconomyState>(statePath) : new(); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { state = new(); }
        bot.ButtonExecuted += DispatchButtonAsync; bot.SelectMenuExecuted += DispatchSelectAsync;
    }

    public async Task<string> SetupAsync(SocketGuild guild, CancellationToken ct, CancellationToken lifetime)
    {
        if (guild.Id != targetGuild) return "Arcane Economy is restricted to its configured server.";
        var current = bot.CurrentUser is null ? null : guild.GetUser(bot.CurrentUser.Id);
        if (current is null || !current.GuildPermissions.ManageChannels) return "I need Manage Channels to create the Arcane Economy boards.";
        ICategoryChannel? category = guild.CategoryChannels.FirstOrDefault(channel => channel.Name == "ARCANE ECONOMY");
        category ??= await guild.CreateCategoryChannelAsync("ARCANE ECONOMY");
        var collections = await EnsureChannelAsync(guild, category, state.CollectionChannelId, "arcane-collections",
            "Arcane Dissolution collection packs ranked by conservative live expected platinum");
        var vosfor = await EnsureChannelAsync(guild, category, state.CalculatorChannelId, "vosfor-calculator",
            "Rank-aware Arcane sell versus dissolve decisions using shared market prices");
        state.CollectionChannelId = collections.Id; state.CalculatorChannelId = vosfor.Id; Save();
        var liquid = await EnsureChannelAsync(guild, category, state.LiquidityChannelId, "arcane-quick-sales",
            "Arcane collections ranked by reported R0 sales activity, then expected platinum value");
        state.LiquidityChannelId = liquid.Id; Save();
        if (state.LegacyCollectionMessageIds is { Count: > 0 })
        {
            foreach (var messageId in state.LegacyCollectionMessageIds.Values.Distinct())
                if (messageId != state.CollectionMessageId && await collections.GetMessageAsync(messageId) is IUserMessage message
                    && message.Author.Id == current.Id) await message.DeleteAsync();
            state.LegacyCollectionMessageIds = null; Save();
        }
        await RenderWaitingAsync(collections, true); await RenderWaitingAsync(vosfor, false);
        Start(lifetime);
        return "Configured **ARCANE ECONOMY / #arcane-collections**, **#vosfor-calculator** and **#arcane-quick-sales**.";
    }

    private static async Task<ITextChannel> EnsureChannelAsync(SocketGuild guild, ICategoryChannel category, ulong id, string name, string topic)
    {
        ITextChannel? channel = guild.GetTextChannel(id) ?? guild.TextChannels.FirstOrDefault(existing => existing.CategoryId == category.Id && existing.Name == name);
        channel ??= await guild.CreateTextChannelAsync(name, properties => { properties.CategoryId = category.Id; properties.Topic = topic; });
        return channel;
    }

    private void Start(CancellationToken lifetime)
    {
        if (worker is { IsCompleted: false }) return;
        cancellation?.Dispose(); cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        worker = Task.Run(() => LoopAsync(cancellation.Token), CancellationToken.None);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await UpdateAsync(false, ct); }
                catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException)
                { status = $"refresh failed ({error.GetType().Name}); previous boards retained"; Console.WriteLine($"[arcane-economy] {error}"); }
                await Task.Delay(market.ArcaneItems.Count == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(15), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task UpdateAsync(bool force, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var guild = bot.GetGuild(targetGuild) ?? throw new InvalidOperationException("Configured server unavailable.");
            var collections = guild.GetTextChannel(state.CollectionChannelId) ?? throw new InvalidOperationException("Arcane Collections channel is missing; restart the bot to repair it.");
            var vosfor = guild.GetTextChannel(state.CalculatorChannelId) ?? throw new InvalidOperationException("Vosfor Calculator channel is missing; restart the bot to repair it.");
            if (market.ArcaneItems.Count == 0)
            {
                status = "waiting for shared market catalog";
                await RenderWaitingAsync(collections, true); await RenderWaitingAsync(vosfor, false); return;
            }
            progressStartedAt = DateTimeOffset.UtcNow;
            progressDeadline = progressStartedAt.AddSeconds(Math.Max(45, market.ArcaneItems.Count * 1.5));
            Interlocked.Exchange(ref progressCurrent, 0); Interlocked.Exchange(ref progressTotal, market.ArcaneItems.Count);
            var progress = new Progress<(int Current, int Total)>(value =>
            {
                Interlocked.Exchange(ref progressCurrent, value.Current); Interlocked.Exchange(ref progressTotal, value.Total);
                Volatile.Write(ref status, $"pricing Arcane books {value.Current}/{value.Total}; shared refresh {market.RefreshedBooks}/{market.TotalBooks} ({market.ReadyBooks} available)");
            });
            var result = await calculator.AnalyzeAsync(null, progress, ct);
            Volatile.Write(ref lastResult, result);
            await RenderCollectionsAsync(collections, result, force);
            await RenderCalculatorAsync(vosfor, result, force);
            status = "checking cached Arcane sales activity";
            salesActivity = await liquidity.RefreshAsync(result, ct);
            var liquid = guild.GetTextChannel(state.LiquidityChannelId);
            if (liquid is not null) await RenderLiquidityAsync(liquid, result);
            status = $"ready; {result.ReadyBooks}/{result.TotalBooks} Arcane books priced";
            Interlocked.Exchange(ref progressCurrent, progressTotal);
        }
        finally { gate.Release(); }
    }

    private async Task RenderCollectionsAsync(ITextChannel channel, ArcaneEconomyResult result, bool force)
    {
        var rows = result.Packs.Select((pack, index) =>
            $"**{index + 1}. {pack.Name}** · optimal **{pack.OptimalEv:0.#}p/pack** · {pack.PlatPerHundredVosfor:0.#}p/100V\n" +
            $"sell-all {pack.DirectSellEv:0.#}p · recycle {pack.ExpectedRecycledVosfor:0.#}V avg · sell {pack.SellChance:P0} of pulls · priced {pack.PricedArcanes}/{pack.TotalArcanes}");
        var description = string.Join('\n', rows) +
            "\n\nEach 200-Vosfor + 50,000-credit pack contains 3 unranked Arcanes. Optimal EV sells a pull when its liquidity-adjusted ask beats its recycle value, otherwise it dissolves the pull and values the recovered Vosfor through the best pack again.";
        var embed = new EmbedBuilder().WithTitle("Arcane Collection profit calculator").WithDescription(description)
            .WithColor(new Color(0x00B4D8u)).WithFooter($"Live asks · conservative liquidity adjustment · Vosfor shadow value {result.PlatinumPerVosfor:0.###}p each · refreshes every 15 minutes").Build();
        var menu = new SelectMenuBuilder().WithCustomId("arcane-economy:collection").WithPlaceholder("Select a collection to see every Arcane and R0–R5 price")
            .WithMinValues(1).WithMaxValues(1);
        foreach (var name in ArcaneEconomy.CollectionNames) menu.AddOption(name, name, "Drop chances, EV contribution and rank-separated prices");
        var components = new ComponentBuilder().WithButton("Refresh prices", "arcane-economy:refresh", ButtonStyle.Primary).WithSelectMenu(menu).Build();
        await UpsertAsync(channel, true, embed, components, Hash(embed, "collections"), force);
    }

    private async Task RenderLiquidityAsync(ITextChannel channel, ArcaneEconomyResult result)
    {
        var ranked = ArcaneLiquidity.Rank(result, salesActivity);
        var lines = ranked.Select((row, index) => $"**{index + 1}. {row.Name}** · **{row.DirectSellEv:0.#}p EV/pack**\n" +
            $"Drop-weighted R0 activity **{row.SalesPerDay:0.#}/day** · data {row.Known}/{row.Total}" + (row.Known < row.Total ? " · incomplete" : ""));
        var embed = new EmbedBuilder().WithTitle("Arcane collections · Most often sold first")
            .WithDescription(string.Join('\n', lines) + "\n\nSorted by drop-chance-weighted reported R0 sales/day over the last 48h, then sell-all EV. Collections with missing data follow complete ones. EV is sale proceeds per 200 Vosfor pack, not net profit. Reported volume is not a guaranteed sale or time-to-sell.")
            .WithColor(new Color(0x2ECC71u)).WithFooter("R0 sales only · statistics cached 6h · prices share the existing market cache").Build();
        var menu = new SelectMenuBuilder().WithCustomId("arcane-economy:liquid-collection").WithPlaceholder("Select a collection · sales activity and prices");
        foreach (var row in ranked) menu.AddOption(row.Name, row.Name);
        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        var old = state.LiquidityMessageId == 0 ? null : await channel.GetMessageAsync(state.LiquidityMessageId) as IUserMessage;
        if (old is null) state.LiquidityMessageId = (await channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None)).Id;
        else await old.ModifyAsync(properties => { properties.Embed = embed; properties.Components = components; });
        Save();
    }

    private async Task RenderCalculatorAsync(ITextChannel channel, ArcaneEconomyResult result, bool force)
    {
        var dissolve = state.CalculatorMode == "dissolve";
        var sorted = (dissolve
            ? result.Quotes.OrderByDescending(row => Math.Max(row.RankZeroDissolveValue - (row.RankZeroAdjusted ?? 0), row.MaxRankDissolveValue - (row.MaxRankAdjusted ?? 0)))
            : result.Quotes.OrderByDescending(row => Math.Max((row.RankZeroAdjusted ?? 0) - row.RankZeroDissolveValue, (row.MaxRankAdjusted ?? 0) - row.MaxRankDissolveValue))
            ).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var pages = Math.Max(1, (sorted.Length + PageSize - 1) / PageSize); state.CalculatorPage = Math.Clamp(state.CalculatorPage, 1, pages);
        var rows = sorted.Skip((state.CalculatorPage - 1) * PageSize).Take(PageSize).Select((row, index) =>
            $"**{(state.CalculatorPage - 1) * PageSize + index + 1}. {row.Name}**\n" +
            $"R0 **{row.RankZeroRecommendation}** · ask {P(row.RankZeroPrice)} · adjusted {P(row.RankZeroAdjusted)} vs dissolve {row.RankZeroDissolveValue:0.#}p ({row.BaseVosfor}V) · {row.RankZeroAsks} asks\n" +
            $"R{row.MaxRank} **{row.MaxRankRecommendation}** · ask {P(row.MaxRankPrice)} · adjusted {P(row.MaxRankAdjusted)} vs dissolve {row.MaxRankDissolveValue:0.#}p ({row.BaseVosfor * row.CopiesAtMax}V) · {row.MaxRankAsks} asks");
        var description = string.Join('\n', rows) +
            "\n\nAdjusted price discounts thin order books; HOLD means the two choices are within 10%. Listing asks are estimates, not completed-sale guarantees.";
        var embed = new EmbedBuilder().WithTitle($"Vosfor calculator · Best {(dissolve ? "Dissolves" : "Sells")}").WithDescription(description)
            .WithColor(new Color(0x9B59B6u)).WithFooter($"Page {state.CalculatorPage}/{pages} · {sorted.Length} dissolvable Arcanes · exact R0/R-max order filtering").Build();
        var components = new ComponentBuilder()
            .WithButton("Previous", "arcane-economy:prev", ButtonStyle.Secondary, disabled: state.CalculatorPage <= 1)
            .WithButton("Best Dissolves", "arcane-economy:dissolve", dissolve ? ButtonStyle.Success : ButtonStyle.Secondary)
            .WithButton("Best Sells", "arcane-economy:sell", dissolve ? ButtonStyle.Secondary : ButtonStyle.Success)
            .WithButton("Next", "arcane-economy:next", ButtonStyle.Secondary, disabled: state.CalculatorPage >= pages).Build();
        await UpsertAsync(channel, false, embed, components, Hash(embed, state.CalculatorMode + state.CalculatorPage), force);
    }

    private async Task RenderWaitingAsync(ITextChannel channel, bool collections)
    {
        var embed = new EmbedBuilder().WithTitle(collections ? "Arcane Collection profit calculator" : "Vosfor calculator")
            .WithDescription("Waiting for the shared market catalog. Start `/rf-relics refresh` if market loading is stopped.")
            .WithColor(new Color(0x5865F2u)).Build();
        await UpsertAsync(channel, collections, embed, new ComponentBuilder().Build(), Hash(embed, "waiting"), true);
    }

    private async Task UpsertAsync(ITextChannel channel, bool collections, Embed embed, MessageComponent components, string hash, bool force)
    {
        var messageId = collections ? state.CollectionMessageId : state.CalculatorMessageId;
        var priorHash = collections ? state.CollectionHash : state.CalculatorHash;
        if (!force && messageId != 0 && priorHash == hash) return;
        var old = messageId == 0 ? null : await channel.GetMessageAsync(messageId) as IUserMessage;
        if (old is null)
        {
            var sent = await channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None);
            if (collections) state.CollectionMessageId = sent.Id; else state.CalculatorMessageId = sent.Id;
        }
        else await old.ModifyAsync(properties => { properties.Embed = embed; properties.Components = components; properties.AllowedMentions = AllowedMentions.None; });
        if (collections) state.CollectionHash = hash; else state.CalculatorHash = hash;
        Save();
    }

    private async Task DispatchButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("arcane-economy:", StringComparison.Ordinal)) return;
        await interaction.DeferAsync(ephemeral: true);
        _ = Task.Run(async () =>
        {
            try
            {
                var action = interaction.Data.CustomId["arcane-economy:".Length..];
                if (action == "prev") state.CalculatorPage--;
                if (action == "next") state.CalculatorPage++;
                if (action is "sell" or "dissolve") { state.CalculatorMode = action; state.CalculatorPage = 1; }
                await UpdateAsync(true, CancellationToken.None);
                await interaction.FollowupAsync(action == "refresh" ? "Arcane prices refreshed." : "Vosfor view updated.", ephemeral: true);
            }
            catch (Exception error)
            {
                Console.WriteLine($"[arcane-economy-button] {error.GetType().Name}: {error.Message}");
                try { await interaction.FollowupAsync($"Arcane Economy update failed: {error.Message}", ephemeral: true); } catch { }
            }
        }, CancellationToken.None);
    }

    private async Task DispatchSelectAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || interaction.Data.CustomId is not ("arcane-economy:collection" or "arcane-economy:liquid-collection")) return;
        await interaction.DeferAsync(ephemeral: true);
        try
        {
            var collection = interaction.Data.Values.FirstOrDefault(); var result = Volatile.Read(ref lastResult);
            if (string.IsNullOrWhiteSpace(collection) || result is null)
            {
                await interaction.FollowupAsync("Arcane pricing is still loading. Try the collection selector again when the board is ready.", ephemeral: true);
                return;
            }
            var isLiquid = interaction.Data.CustomId == "arcane-economy:liquid-collection";
            var members = ArcaneEconomy.CollectionMembers(collection);
            if (isLiquid) members = members.OrderByDescending(member => salesActivity.GetValueOrDefault(member.Name)?.SalesPerDay ?? -1)
                .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var sections = Math.Max(1, (members.Count + 7) / 8); var embeds = new List<Embed>();
            for (var section = 0; section < sections; section++)
            {
                var lines = members.Skip(section * 8).Take(8).Select(member =>
                {
                    var quote = result.Quotes.FirstOrDefault(row => row.Name.Equals(member.Name, StringComparison.OrdinalIgnoreCase));
                    if (quote is null) return $"**{member.Name}** · {member.Chance:P2} · no market mapping";
                    var prices = $"R0 {P(quote.RankZeroPrice)}" + (quote.MaxRank > 0 ? $" · R{quote.MaxRank} {P(quote.MaxRankPrice)}" : "");
                    var usedR0 = Math.Max(quote.RankZeroAdjusted ?? 0, member.BaseVosfor * result.PlatinumPerVosfor);
                    var contribution = 3 * member.Chance * usedR0;
                    var activity = salesActivity.GetValueOrDefault(member.Name)?.SalesPerDay;
                    var demand = isLiquid ? $" · R0 reported sales/day {(activity.HasValue ? activity.Value.ToString("0.#") : "unknown")}" : "";
                    return $"**{member.Name}** · {member.Chance:P2} · **{contribution:0.##}p EV/pack** · {member.BaseVosfor}V\n{prices}{demand}";
                });
                embeds.Add(new EmbedBuilder().WithTitle($"{collection} collection · Arcane prices{(sections > 1 ? $" · section {section + 1}/{sections}" : "")}")
                    .WithDescription(string.Join('\n', lines)).WithColor(new Color(0x00B4D8u))
                    .WithFooter("R0 drives pack EV because packs drop unranked Arcanes. The second price is that Arcane’s actual maximum rank.").Build());
            }
            await interaction.FollowupAsync(embeds: embeds.ToArray(), ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        catch (Exception error)
        {
            Console.WriteLine($"[arcane-economy-select] {error.GetType().Name}: {error.Message}");
            try { await interaction.FollowupAsync($"Collection breakdown failed: {error.Message}", ephemeral: true); } catch { }
        }
    }

    private static string P(double? value) => value.HasValue ? $"{value:0.#}p" : "no data";
    private static string Hash(Embed embed, string stateKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(embed.Description + "\0" + embed.Footer?.Text + "\0" + stateKey)));
    private void Save() => Json.WriteAtomic(statePath, state);
    public async Task StopAsync() { if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker; status = "stopped"; }
    public async ValueTask DisposeAsync() { bot.ButtonExecuted -= DispatchButtonAsync; bot.SelectMenuExecuted -= DispatchSelectAsync; await StopAsync(); cancellation?.Dispose(); gate.Dispose(); }
}
