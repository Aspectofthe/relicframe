using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal sealed class RivenEvidenceControls
{
    private sealed record Session(ulong UserId, RivenAppraisal Appraisal, DateTimeOffset CreatedAt);
    private readonly RivenMarket market;
    private readonly ulong guildId;
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ulong, MessageComponent> pending = new();

    public RivenEvidenceControls(DiscordSocketClient socket, RivenMarket market, ulong guildId)
    {
        this.market = market; this.guildId = guildId;
        socket.ButtonExecuted += DispatchButtonAsync;
        socket.ModalSubmitted += DispatchModalAsync;
    }

    public void Prepare(ulong commandId, ulong userId, RivenAppraisal appraisal)
    {
        pending[commandId] = Create(userId, appraisal);
    }
    public void PrepareComponents(ulong commandId, MessageComponent components) => pending[commandId] = components;
    public MessageComponent Create(ulong userId, RivenAppraisal appraisal)
    {
        foreach (var stale in sessions.Where(p => DateTimeOffset.UtcNow - p.Value.CreatedAt > TimeSpan.FromDays(7)).ToArray()) sessions.TryRemove(stale.Key, out _);
        if (sessions.Count >= 2048) foreach (var old in sessions.OrderBy(pair => pair.Value.CreatedAt).Take(sessions.Count - 2047).ToArray()) sessions.TryRemove(old.Key, out _);
        var token = Guid.NewGuid().ToString("N")[..16]; sessions[token] = new(userId, appraisal, DateTimeOffset.UtcNow);
        return new ComponentBuilder()
            .WithButton("Record sold", $"riven:sold:{token}", ButtonStyle.Success)
            .WithButton("Still listed", $"riven:still:{token}", ButtonStyle.Primary)
            .WithButton("Withdrawn", $"riven:withdrawn:{token}", ButtonStyle.Secondary).Build();
    }
    public MessageComponent? Take(ulong commandId) => pending.TryRemove(commandId, out var component) ? component : null;

    private bool TrySession(string customId, out string action, out string token, out Session session)
    {
        action = token = ""; session = null!;
        if (!customId.StartsWith("riven:", StringComparison.Ordinal)) return false;
        var parts = customId.Split(':', 3); if (parts.Length != 3 || !sessions.TryGetValue(parts[2], out session!)) return false;
        action = parts[1]; token = parts[2]; return action is "sold" or "still" or "withdrawn";
    }
    private Task DispatchButtonAsync(SocketMessageComponent interaction) =>
        interaction.GuildId == guildId && interaction.Data.CustomId.StartsWith("riven:", StringComparison.Ordinal)
            ? DispatchAsync(interaction, ButtonAsync) : Task.CompletedTask;
    private Task DispatchModalAsync(SocketModal interaction) =>
        interaction.GuildId == guildId && interaction.Data.CustomId.StartsWith("riven:", StringComparison.Ordinal)
            ? DispatchAsync(interaction, ModalAsync) : Task.CompletedTask;
    private Task DispatchAsync<T>(T interaction, Func<T, Task> handler) where T : SocketInteraction
    {
        _ = Task.Run(async () =>
        {
            try { await handler(interaction); }
            catch (Exception error)
            {
                Console.WriteLine($"[riven-evidence] {error.GetType().Name}: {error.Message}");
                try
                {
                    const string message = "The evidence action could not finish. Your appraisal remains available; try again.";
                    if (interaction.HasResponded) await interaction.FollowupAsync(message, ephemeral: true);
                    else await interaction.RespondAsync(message, ephemeral: true);
                }
                catch (Exception responseError) { Console.WriteLine($"[riven-evidence] Response failed: {responseError.GetType().Name}"); }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ButtonAsync(SocketMessageComponent interaction)
    {
        if (interaction.GuildId != guildId || !interaction.Data.CustomId.StartsWith("riven:", StringComparison.Ordinal)) return;
        if (!TrySession(interaction.Data.CustomId, out var action, out var token, out var session))
        { await interaction.RespondAsync("This appraisal control expired. Run a fresh appraisal to record new evidence.", ephemeral: true); return; }
        if (interaction.User.Id != session.UserId) { await interaction.RespondAsync("Only the person who ran this appraisal can record its outcome.", ephemeral: true); return; }
        if (action == "sold")
        {
            var modal = new ModalBuilder().WithTitle($"Record {session.Appraisal.WeaponName} sale").WithCustomId($"riven:sold-modal:{token}")
                .AddTextInput("Sold for (platinum)", "price", TextInputStyle.Short, required: true, minLength: 1, maxLength: 6)
                .AddTextInput("Originally listed at (optional)", "listed_at", TextInputStyle.Short, "Example: 2026-09-04 8:30 PM", required: false, maxLength: 40)
                .AddTextInput("Sold at (optional; defaults now)", "sold_at", TextInputStyle.Short, "Example: 2026-09-05 1:15 PM", required: false, maxLength: 40).Build();
            await interaction.RespondWithModalAsync(modal); return;
        }
        await interaction.DeferAsync(ephemeral: true);
        try
        {
            var outcome = action == "still" ? "still_listed" : "withdrawn";
            var added = await market.RecordOutcomeAsync(session.Appraisal, outcome, null, null, null, Reporter(interaction.User.Id));
            await interaction.FollowupAsync(!added ? "That same outcome was already recorded recently; no duplicate was added." :
                outcome == "still_listed" ? "Recorded as still listed. This is active-listing evidence, not a sale." : "Recorded as withdrawn. It will not be counted as a sale.", ephemeral: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { await interaction.FollowupAsync($"Could not save that evidence: {error.Message}", ephemeral: true); }
    }
    private async Task ModalAsync(SocketModal interaction)
    {
        if (interaction.GuildId != guildId || !interaction.Data.CustomId.StartsWith("riven:sold-modal:", StringComparison.Ordinal)) return;
        if (!TrySession(interaction.Data.CustomId.Replace("sold-modal", "sold", StringComparison.Ordinal), out _, out _, out var session))
        { await interaction.RespondAsync("This appraisal control expired. Run a fresh appraisal to record the sale.", ephemeral: true); return; }
        if (interaction.User.Id != session.UserId) { await interaction.RespondAsync("Only the person who ran this appraisal can record its outcome.", ephemeral: true); return; }
        string Get(string id) => interaction.Data.Components.FirstOrDefault(c => c.CustomId == id)?.Value?.Trim() ?? "";
        if (!int.TryParse(Get("price"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) || price is < 1 or > 1_000_000)
        { await interaction.RespondAsync("Enter a sale price from 1 to 1,000,000 platinum.", ephemeral: true); return; }
        DateTimeOffset? Parse(string id)
        {
            var value = Get(id); if (value.Length == 0) return null;
            return DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed) ? parsed : throw new ArgumentException($"Could not understand {id.Replace('_', ' ')}. Use a date like 2026-09-04 8:30 PM.");
        }
        try
        {
            var listed = Parse("listed_at"); var sold = Parse("sold_at") ?? DateTimeOffset.Now;
            if (listed.HasValue && sold < listed) throw new ArgumentException("Sold time cannot be before listed time.");
            if (listed > DateTimeOffset.Now.AddMinutes(5) || sold > DateTimeOffset.Now.AddMinutes(5)) throw new ArgumentException("Evidence timestamps cannot be in the future.");
            await interaction.DeferAsync(ephemeral: true);
            var added = await market.RecordOutcomeAsync(session.Appraisal, "sold", price, listed, sold, Reporter(interaction.User.Id));
            if (!added) { await interaction.FollowupAsync("That sale was already recorded recently; no duplicate was added.", ephemeral: true); return; }
            var duration = listed.HasValue ? $" after {(sold - listed.Value).TotalHours:0.#} hours" : "";
            await interaction.FollowupAsync($"Recorded a confirmed user-reported sale of **{session.Appraisal.WeaponName}** for **{price:N0}p**{duration}. It will influence future matching appraisals.", ephemeral: true);
        }
        catch (ArgumentException error)
        {
            if (interaction.HasResponded) await interaction.FollowupAsync(error.Message, ephemeral: true);
            else await interaction.RespondAsync(error.Message, ephemeral: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { await interaction.FollowupAsync($"Could not save that sale evidence: {error.Message}", ephemeral: true); }
    }
    private static string Reporter(ulong id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString(CultureInfo.InvariantCulture))))[..16];
}
