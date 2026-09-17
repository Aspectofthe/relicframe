using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed class RivenImageWorkflow : IDisposable
{
    private sealed record Session(ulong UserId, RivenOcrDraft Draft, DateTimeOffset CreatedAt);
    private sealed record Validated(string Weapon, string[] Positives, double?[] Values, string? Negative, double? NegativeValue,
        int MasteryRank, int ModRank, int Rerolls, string[] Problems);
    private readonly RivenMarket market;
    private readonly RivenTradeChat chat;
    private readonly RivenEvidenceControls evidence;
    private readonly ulong guildId;
    private readonly DiscordSocketClient socket;
    private ulong appraisalChannelId;
    private readonly RivenLocalOcr ocr = new();
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private int pendingUploads;

    public RivenImageWorkflow(DiscordSocketClient socket, RivenMarket market, RivenTradeChat chat, RivenEvidenceControls evidence, ulong guildId)
    {
        this.socket = socket; this.market = market; this.chat = chat; this.evidence = evidence; this.guildId = guildId;
        socket.ButtonExecuted += ButtonAsync; socket.ModalSubmitted += DispatchModalAsync; socket.MessageReceived += MessageAsync;
    }

    public void SetAppraisalChannel(ulong channelId) => appraisalChannelId = channelId;

    private Task MessageAsync(SocketMessage message)
    {
        if (appraisalChannelId == 0 || message.Channel.Id != appraisalChannelId || message.Author.IsBot || message.Author.IsWebhook) return Task.CompletedTask;
        var image = message.Attachments.FirstOrDefault(IsSupportedImage);
        if (image is null) return Task.CompletedTask;
        if (Interlocked.Increment(ref pendingUploads) > 4)
        {
            Interlocked.Decrement(ref pendingUploads);
            _ = message.Channel.SendMessageAsync("The screenshot reader already has four images queued. Try again after the current drafts appear.",
                messageReference: new MessageReference(message.Id), allowedMentions: AllowedMentions.None);
            return Task.CompletedTask;
        }
        _ = Task.Run(async () =>
        {
            IUserMessage? progress = null;
            try
            {
                progress = await SendReplyAsync(message, "Reading the Riven screenshot locally…");
                var draft = await ocr.AnalyzeAsync(image.Url, image.Filename, image.Size, market.WeaponNames, default);
                var created = await CreateDraftAsync(message.Author.Id, draft, default);
                await progress.ModifyAsync(update => { update.Content = created.Text; update.Components = created.Components; update.AllowedMentions = AllowedMentions.None; });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                Console.WriteLine($"[riven-ocr-upload] {error.GetType().Name}: {error.Message}");
                try
                {
                    var detail = error is ArgumentException or InvalidOperationException or HttpRequestException
                        ? error.Message : "Local text recognition failed unexpectedly. Try a clear, uncropped PNG or JPG.";
                    var text = $"I could not read that Riven screenshot: {detail}";
                    if (progress is null) await SendReplyAsync(message, text);
                    else await progress.ModifyAsync(update => { update.Content = text; update.Components = new ComponentBuilder().Build(); update.AllowedMentions = AllowedMentions.None; });
                }
                catch (Exception responseError) { Console.WriteLine($"[riven-ocr-upload] Could not send failure response: {responseError.Message}"); }
            }
            finally { Interlocked.Decrement(ref pendingUploads); }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private static async Task<IUserMessage> SendReplyAsync(SocketMessage source, string text)
    {
        try
        {
            return await source.Channel.SendMessageAsync(text, messageReference: new MessageReference(source.Id), allowedMentions: AllowedMentions.None);
        }
        catch (Discord.Net.HttpException)
        {
            // Reply references require Read Message History; a normal channel message does not.
            return await source.Channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None);
        }
    }

    private static bool IsSupportedImage(Attachment attachment)
    {
        if (attachment.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) return true;
        return Path.GetExtension(attachment.Filename).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".tif" or ".tiff";
    }

    public async Task<string> BeginAsync(ulong commandId, ulong userId, Attachment image, CancellationToken ct)
    {
        var draft = await ocr.AnalyzeAsync(image.Url, image.Filename, image.Size, market.WeaponNames, ct);
        var (text, components) = await CreateDraftAsync(userId, draft, ct);
        evidence.PrepareComponents(commandId, components);
        return text;
    }

    private async Task<(string Text, MessageComponent Components)> CreateDraftAsync(ulong userId, RivenOcrDraft draft, CancellationToken ct)
    {
        foreach (var stale in sessions.Where(pair => DateTimeOffset.UtcNow - pair.Value.CreatedAt > TimeSpan.FromMinutes(30)).ToArray()) sessions.TryRemove(stale.Key, out _);
        if (sessions.Count >= 256) foreach (var old in sessions.OrderBy(pair => pair.Value.CreatedAt).Take(sessions.Count - 255).ToArray()) sessions.TryRemove(old.Key, out _);
        var token = Guid.NewGuid().ToString("N")[..16]; sessions[token] = new(userId, draft, DateTimeOffset.UtcNow);
        return (await PreviewAsync(draft, ct), Controls(token));
    }

    private async Task<string> PreviewAsync(RivenOcrDraft draft, CancellationToken ct)
    {
        var valid = await ValidateAsync(draft, ct);
        static string Stat(RivenOcrStat stat, bool positive) => $"{(positive ? "+" : "−")}{(stat.Value.HasValue ? Math.Abs(stat.Value.Value).ToString("0.###", CultureInfo.InvariantCulture) + " " : "")}{RivenPricing.DisplayName(SafeStat(stat.Name))}";
        var fields = draft.Positives.Select(stat => Stat(stat, true)).Concat(draft.Negative is null ? [] : [Stat(draft.Negative, false)]);
        var details = $"rank {(draft.ModRank.HasValue ? draft.ModRank : "?")}/8 · MR {(draft.MasteryRank.HasValue ? draft.MasteryRank : "?")} · rerolls {(draft.Rerolls.HasValue ? draft.Rerolls : "?")}";
        var warnings = valid.Problems.Concat(draft.Notes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return $"**Local OCR draft — confirm before appraisal** · confidence {draft.Confidence:P0}\n" +
            $"Weapon: **{(draft.WeaponName.Length == 0 ? "unreadable" : draft.WeaponName)}**\n" +
            $"Stats: {(fields.Any() ? string.Join(" · ", fields) : "none read")}\n{details}\n" +
            (warnings.Length == 0 ? "All fields passed the weapon-class checks. Still compare them with the screenshot." : "Check: " + string.Join(" ", warnings)) +
            "\n\nPress **Edit OCR** to correct any field, or **Confirm & appraise** only when everything matches the image. The screenshot and OCR text are not saved.";
    }

    private async Task<Validated> ValidateAsync(RivenOcrDraft draft, CancellationToken ct)
    {
        var problems = new List<string>(); RivenAppraisalForm? form = null;
        if (draft.WeaponName.Length == 0) problems.Add("Weapon is missing.");
        else try { form = await market.GetAppraisalFormAsync(draft.WeaponName, ct); } catch (Exception error) when (error is ArgumentException or HttpRequestException or InvalidDataException) { problems.Add(error.Message); }
        var positives = new List<string>(); var values = new List<double?>();
        foreach (var stat in draft.Positives.Take(3))
        {
            try { positives.Add(RivenPricing.NormalizeStat(stat.Name)); values.Add(stat.Value.HasValue ? Math.Abs(stat.Value.Value) : null); }
            catch (ArgumentException) { problems.Add($"Unknown positive stat: {stat.Name}."); }
        }
        string? negative = null;
        if (draft.Negative is not null) try { negative = RivenPricing.NormalizeStat(draft.Negative.Name); } catch (ArgumentException) { problems.Add($"Unknown negative stat: {draft.Negative.Name}."); }
        if (positives.Count is < 2 or > 3) problems.Add("Exactly two or three positives are required.");
        if (positives.Distinct(StringComparer.Ordinal).Count() != positives.Count) problems.Add("Positive stats must be different.");
        if (negative is not null && positives.Contains(negative, StringComparer.Ordinal)) problems.Add("One stat cannot be both positive and negative.");
        if (form is not null)
        {
            foreach (var stat in positives.Where(stat => !form.PositiveStats.Contains(stat, StringComparer.Ordinal))) problems.Add($"{RivenPricing.DisplayName(stat)} cannot be positive on {form.StatClass} Rivens.");
            if (negative is not null && !form.NegativeStats.Contains(negative, StringComparer.Ordinal)) problems.Add($"{RivenPricing.DisplayName(negative)} cannot be negative on {form.StatClass} Rivens.");
        }
        return new(form?.WeaponName ?? draft.WeaponName, positives.ToArray(), values.ToArray(), negative,
            draft.Negative?.Value is { } negativeValue ? -Math.Abs(negativeValue) : null,
            Math.Clamp(draft.MasteryRank ?? 8, 8, 16), Math.Clamp(draft.ModRank ?? 8, 0, 8), Math.Clamp(draft.Rerolls ?? 0, 0, 9999), problems.Distinct().ToArray());
    }

    private Task ButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != guildId || !interaction.Data.CustomId.StartsWith("riven-ocr:", StringComparison.Ordinal)) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { await HandleButtonAsync(interaction); }
            catch (Exception error)
            {
                Console.WriteLine($"[riven-ocr-button] {error.GetType().Name}: {error.Message}");
                try
                {
                    const string message = "The screenshot appraisal failed before it could finish. Your draft is still available; try Confirm again or use Edit OCR.";
                    if (interaction.HasResponded) await interaction.FollowupAsync(message, ephemeral: true);
                    else await interaction.RespondAsync(message, ephemeral: true);
                }
                catch (Exception responseError) { Console.WriteLine($"[riven-ocr-button] Could not send failure response: {responseError.GetType().Name}: {responseError.Message}"); }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task HandleButtonAsync(SocketMessageComponent interaction)
    {
        var parts = interaction.Data.CustomId.Split(':');
        if (parts.Length != 3 || !sessions.TryGetValue(parts[2], out var session)) { await interaction.RespondAsync("This OCR draft expired. Start a fresh screenshot appraisal.", ephemeral: true); return; }
        if (interaction.User.Id != session.UserId) { await interaction.RespondAsync("Only the person who uploaded this screenshot can use its draft.", ephemeral: true); return; }
        if (parts[1] == "cancel") { sessions.TryRemove(parts[2], out _); await interaction.UpdateAsync(message => { message.Content = "Riven screenshot appraisal cancelled."; message.Components = new ComponentBuilder().Build(); }); return; }
        if (parts[1] == "edit")
        {
            var draft = session.Draft;
            var modal = new ModalBuilder().WithTitle("Correct Riven OCR").WithCustomId($"riven-ocr:edit-modal:{parts[2]}")
                .AddTextInput("Weapon", "weapon", TextInputStyle.Short, value: draft.WeaponName, required: true, maxLength: 80)
                .AddTextInput("Positive stats (one per line)", "positives", TextInputStyle.Paragraph, "Example: 189 Critical Chance", value: string.Join('\n', draft.Positives.Select(Editable)), required: true, maxLength: 300)
                .AddTextInput("Negative stat (optional)", "negative", TextInputStyle.Short, "Example: -64.9 Zoom", value: draft.Negative is null ? null : Editable(draft.Negative), required: false, maxLength: 100)
                .AddTextInput("Mod details", "details", TextInputStyle.Short, "rank 8; MR 15; rerolls 8", value: $"rank {draft.ModRank ?? 8}; MR {draft.MasteryRank ?? 8}; rerolls {draft.Rerolls ?? 0}", required: true, maxLength: 80).Build();
            await interaction.RespondWithModalAsync(modal); return;
        }
        await interaction.DeferAsync(ephemeral: true);
        try
        {
            var valid = await ValidateAsync(session.Draft, default);
            if (valid.Problems.Length > 0) { await interaction.FollowupAsync("Correct the OCR draft before appraisal: " + string.Join(" ", valid.Problems), ephemeral: true); return; }
            var appraisal = await market.AppraiseAsync(valid.Weapon, valid.Positives, valid.Negative, valid.Rerolls > 0, valid.Values, valid.NegativeValue,
                valid.MasteryRank, valid.ModRank, valid.Rerolls);
            var trade = RivenTradeChat.Summary(chat.Recent(appraisal.WeaponName, 30)); appraisal = RivenCommands.ApplyTradeChat(appraisal, trade);
            await interaction.FollowupAsync(RivenCommands.FormatAppraisal(appraisal, trade), components: evidence.Create(interaction.User.Id, appraisal), ephemeral: true, allowedMentions: AllowedMentions.None);
            sessions.TryRemove(parts[2], out _);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or HttpRequestException)
        { await interaction.FollowupAsync($"Could not appraise the confirmed OCR draft: {error.Message}", ephemeral: true); }
    }

    private Task DispatchModalAsync(SocketModal interaction)
    {
        if (interaction.GuildId != guildId || !interaction.Data.CustomId.StartsWith("riven-ocr:", StringComparison.Ordinal)) return Task.CompletedTask;
        _ = Task.Run(async () =>
        {
            try { await ModalAsync(interaction); }
            catch (Exception error)
            {
                Console.WriteLine($"[riven-ocr-modal] {error.GetType().Name}: {error.Message}");
                try
                {
                    const string message = "The local OCR interaction failed. Start a fresh screenshot appraisal; the bot remains online.";
                    if (interaction.HasResponded) await interaction.FollowupAsync(message, ephemeral: true);
                    else await interaction.RespondAsync(message, ephemeral: true);
                }
                catch (Exception responseError) { Console.WriteLine($"[riven-ocr-modal] Could not send failure response: {responseError.Message}"); }
            }
        }, CancellationToken.None); return Task.CompletedTask;
    }

    private async Task ModalAsync(SocketModal interaction)
    {
        string Get(string id) => interaction.Data.Components.FirstOrDefault(component => component.CustomId == id)?.Value?.Trim() ?? "";
        if (interaction.Data.CustomId == "riven-ocr:url")
        {
            await interaction.DeferAsync(ephemeral: true);
            try
            {
                var url = Get("url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("cdn.discordapp.com" or "media.discordapp.net"))
                    throw new ArgumentException("Paste an HTTPS Discord CDN image link. For a normal upload, use `/rf-riven appraise image:`.");
                var draft = await ocr.AnalyzeAsync(url, Path.GetFileName(uri.AbsolutePath), 0, market.WeaponNames, default);
                var created = await CreateDraftAsync(interaction.User.Id, draft, default);
                await interaction.FollowupAsync(created.Text, components: created.Components, ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or HttpRequestException)
            { await interaction.FollowupAsync($"Could not read that screenshot: {error.Message}", ephemeral: true); }
            return;
        }
        var parts = interaction.Data.CustomId.Split(':');
        if (parts.Length != 3 || parts[1] != "edit-modal" || !sessions.TryGetValue(parts[2], out var session)) { await interaction.RespondAsync("This OCR draft expired.", ephemeral: true); return; }
        if (interaction.User.Id != session.UserId) { await interaction.RespondAsync("Only the screenshot owner can edit this draft.", ephemeral: true); return; }
        try
        {
            var positives = Get("positives").Split(['\n', ';', '|'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(ParseEditable).ToArray();
            var negativeText = Get("negative"); var negative = negativeText.Length == 0 ? null : ParseEditable(negativeText);
            var details = Get("details");
            int? Number(string label) { var match = Regex.Match(details, $@"\b{label}\s*[:=]?\s*(\d{{1,4}})", RegexOptions.IgnoreCase); return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null; }
            var updated = session.Draft with { WeaponName = Get("weapon"), Positives = positives, Negative = negative, ModRank = Number("rank"), MasteryRank = Number("MR"), Rerolls = Number("rerolls?"), Confidence = 1, Notes = ["Manually reviewed OCR draft."] };
            sessions[parts[2]] = session with { Draft = updated };
            await interaction.RespondAsync(await PreviewAsync(updated, default), components: Controls(parts[2]), ephemeral: true, allowedMentions: AllowedMentions.None);
        }
        catch (ArgumentException error) { await interaction.RespondAsync($"Could not understand the correction: {error.Message}", ephemeral: true); }
    }

    private static RivenOcrStat ParseEditable(string line)
    {
        var match = Regex.Match(line.Trim(), @"^(?<value>[+\-−–—]?\s*\d+(?:[.,]\d+)?)?\s*%?\s*(?<name>[A-Za-z][A-Za-z /&().'-]+)$");
        if (!match.Success) throw new ArgumentException($"Use `number Stat Name`, one per line; unreadable line: {line}");
        double? value = null; var valueText = match.Groups["value"].Value.Replace(" ", "").Replace('−', '-').Replace('–', '-').Replace('—', '-').Replace(',', '.');
        if (valueText.Length > 0)
        {
            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) throw new ArgumentException($"Invalid stat value: {valueText}");
            value = parsed;
        }
        return new(RivenPricing.NormalizeStat(match.Groups["name"].Value.Trim()), value);
    }

    private static string Editable(RivenOcrStat stat) => (stat.Value.HasValue ? stat.Value.Value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture) + " " : "") + RivenPricing.DisplayName(SafeStat(stat.Name));
    private static string SafeStat(string value) { try { return RivenPricing.NormalizeStat(value); } catch (ArgumentException) { return value; } }
    private static MessageComponent Controls(string token) => new ComponentBuilder()
        .WithButton("Confirm & appraise", $"riven-ocr:confirm:{token}", ButtonStyle.Success, new Emoji("✅"))
        .WithButton("Edit OCR", $"riven-ocr:edit:{token}", ButtonStyle.Primary, new Emoji("✏️"))
        .WithButton("Cancel", $"riven-ocr:cancel:{token}", ButtonStyle.Secondary).Build();
    public void Dispose()
    {
        socket.ButtonExecuted -= ButtonAsync; socket.ModalSubmitted -= DispatchModalAsync; socket.MessageReceived -= MessageAsync;
        ocr.Dispose();
    }
}
