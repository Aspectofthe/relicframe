using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record PrimeSetCompletionState
{
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public int Page { get; set; } = 1;
    public string RenderHash { get; set; } = "";
}

internal sealed class PrimeSetCompletionManager : IAsyncDisposable
{
    private const int PageSize = 10;
    private readonly DiscordSocketClient bot;
    private readonly LiveMarket market;
    private readonly PersonalMarketManager personalMarket;
    private readonly PrimeSetCompletion completion;
    private readonly ulong targetGuild;
    private readonly ulong configuredOwner;
    private readonly string statePath;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim renderGate = new(1, 1);
    private PrimeSetCompletionState state;
    private CancellationTokenSource? cancellation;
    private Task? worker;
    private string status = "stopped";
    private Dictionary<string, PrimeCompletionRelic?> completionRelics = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PrimeSetOpportunity>? cachedRows;
    private IReadOnlyList<PrimeSetSaleComparison>? cachedComparisons;
    public string Status => Volatile.Read(ref status);

    public PrimeSetCompletionManager(DiscordSocketClient bot, LiveMarket market, MarketHttp http,
        PersonalMarketManager personalMarket, ulong targetGuild, string runtime)
    {
        this.bot = bot; this.market = market; this.personalMarket = personalMarket; this.targetGuild = targetGuild;
        configuredOwner = personalMarket.OwnerId;
        statePath = Path.Combine(runtime, "prime_set_completion_board.json");
        completion = new(market, http, Path.Combine(runtime, "prime_set_components.json"));
        try { state = File.Exists(statePath) ? Json.Read<PrimeSetCompletionState>(statePath) : new(); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { state = new(); }
        bot.ButtonExecuted += DispatchButtonAsync;
    }

    public async Task<string> SetupAsync(SocketGuild guild, CancellationToken ct, CancellationToken lifetime)
    {
        if (guild.Id != targetGuild) return "Prime-set completion is restricted to its configured server.";
        var ownerId = configuredOwner == 0 ? guild.OwnerId : configuredOwner;
        var owner = (IGuildUser?)guild.GetUser(ownerId) ?? await bot.Rest.GetGuildUserAsync(guild.Id, ownerId);
        var current = bot.CurrentUser is null ? null : guild.GetUser(bot.CurrentUser.Id);
        if (owner is null) return $"Discord could not find prime-set completion owner `{ownerId}` in this server.";
        if (current is null || !current.GuildPermissions.ManageChannels) return "I need Manage Channels to create the private Prime Set Completion board.";
        ICategoryChannel? category = guild.CategoryChannels.FirstOrDefault(channel => channel.Name == "PERSONAL MARKET");
        category ??= await guild.CreateCategoryChannelAsync("PERSONAL MARKET");
        await category.AddPermissionOverwriteAsync(guild.EveryoneRole, new OverwritePermissions(viewChannel: PermValue.Deny));
        await category.AddPermissionOverwriteAsync(owner, new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow));
        await category.AddPermissionOverwriteAsync(current, new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow, manageMessages: PermValue.Allow));
        ITextChannel? channel = guild.GetTextChannel(state.ChannelId) ?? guild.TextChannels.FirstOrDefault(existing => existing.CategoryId == category.Id && existing.Name == "prime-set-completion");
        channel ??= await guild.CreateTextChannelAsync("prime-set-completion", properties =>
        {
            properties.CategoryId = category.Id;
            properties.Topic = "Private inventory-aware Prime sets that are one missing component away from completion";
        });
        await channel.AddPermissionOverwriteAsync(guild.EveryoneRole, new OverwritePermissions(viewChannel: PermValue.Deny));
        await channel.AddPermissionOverwriteAsync(owner, new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow));
        await channel.AddPermissionOverwriteAsync(current, new OverwritePermissions(viewChannel: PermValue.Allow, readMessageHistory: PermValue.Allow, sendMessages: PermValue.Allow, manageMessages: PermValue.Allow));
        state.ChannelId = channel.Id; Save();
        await RenderAsync(channel, [], "Waiting for the shared market catalog. Start `/rf-relics refresh` if it is stopped.", true);
        Start(lifetime);
        return "Configured owner-only **PERSONAL MARKET / #prime-set-completion**; it ranks sets that are one component type away.";
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
                { status = $"refresh failed ({error.GetType().Name}); previous board retained"; Console.WriteLine($"[prime-set-completion] {error}"); }
                await Task.Delay(market.PrimeSetNames.Count == 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(15), ct);
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
            var channel = guild.GetTextChannel(state.ChannelId) ?? throw new InvalidOperationException("Prime Set Completion channel is missing; restart the bot to repair it.");
            if (market.PrimeSetNames.Count == 0)
            {
                await RenderAsync(channel, [], "Waiting for the shared market catalog. Start `/rf-relics refresh` if it is stopped.", force);
                status = "waiting for market catalog"; return;
            }
            var inventory = await personalMarket.InventoryAsync(ct);
            var progress = new Progress<(int Current, int Total)>(value =>
                Volatile.Write(ref status, value.Total == 0 ? "checking inventory" : $"reading set metadata {value.Current}/{value.Total}"));
            var rows = await completion.AnalyzeAsync(inventory, personalMarket.ExcludedSellerSlug, progress, ct);
            var top = rows.Take(10).ToArray();
            var sources = top.SelectMany(row => market.SourceRelics(row.MissingItem)).DistinctBy(relic => relic.RelicName).ToArray();
            await market.RefreshBooksAsync(sources.Select(relic => relic.RelicName + " Relic"),
                force ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5), ct);
            var relics = new Dictionary<string, PrimeCompletionRelic?>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in top)
                relics[row.SetName] = PrimeSetCompletion.CheapestSourceRelic(market.SourceRelics(row.MissingItem), row.MissingItem,
                    (name, tier) => market.IsBookFresh(name + " Relic", TimeSpan.FromMinutes(10))
                        ? market.Match(name, tier.ToString(), true, relic: true, excludedSellerSlug: personalMarket.ExcludedSellerSlug)
                        : new OrderMatch([], false));
            var comparisons = await completion.ComparePartsAndSetsAsync(inventory, personalMarket.ExcludedSellerSlug, ct);
            completionRelics = relics;
            cachedRows = rows;
            cachedComparisons = comparisons;
            await RenderAsync(channel, rows, null, force, comparisons);
            status = $"ready; {rows.Count} one-component completions; {comparisons.Count} part/set comparisons";
        }
        finally { gate.Release(); }
    }

    private async Task RenderAsync(ITextChannel channel, IReadOnlyList<PrimeSetOpportunity> opportunities, string? notice, bool force,
        IReadOnlyList<PrimeSetSaleComparison>? comparisons = null)
    {
        await renderGate.WaitAsync();
        try
        {
        var pages = Math.Max(1, (opportunities.Count + PageSize - 1) / PageSize); state.Page = Math.Clamp(state.Page, 1, pages);
        var rows = opportunities.Skip((state.Page - 1) * PageSize).Take(PageSize).Select((row, index) =>
            $"**{(state.Page - 1) * PageSize + index + 1}. {row.SetName}** · profit **{Signed(row.CompletionProfit)}p**\n" +
            $"Missing {row.MissingItem}{(row.MissingQuantity > 1 ? $" ×{row.MissingQuantity}" : "")} · buy ~{row.MissingCost}p · set ~{row.SetValue}p · owned {row.OwnedComponentUnits}/{row.RequiredComponentUnits}" +
            ((state.Page - 1) * PageSize + index < 10 ? RelicLine(row.SetName) : ""));
        var description = notice ?? (opportunities.Count == 0
            ? "No mapped Prime set in the current AlecaFrame inventory is exactly one component type away with usable in-game buy supply and a set price."
            : string.Join('\n', rows));
        description += "\n\nCompletion profit = estimated full-set sell value minus the current cost of the missing quantity. This board does not auto-buy, auto-craft or list completed sets.";
        if (opportunities.Count > 0 && state.Page == 1) description += "\nCheapest relic = one online-seller relic, any refinement. Drops are not guaranteed; opening/traces are not included in completion profit.";
        if (state.Page == 1 && comparisons is { Count: > 0 })
        {
            var comparisonLines = comparisons.Take(5).Select(row =>
                $"**{row.SetName}** · {(row.Advantage > 0 ? "set" : row.Advantage < 0 ? "parts" : "tie")} by **{Math.Abs(row.Advantage)}p** " +
                $"(set {row.SetValue}p{(row.Complete ? "" : $" − missing {row.MissingCost}p")} vs owned parts {row.OwnedPartsValue}p)");
            description += "\n\n**Part versus set · biggest differences**\n" + string.Join('\n', comparisonLines)
                + "\nThese are asking-price estimates, not confirmed sales. Unpriced or stale components are omitted.";
        }
        var embed = new EmbedBuilder().WithTitle("Prime-set completion profit").WithDescription(description.Length <= 4096 ? description : description[..4093] + "…")
            .WithColor(new Color(0x9B59B6u)).WithFooter($"Page {state.Page}/{pages} · {opportunities.Count} one-component opportunities · refreshes every 15 minutes").Build();
        var components = new ComponentBuilder().WithButton("Previous", "prime-set-completion:prev", ButtonStyle.Secondary, disabled: state.Page <= 1)
            .WithButton("Refresh inventory", "prime-set-completion:refresh", ButtonStyle.Primary)
            .WithButton("Next", "prime-set-completion:next", ButtonStyle.Secondary, disabled: state.Page >= pages).Build();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(embed.Description + "\0" + embed.Footer?.Text + "\0" + state.Page)));
        if (!force && state.MessageId != 0 && state.RenderHash == hash) { Save(); return; }
        var old = state.MessageId == 0 ? null : await channel.GetMessageAsync(state.MessageId) as IUserMessage;
        if (old is null)
        {
            var sent = await channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None);
            state.MessageId = sent.Id;
        }
        else await old.ModifyAsync(properties => { properties.Embed = embed; properties.Components = components; properties.AllowedMentions = AllowedMentions.None; });
        state.RenderHash = hash; Save();
        }
        finally { renderGate.Release(); }
    }
    private string RelicLine(string setName)
        => completionRelics.GetValueOrDefault(setName) is { } relic
            ? $"\nCheapest relic: **{relic.Name} · {relic.Refinement} · {relic.Price:0.#}p** · drop {relic.DropChance:0.##}%"
            : "\nCheapest relic: no fresh online source listing.";

    private async Task DispatchButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("prime-set-completion:", StringComparison.Ordinal)) return;
        var ownerId = configuredOwner == 0 ? interaction.GuildId is { } id ? bot.GetGuild(id)?.OwnerId ?? 0 : 0 : configuredOwner;
        if (interaction.User.Id != ownerId) { await interaction.RespondAsync("This private board belongs to its configured owner.", ephemeral: true); return; }
        await interaction.DeferAsync(ephemeral: true);
        _ = Task.Run(async () =>
        {
            try
            {
                var refresh = interaction.Data.CustomId.EndsWith(":refresh", StringComparison.Ordinal);
                await interaction.ModifyOriginalResponseAsync(response => response.Content = refresh ? "Refreshing Prime-set inventory and prices…" : "Updating Prime-set page…");
                if (interaction.Data.CustomId.EndsWith(":prev", StringComparison.Ordinal)) state.Page--;
                if (interaction.Data.CustomId.EndsWith(":next", StringComparison.Ordinal)) state.Page++;
                if (refresh) await UpdateAsync(true, CancellationToken.None);
                else if (cachedRows is { } rows)
                {
                    var channel = bot.GetGuild(targetGuild)?.GetTextChannel(state.ChannelId)
                        ?? throw new InvalidOperationException("Prime Set Completion channel unavailable.");
                    await RenderAsync(channel, rows, null, false, cachedComparisons);
                }
                else throw new InvalidOperationException("Prime-set completion is still loading. Try again when the board is ready.");
                await interaction.ModifyOriginalResponseAsync(response => response.Content = refresh ? "Prime-set completion inventory and prices refreshed." : "Prime-set page updated.");
            }
            catch (Exception error)
            {
                Console.WriteLine($"[prime-set-completion-button] {error.GetType().Name}: {error.Message}");
                try { await interaction.ModifyOriginalResponseAsync(response => response.Content = $"Prime-set completion update failed: {error.Message}"); } catch { }
            }
        }, CancellationToken.None);
    }

    private static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString();
    private void Save() => Json.WriteAtomic(statePath, state);
    public async Task StopAsync() { if (cancellation is not null) await cancellation.CancelAsync(); if (worker is not null) await worker; status = "stopped"; }
    public async ValueTask DisposeAsync() { bot.ButtonExecuted -= DispatchButtonAsync; await StopAsync(); cancellation?.Dispose(); gate.Dispose(); renderGate.Dispose(); }
}
