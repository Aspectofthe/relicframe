using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed record RivenPanelState
{
    public ulong CategoryId { get; set; }
    public ulong ChannelId { get; set; }
    public ulong MessageId { get; set; }
    public ulong EndoCategoryId { get; set; }
    public ulong EndoChannelId { get; set; }
    public ulong EndoMessageId { get; set; }
    public ulong DesiredChannelId { get; set; }
    public ulong DesiredMessageId { get; set; }
    public int DesiredPage { get; set; } = 1;
    public string DesiredRenderHash { get; set; } = "";
}

internal sealed class RivenAppraisalPanel
{
    internal const string CategoryName = "RIVEN APPRAISAL";
    internal const string ChannelName = "riven-appraisal";
    internal const string DesiredChannelName = "riven-desired-rolls";
    private sealed class Session
    {
        public required ulong UserId { get; init; }
        public required RivenAppraisalForm Form { get; init; }
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
        public bool Rerolled { get; init; } = true;
        public string[] Positives { get; set; } = [];
        public string? Negative { get; set; }
    }
    private readonly DiscordSocketClient bot;
    private readonly RivenMarket market;
    private readonly RivenTradeChat chat;
    private readonly RivenEvidenceControls evidence;
    private readonly ulong targetGuild;
    private readonly string path;
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim desiredUpdate = new(1, 1);
    private Task? desiredWorker;
    private RivenPanelState state;
    public ulong AppraisalChannelId => state.ChannelId;

    public RivenAppraisalPanel(DiscordSocketClient bot, RivenMarket market, RivenTradeChat chat, RivenEvidenceControls evidence, ulong targetGuild, string runtime)
    {
        this.bot = bot; this.market = market; this.chat = chat; this.evidence = evidence; this.targetGuild = targetGuild;
        path = Path.Combine(Path.GetFullPath(runtime), "riven_appraisal_panel.json");
        try { state = File.Exists(path) ? Json.Read<RivenPanelState>(path) : new(); } catch { state = new(); }
        bot.ButtonExecuted += DispatchButtonAsync; bot.SelectMenuExecuted += DispatchSelectAsync; bot.ModalSubmitted += DispatchModalAsync;
    }

    private Task DispatchModalAsync(SocketModal interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("riven-panel:", StringComparison.Ordinal)) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { await ModalAsync(interaction); }
            catch (Exception error)
            {
                var errorId = DateTimeOffset.UtcNow.ToString("HHmmss", CultureInfo.InvariantCulture);
                Console.WriteLine($"[riven-modal:{errorId}] {error}");
                try
                {
                    const string message = "The appraisal interaction failed unexpectedly. Start a fresh appraisal; the bot remains online.";
                    if (interaction.HasResponded) await interaction.FollowupAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
                    else await interaction.RespondAsync(message, ephemeral: true, allowedMentions: AllowedMentions.None);
                }
                catch (Exception responseError) { Console.WriteLine($"[riven-modal:{errorId}] Could not send failure response: {responseError}"); }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task<string> SetupAsync(SocketGuild guild, CancellationToken ct)
    {
        if (guild.Id != targetGuild) return "Riven appraisal setup is restricted to its configured server.";
        var current = bot.CurrentUser is null ? null : guild.GetUser(bot.CurrentUser.Id);
        if (current is null) return "Discord is still loading the bot member.";
        if (!current.GuildPermissions.ManageChannels) return "Riven appraisal channel not configured: I need Manage Channels.";
        ICategoryChannel? category = guild.GetCategoryChannel(state.CategoryId) ?? guild.CategoryChannels.FirstOrDefault(c => c.Name == CategoryName);
        category ??= await guild.CreateCategoryChannelAsync(CategoryName); state.CategoryId = category.Id;
        ITextChannel? channel = guild.GetTextChannel(state.ChannelId) ?? guild.TextChannels.FirstOrDefault(c => c.CategoryId == category.Id && c.Name == ChannelName);
        channel ??= await guild.CreateTextChannelAsync(ChannelName, p => { p.CategoryId = category.Id; p.Topic = "Guided weapon-aware Riven appraisals, sale timing, and evidence recording"; });
        if (channel.CategoryId != category.Id || channel.Topic != "Guided weapon-aware Riven appraisals, sale timing, and evidence recording")
            await channel.ModifyAsync(p => { p.CategoryId = category.Id; p.Topic = "Guided weapon-aware Riven appraisals, sale timing, and evidence recording"; });
        state.ChannelId = channel.Id;
        var embed = new EmbedBuilder().WithTitle("Riven Appraisal Desk").WithColor(new Color(0x8E44AD))
            .WithDescription("Press **Appraise a Riven**, enter its weapon, then choose **2–3 positive stats** and **zero or one different negative**. The menus hide stats that cannot roll on that weapon type and prevent selecting one attribute on both sides.")
            .AddField("Screenshot OCR", "Drop one Riven screenshot directly into this channel, use `/rf-riven appraise image:`, or press **Appraise screenshot** for a Discord image link. Local YOLO first isolates stat rows, PaddleOCR reads those rows, and up to eleven Tesseract color/threshold/card passes independently read the whole card and footer. Generated affixes cross-check positives; shared scaling tests every possible fourth attribute; footer consensus protects MR/rerolls from stray digits. Mod rank stays editable because the card shows pips, not reliable rank text. Implausible values are rejected. Faction multipliers above x1 become positive bonuses; values below x1 become negatives (for example x0.6 = −40%). Always review the editable draft. No image or recognized text is saved, and no cloud or generative-AI service is used.")
            .AddField("Weapon-aware filters", "Guns receive their compatible firearm stats. Melee/Zaw receives Initial Combo, Heavy Attack Efficiency, Combo Duration, Range, Additional Combo Count Chance and other legal melee rolls.")
            .AddField("Grades and displayed values", "Enter the numbers printed on a rank-8 Riven to receive an individual F–S grade. Positive grades reward larger values; negative grades reverse the scale because a smaller penalty is better. Grade measures roll variance, not whether the chosen stat is useful.")
            .AddField("Disposition and build checks", "Critical Chance is strongly disposition-sensitive and is down-weighted at low disposition. Shotgun Riven Critical Chance is treated conservatively. Critical Damage assumes the actual weapon build already uses a Critical Damage mod. −Finisher is never assumed harmless for every melee.")
            .AddField("Pricing evidence", "Results prioritize current exact/near asks, listing age/closures and confirmed sales. Isolated extreme asks are filtered and counted. Five comparable asks are required before stale age can reduce their anchor by up to 10%; three timed confirmed sales are required before sale speed can move their anchor by +5% to −8%. Pasted WTS/WTB also needs three priced observations before its small capped influence. Every adjustment is printed. DE's completed-family archive is dated 13 May 2024 and receives only low prior weight.")
            .AddField("After appraisal", "Use **Record sold**, **Still listed**, or **Withdrawn** so future appraisals learn from real outcomes. Closed marketplace listings remain unconfirmed unless a user records the sale.")
            .WithFooter("Some weapons break general rules. Grades and listings inform an estimate; neither proves value or a completed sale.").Build();
        var components = new ComponentBuilder()
            .WithButton("Appraise a Riven", "riven-panel:start", ButtonStyle.Primary, new Emoji("🔮"))
            .WithButton("Appraise screenshot", "riven-panel:image", ButtonStyle.Success, new Emoji("📷")).Build();
        var old = state.MessageId == 0 ? null : await channel.GetMessageAsync(state.MessageId) as IUserMessage;
        if (old is null) { var sent = await channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None); state.MessageId = sent.Id; }
        else await old.ModifyAsync(p => { p.Embed = embed; p.Components = components; p.AllowedMentions = AllowedMentions.None; });
        Json.WriteAtomic(path, state); await DiscordCleanup.BotMessagesAsync(channel, current.Id, state.MessageId);

        ITextChannel? desiredChannel = guild.GetTextChannel(state.DesiredChannelId) ?? guild.TextChannels.FirstOrDefault(c => c.CategoryId == category.Id && c.Name == DesiredChannelName);
        desiredChannel ??= await guild.CreateTextChannelAsync(DesiredChannelName, p => { p.CategoryId = category.Id; p.Topic = "Desired Riven rolls ranked by weapon popularity and observed WTB demand"; });
        if (desiredChannel.CategoryId != category.Id || desiredChannel.Topic != "Desired Riven rolls ranked by weapon popularity and observed WTB demand")
            await desiredChannel.ModifyAsync(p => { p.CategoryId = category.Id; p.Topic = "Desired Riven rolls ranked by weapon popularity and observed WTB demand"; });
        state.DesiredChannelId = desiredChannel.Id;
        await UpdateDesiredBoardAsync(guild, current.Id, force: true, ct);
        StartDesiredWorker(ct);

        ICategoryChannel? endoCategory = guild.GetCategoryChannel(state.EndoCategoryId) ?? guild.CategoryChannels.FirstOrDefault(c => c.Name == "RIVEN ENDO");
        endoCategory ??= await guild.CreateCategoryChannelAsync("RIVEN ENDO"); state.EndoCategoryId = endoCategory.Id;
        ITextChannel? endoChannel = guild.GetTextChannel(state.EndoChannelId) ?? guild.TextChannels.FirstOrDefault(c => c.CategoryId == endoCategory.Id && c.Name == "riven-endo-buying");
        endoChannel ??= await guild.CreateTextChannelAsync("riven-endo-buying", p => { p.CategoryId = endoCategory.Id; p.Topic = "Find cheap Rivens by dissolve Endo per platinum"; });
        if (endoChannel.CategoryId != endoCategory.Id || endoChannel.Topic != "Find cheap Rivens by dissolve Endo per platinum")
            await endoChannel.ModifyAsync(p => { p.CategoryId = endoCategory.Id; p.Topic = "Find cheap Rivens by dissolve Endo per platinum"; });
        state.EndoChannelId = endoChannel.Id;
        var endoEmbed = new EmbedBuilder().WithTitle("Riven Endo Buying").WithColor(new Color(0xF1C40F))
            .WithDescription("This board is separate from roll appraisal. It finds cheap Rivens to dissolve and ranks them by **platinum per 1,000 Endo**.")
            .AddField("Dissolve formula", "`(100 × (MR − 8)) + floor(22.5 × 2^mod rank) + (200 × rerolls) − 7`")
            .AddField("Filters", "Choose your maximum total purchase, maximum platinum per 1,000 Endo, and whether sellers must be online. Roll desirability is intentionally ignored.")
            .WithFooter("Compare the rate with sculpture prices. Listings can disappear; RelicFrame never contacts sellers.").Build();
        var endoComponents = new ComponentBuilder().WithButton("Find Endo Rivens", "riven-panel:endo", ButtonStyle.Success, new Emoji("🟡")).Build();
        var oldEndo = state.EndoMessageId == 0 ? null : await endoChannel.GetMessageAsync(state.EndoMessageId) as IUserMessage;
        if (oldEndo is null) { var sent = await endoChannel.SendMessageAsync(embed: endoEmbed, components: endoComponents, allowedMentions: AllowedMentions.None); state.EndoMessageId = sent.Id; }
        else await oldEndo.ModifyAsync(p => { p.Embed = endoEmbed; p.Components = endoComponents; p.AllowedMentions = AllowedMentions.None; });
        Json.WriteAtomic(path, state); await DiscordCleanup.BotMessagesAsync(endoChannel, current.Id, state.EndoMessageId);
        return $"Configured **#{ChannelName}** and **#{DesiredChannelName}** in **{CategoryName}**, plus **#riven-endo-buying** in **RIVEN ENDO**.";
    }

    private void StartDesiredWorker(CancellationToken lifetime)
    {
        if (desiredWorker is { IsCompleted: false }) return;
        desiredWorker = Task.Run(async () =>
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), lifetime);
                    try
                    {
                        var guild = bot.GetGuild(targetGuild); var current = guild is null || bot.CurrentUser is null ? null : guild.GetUser(bot.CurrentUser.Id);
                        if (guild is not null && current is not null) await UpdateDesiredBoardAsync(guild, current.Id, force: false, lifetime);
                    }
                    catch (Exception error) when (!lifetime.IsCancellationRequested && error is not OutOfMemoryException)
                    { Console.WriteLine($"[riven-desired-board] {error}"); }
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }, CancellationToken.None);
    }

    private (Embed Embed, MessageComponent Components, string Hash, int TotalPages) RenderDesiredBoard()
    {
        var index = market.Index; var all = index.DesiredRolls ?? [];
        var wants = chat.Recent(days: 30).Where(offer => offer.Action == "WTB")
            .GroupBy(offer => RivenPricing.Key(offer.Weapon)).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var ranked = all.Select(row => new
        {
            Row = row,
            Wants = wants.GetValueOrDefault(RivenPricing.Key(row.WeaponName)),
            Popularity = Math.Max(row.Unrolled?.Popularity ?? 0, row.Rolled?.Popularity ?? 0)
        }).OrderByDescending(row => row.Wants > 0).ThenByDescending(row => row.Wants)
          .ThenByDescending(row => row.Popularity).ThenByDescending(row => row.Row.DesiredAsks.Online)
          .ThenBy(row => row.Row.WeaponName, StringComparer.OrdinalIgnoreCase).ToArray();
        const int pageSize = 8; var totalPages = Math.Max(1, (int)Math.Ceiling(ranked.Length / (double)pageSize));
        state.DesiredPage = Math.Clamp(state.DesiredPage, 1, totalPages);
        static string Price(int? value) => value.HasValue ? $"{value.Value:N0}p" : "n/a";
        static string Short(string value, int maximum) => value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
        var lines = ranked.Skip((state.DesiredPage - 1) * pageSize).Take(pageSize).Select((item, offset) =>
        {
            var row = item.Row; var ask = row.DesiredAsks;
            var band = ask.Total == 0 ? "no qualifying asks" : $"{Price(ask.Low)} / **{Price(ask.Median)}** / {Price(ask.High)} ({ask.Total}; {ask.Online} online)";
            var negative = row.HarmlessNegatives.Length == 0 ? "none confirmed" : string.Join(", ", row.HarmlessNegatives.Take(4).Select(RivenPricing.DisplayName));
            return $"**{(state.DesiredPage - 1) * pageSize + offset + 1}. {row.WeaponName}** · WTB {item.Wants} · DE popularity {item.Popularity:0.###}\n`{Short(row.PositiveExpression, 130)}` · −{Short(negative, 90)}\nDesired asks low/median/high: {band}";
        });
        var description = ranked.Length == 0
            ? $"The desired-roll index is not ready yet. **{market.Status}**\nThis message will replace itself after the automatic scan completes."
            : "Ranks locally observed **WTB wants from the last 30 days first**, then DE completed-trade popularity, qualifying online supply, and weapon name. WTB is local Trade Chat evidence; DE popularity is a stale 13 May 2024 family-level baseline.\n\n" + string.Join("\n\n", lines);
        var embed = new EmbedBuilder().WithTitle("Most Wanted Riven Rolls")
            .WithDescription(description).WithColor(new Color(0xA855F7))
            .WithFooter(ranked.Length == 0 ? "Waiting for the all-family Riven scan." : $"Page {state.DesiredPage}/{totalPages} · {ranked.Length} weapons · asks are listings, not confirmed sales · index {index.CompletedAt:u}").Build();
        var components = new ComponentBuilder()
            .WithButton("Previous", "riven-panel:desired-prev", ButtonStyle.Secondary, disabled: state.DesiredPage <= 1 || ranked.Length == 0)
            .WithButton("Next", "riven-panel:desired-next", ButtonStyle.Secondary, disabled: state.DesiredPage >= totalPages || ranked.Length == 0).Build();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(embed.Description + "\0" + embed.Footer?.Text + "\0" + state.DesiredPage)));
        return (embed, components, hash, totalPages);
    }

    private async Task UpdateDesiredBoardAsync(SocketGuild guild, ulong botUserId, bool force, CancellationToken ct)
    {
        await desiredUpdate.WaitAsync(ct);
        try
        {
            var channel = guild.GetTextChannel(state.DesiredChannelId) ?? throw new InvalidOperationException($"#{DesiredChannelName} is missing; restart setup.");
            var rendered = RenderDesiredBoard();
            if (!force && rendered.Hash == state.DesiredRenderHash) return;
            var old = state.DesiredMessageId == 0 ? null : await channel.GetMessageAsync(state.DesiredMessageId) as IUserMessage;
            if (old is null)
            {
                var sent = await channel.SendMessageAsync(embed: rendered.Embed, components: rendered.Components, allowedMentions: AllowedMentions.None);
                state.DesiredMessageId = sent.Id;
            }
            else await old.ModifyAsync(properties => { properties.Embed = rendered.Embed; properties.Components = rendered.Components; properties.AllowedMentions = AllowedMentions.None; });
            state.DesiredRenderHash = rendered.Hash; Json.WriteAtomic(path, state);
            if (force) await DiscordCleanup.BotMessagesAsync(channel, botUserId, state.DesiredMessageId);
        }
        finally { desiredUpdate.Release(); }
    }

    private Task DispatchButtonAsync(SocketMessageComponent interaction) => DispatchComponentAsync(interaction, ButtonAsync);
    private Task DispatchSelectAsync(SocketMessageComponent interaction) => DispatchComponentAsync(interaction, SelectAsync);
    private Task DispatchComponentAsync(SocketMessageComponent interaction, Func<SocketMessageComponent, Task> handler)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("riven-panel:", StringComparison.Ordinal)) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { await handler(interaction); }
            catch (Exception error)
            {
                Console.WriteLine($"[riven-panel-interaction] {error.GetType().Name}: {error.Message}");
                try
                {
                    const string message = "That Riven panel action could not finish. Try it again; the bot remains online.";
                    if (interaction.HasResponded) await interaction.FollowupAsync(message, ephemeral: true);
                    else await interaction.RespondAsync(message, ephemeral: true);
                }
                catch (Exception responseError) { Console.WriteLine($"[riven-panel-interaction] Response failed: {responseError.GetType().Name}"); }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("riven-panel:", StringComparison.Ordinal)) return;
        var parts = interaction.Data.CustomId.Split(':'); var action = parts.ElementAtOrDefault(1) ?? "";
        if (action is "desired-prev" or "desired-next")
        {
            await interaction.DeferAsync();
            await desiredUpdate.WaitAsync();
            try
            {
                state.DesiredPage += action == "desired-next" ? 1 : -1;
                var rendered = RenderDesiredBoard(); state.DesiredRenderHash = rendered.Hash; Json.WriteAtomic(path, state);
                await interaction.ModifyOriginalResponseAsync(properties => { properties.Embed = rendered.Embed; properties.Components = rendered.Components; });
            }
            finally { desiredUpdate.Release(); }
            return;
        }
        if (action == "start")
        {
            var modal = new ModalBuilder().WithTitle("Start a Riven appraisal").WithCustomId("riven-panel:weapon")
                .AddTextInput("Weapon", "weapon", TextInputStyle.Short, "Example: Torid, Glaive Prime, Kuva Brakk", required: true, maxLength: 80)
                .AddTextInput("Rerolled?", "rerolled", TextInputStyle.Short, "yes (default) or no", required: false, maxLength: 5)
                .AddTextInput("Optional copied WTS/WTB messages", "trade_chat", TextInputStyle.Paragraph, "Trade-chat evidence is an offer, not a completed sale", required: false, maxLength: 1000).Build();
            await interaction.RespondWithModalAsync(modal); return;
        }
        if (action == "image")
        {
            var modal = new ModalBuilder().WithTitle("Riven screenshot OCR").WithCustomId("riven-ocr:url")
                .AddTextInput("Discord image link", "url", TextInputStyle.Short, "Or use /rf-riven appraise image: to upload directly", required: true, maxLength: 500).Build();
            await interaction.RespondWithModalAsync(modal); return;
        }
        if (action == "endo")
        {
            var modal = new ModalBuilder().WithTitle("Find Rivens for Endo").WithCustomId("riven-panel:endo-search")
                .AddTextInput("Maximum total buy", "maximum_buy", TextInputStyle.Short, "Example: 20; blank means any", required: false, maxLength: 8)
                .AddTextInput("Maximum platinum per 1,000 Endo", "maximum_rate", TextInputStyle.Short, "Default 6.67", required: false, maxLength: 8)
                .AddTextInput("Online sellers only?", "online_only", TextInputStyle.Short, "yes or no (default)", required: false, maxLength: 5).Build();
            await interaction.RespondWithModalAsync(modal); return;
        }
        if (action == "values")
        {
            if (parts.Length != 3 || !sessions.TryGetValue(parts[2], out var session))
            { await interaction.RespondAsync("This appraisal session expired. Press **Appraise a Riven** to start again.", ephemeral: true); return; }
            if (interaction.User.Id != session.UserId) { await interaction.RespondAsync("Start your own appraisal with the main button.", ephemeral: true); return; }
            if (session.Positives.Length is < 2 or > 3) { await interaction.RespondAsync("Select two or three positive stats first.", ephemeral: true); return; }
            var modal = new ModalBuilder().WithTitle($"{session.Form.WeaponName} roll values").WithCustomId($"riven-panel:values-modal:{parts[2]}");
            for (var i = 0; i < session.Positives.Length; i++) modal.AddTextInput(RivenPricing.DisplayName(session.Positives[i]), $"p{i}", TextInputStyle.Short, "Optional displayed number", required: false, maxLength: 12);
            if (session.Negative is not null) modal.AddTextInput(RivenPricing.DisplayName(session.Negative), "negative", TextInputStyle.Short, "Optional; either sign is accepted", required: false, maxLength: 12);
            await interaction.RespondWithModalAsync(modal.Build());
            return;
        }
        await interaction.RespondAsync("That Riven panel control is no longer active. Use the current panel buttons.", ephemeral: true);
    }

    private async Task SelectAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("riven-panel:", StringComparison.Ordinal)) return;
        var parts = interaction.Data.CustomId.Split(':');
        if (parts.Length != 3 || !sessions.TryGetValue(parts[2], out var session))
        { await interaction.RespondAsync("This appraisal session expired. Press **Appraise a Riven** to start again.", ephemeral: true); return; }
        if (interaction.User.Id != session.UserId) { await interaction.RespondAsync("Start your own appraisal with the main button.", ephemeral: true); return; }
        if (parts[1] == "positive")
        {
            session.Positives = interaction.Data.Values.Take(3).ToArray();
            if (session.Negative is not null && session.Positives.Contains(session.Negative, StringComparer.Ordinal)) session.Negative = null;
        }
        if (parts[1] == "negative") session.Negative = interaction.Data.Values.FirstOrDefault() is { } value && value != "__none__" ? value : null;
        await interaction.UpdateAsync(p => { p.Content = SelectionText(session); p.Components = BuildSelectors(parts[2], session); });
    }

    private async Task ModalAsync(SocketModal interaction)
    {
        if (interaction.GuildId != targetGuild || !interaction.Data.CustomId.StartsWith("riven-panel:", StringComparison.Ordinal)) return;
        string Get(string id) => interaction.Data.Components.FirstOrDefault(c => c.CustomId == id)?.Value?.Trim() ?? "";
        if (interaction.Data.CustomId == "riven-panel:endo-search")
        {
            var index = market.Index;
            if (index.CompletedAt == default) { await interaction.RespondAsync($"The automatic Riven scan has not completed yet. {market.Status}", ephemeral: true); return; }
            double Number(string id, double fallback)
            {
                var text = Get(id); if (text.Length == 0) return fallback;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
                    throw new ArgumentException($"{id.Replace('_', ' ')} must be a number greater than zero.");
                return value;
            }
            try
            {
                var budget = Number("maximum_buy", double.MaxValue); var maxRate = Number("maximum_rate", 6.67);
                var onlineOnly = Get("online_only").Equals("yes", StringComparison.OrdinalIgnoreCase) || Get("online_only").Equals("true", StringComparison.OrdinalIgnoreCase);
                var rows = (index.EndoDeals ?? []).Where(d => d.Price <= budget && d.PlatPerThousandEndo <= maxRate && (!onlineOnly || d.SellerStatus.ToLowerInvariant() is "online" or "ingame")).Take(8).ToArray();
                var lines = rows.Select(d => $"**{d.WeaponName}** · {d.Price}p → {d.Endo:N0} Endo · **{d.PlatPerThousandEndo:0.##}p/1k** · {d.SellerStatus}\n[Listing]({d.Url}) · `{d.IngameWhisper.Replace('`', '\'')}`");
                await interaction.RespondAsync($"**Riven Endo purchases** · cap {maxRate:0.##}p/1k\n" + string.Join("\n\n", lines.DefaultIfEmpty("No indexed listings match those filters.")), ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            catch (ArgumentException error) { await interaction.RespondAsync(error.Message, ephemeral: true); }
            return;
        }
        if (interaction.Data.CustomId == "riven-panel:weapon")
        {
            await interaction.DeferAsync(ephemeral: true);
            try
            {
                foreach (var stale in sessions.Where(pair => DateTimeOffset.UtcNow - pair.Value.CreatedAt > TimeSpan.FromMinutes(30)).ToArray()) sessions.TryRemove(stale.Key, out _);
                if (sessions.Count >= 256) foreach (var old in sessions.OrderBy(pair => pair.Value.CreatedAt).Take(sessions.Count - 255).ToArray()) sessions.TryRemove(old.Key, out _);
                var form = await market.GetAppraisalFormAsync(Get("weapon")); var token = Guid.NewGuid().ToString("N")[..16];
                var rerolled = !Get("rerolled").Equals("no", StringComparison.OrdinalIgnoreCase) && Get("rerolled") != "false";
                var pasted = Get("trade_chat"); if (pasted.Length > 0) chat.Import(pasted, source: "appraisal-panel");
                var session = new Session { UserId = interaction.User.Id, Form = form, Rerolled = rerolled }; sessions[token] = session;
                await interaction.FollowupAsync(SelectionText(session), components: BuildSelectors(token, session), ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or HttpRequestException)
            { await interaction.FollowupAsync($"Could not start the appraisal: {error.Message}", ephemeral: true); }
            return;
        }
        var parts = interaction.Data.CustomId.Split(':');
        if (parts.Length != 3 || parts[1] != "values-modal")
        { await interaction.RespondAsync("That Riven form is no longer active. Start a fresh appraisal.", ephemeral: true); return; }
        if (!sessions.TryGetValue(parts[2], out var selected))
        { await interaction.RespondAsync("This appraisal session expired. Press **Appraise a Riven** to start again.", ephemeral: true); return; }
        if (interaction.User.Id != selected.UserId) { await interaction.RespondAsync("Start your own appraisal with the main button.", ephemeral: true); return; }
        await interaction.DeferAsync(ephemeral: true);
        try
        {
            double? Number(string id)
            {
                var text = Get(id); if (text.Length == 0) return null;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
                    throw new ArgumentException($"{id} must be a valid number.");
                return value;
            }
            var values = Enumerable.Range(0, selected.Positives.Length).Select(i => Number($"p{i}")).ToArray();
            var appraisal = await market.AppraiseAsync(selected.Form.WeaponName, selected.Positives, selected.Negative, selected.Rerolled,
                values, Number("negative"));
            var trade = RivenTradeChat.Summary(chat.Recent(appraisal.WeaponName, 30)); appraisal = RivenCommands.ApplyTradeChat(appraisal, trade);
            await interaction.FollowupAsync(RivenCommands.FormatAppraisal(appraisal, trade), components: evidence.Create(interaction.User.Id, appraisal), ephemeral: true, allowedMentions: AllowedMentions.None);
            sessions.TryRemove(parts[2], out _);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or HttpRequestException)
        { await interaction.FollowupAsync($"Could not appraise this roll: {error.Message}", ephemeral: true); }
    }

    private static string SelectionText(Session session) => $"**{session.Form.WeaponName}** · {session.Form.StatClass} · disposition {session.Form.Disposition:0.##} · {(session.Rerolled ? "rerolled" : "unrolled")}\n" +
        $"Desirability: **{session.Form.ProfileSource}** · `{session.Form.PositiveExpression}`\n" +
        $"Supported harmless negatives: {(session.Form.HarmlessNegatives.Length == 0 ? "none confirmed" : string.Join(", ", session.Form.HarmlessNegatives.Select(RivenPricing.DisplayName)))}\n" +
        (session.Form.ProfileNotes.Length == 0 ? "" : $"Condition: {session.Form.ProfileNotes}\n") +
        $"Positives: {(session.Positives.Length == 0 ? "select 2–3 below" : string.Join(", ", session.Positives.Select(RivenPricing.DisplayName)))}\n" +
        $"Negative: {(session.Negative is null ? "none" : RivenPricing.DisplayName(session.Negative))}\nChoose the roll, then press **Enter values & appraise**.";
    private static MessageComponent BuildSelectors(string token, Session session)
    {
        var positives = session.Form.PositiveStats.Select(stat => new SelectMenuOptionBuilder(RivenPricing.DisplayName(stat), stat, isDefault: session.Positives.Contains(stat))).ToList();
        var negatives = new List<SelectMenuOptionBuilder> { new("No negative", "__none__", isDefault: session.Negative is null) };
        negatives.AddRange(session.Form.NegativeStats.Where(stat => !session.Positives.Contains(stat, StringComparer.Ordinal))
            .Select(stat => new SelectMenuOptionBuilder(RivenPricing.DisplayName(stat), stat, isDefault: session.Negative == stat)));
        return new ComponentBuilder().WithSelectMenu($"riven-panel:positive:{token}", positives, "Choose 2–3 positive stats", 2, 3, row: 0)
            .WithSelectMenu($"riven-panel:negative:{token}", negatives, "Choose zero or one negative", 1, 1, row: 1)
            .WithButton("Enter values & appraise", $"riven-panel:values:{token}", ButtonStyle.Success, new Emoji("📊"), disabled: session.Positives.Length < 2, row: 2).Build();
    }
}
