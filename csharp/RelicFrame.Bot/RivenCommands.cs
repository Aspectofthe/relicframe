using System.Globalization;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal static class RivenCommands
{
    public static ApplicationCommandProperties Build()
    {
        var group = new SlashCommandBuilder().WithName("rf-riven").WithDescription("C# preview: Riven resale candidates, not guaranteed profit");
        foreach (var name in new[] { "refresh", "stop", "flips", "price", "top", "guide" })
        {
            var sub = new SlashCommandOptionBuilder().WithName(name).WithDescription($"Riven {name}").WithType(ApplicationCommandOptionType.SubCommand);
            if (name == "price") sub.AddOption("weapon", ApplicationCommandOptionType.String, "Weapon family", true);
            if (name == "flips")
            {
                sub.AddOption("page", ApplicationCommandOptionType.Integer, "Two full rolls per page", minValue: 1);
                sub.AddOption("weapon", ApplicationCommandOptionType.String, "Optional weapon filter");
                sub.AddOption("budget", ApplicationCommandOptionType.Integer, "Maximum asking price", minValue: 1);
                sub.AddOption("roi", ApplicationCommandOptionType.Number, "Minimum estimated ROI percentage", minValue: 0);
                sub.AddOption("online", ApplicationCommandOptionType.Boolean, "Only online or in-game sellers (default true)");
            }
            group.AddOption(sub);
        }
        return group.Build();
    }
    public static async Task<string> ExecuteAsync(SocketSlashCommand command, RivenMarket market, CancellationToken ct)
    {
        var sub = command.Data.Options.Single();
        string Get(string name, string fallback = "") => sub.Options.FirstOrDefault(o => o.Name == name)?.Value?.ToString() ?? fallback;
        if (sub.Name is "refresh" or "stop")
        {
            if (command.User is not SocketGuildUser user || !user.GuildPermissions.ManageGuild) return "Manage Server permission is required.";
            if (sub.Name == "stop") { await market.StopAsync(); return "C# Riven scanning stopped; completed results retained."; }
            await market.StartAsync(ct); return "C# Riven scan started in the background. It evaluates every catalog family against the sampled auction pool. /rf-status shows progress; /rf-riven stop cancels.";
        }
        if (sub.Name == "guide") return "Start /rf-riven refresh (Manage Server), then use /rf-riven flips with page, weapon, budget and ROI filters.\n" +
            "Candidates require curated desired stats, a harmless negative and at least three comparable asks. Projected resale is 90% of the weighted comparable median, capped by official weekly trade evidence when available.\n" +
            "Every catalog family is evaluated, but Warframe.market caps searches: this is not every listing or a guaranteed sale. The weekly feed is completed-trade data without exact rolls; live listings are asking prices.\n" +
            "Click the listing/seller link or copy the /w text into Warframe yourself. The bot never buys or messages sellers. Stop scanning with /rf-riven stop.";
        if (sub.Name is "price" or "top")
        {
            var rows = market.Weekly;
            if (rows.Count == 0) return "No C# weekly data loaded yet. Start /rf-riven refresh.";
            if (sub.Name == "price")
            {
                var weapon = Get("weapon"); var prices = new[] { false, true }.Select(r => RivenPricing.ParseWeekly(rows, weapon, r)).OfType<WeeklyPrice>().ToArray();
                return prices.Length == 0 ? "Weapon family not found in DE's weekly feed." :
                    string.Join('\n', prices.Select(p => $"{p.Weapon} ({(p.Rerolled ? "rerolled" : "unrolled")}): median {p.Median:0.##}p, range {p.Minimum:0.##}–{p.Maximum:0.##}p; popularity {p.Popularity:0.##}.")) +
                    "\nCompleted-trade aggregates—not a valuation of an individual roll.";
            }
            return string.Join('\n', rows.Select(r => RivenPricing.ParseWeekly([r], r.Get("compatibility").Text(), r.Get("rerolled").Bool())).OfType<WeeklyPrice>()
                .DistinctBy(p => (RivenPricing.Key(p.Weapon), p.Rerolled)).OrderByDescending(p => p.Popularity).Take(10)
                .Select(p => $"{p.Weapon} ({(p.Rerolled ? "rerolled" : "unrolled")}): median {p.Median:0.##}p; popularity {p.Popularity:0.##}"));
        }
        var index = market.Index;
        if (index.CompletedAt == default) return $"No completed C# Riven scan yet. {market.Status}";
        var online = bool.Parse(Get("online", "true")); var weaponFilter = Get("weapon");
        var budget = double.Parse(Get("budget", double.MaxValue.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        var roi = double.Parse(Get("roi", "0"), CultureInfo.InvariantCulture);
        var results = index.Deals.Where(d => d.Price <= budget && d.RoiPct >= roi && (!online || d.SellerStatus.ToLowerInvariant() is "online" or "ingame")
            && (weaponFilter.Length == 0 || d.WeaponName.Contains(weaponFilter, StringComparison.OrdinalIgnoreCase))).ToArray();
        var page = int.Parse(Get("page", "1"), CultureInfo.InvariantCulture); var pages = Math.Max(1, (results.Length + 1) / 2);
        if (page > pages) return $"Page out of range; there are {pages} pages.";
        string Roll(object[] r, bool positive)
        {
            var slug = r[0] is JsonElement s ? s.Text() : r[0].ToString() ?? "";
            double? value = r[1] is JsonElement j ? j.Number() : r[1] is double d ? d : null;
            var suffix = RivenPricing.Unit(slug) switch { "meters" => "m", "seconds" => "s", "flat" => "", _ => "%" };
            return $"{(value.HasValue ? value.Value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + suffix : positive ? "+" : "−")} {slug.Replace('_', ' ')}";
        }
        var lines = results.Skip((page - 1) * 2).Take(2).Select(d =>
            $"{d.WeaponName}: buy {d.Price}p → estimated resale {d.ProjectedResale}p; margin {d.PotentialMargin}p ({d.RoiPct:0.#}% ROI)\n" +
            string.Join(" | ", d.PositiveRolls.Select(r => Roll(r, true)).Concat(d.NegativeRolls.Select(r => Roll(r, false)))) +
            $"\n{d.ComparableCount} comparable asks • {d.SellerStatus}\n[Listing]({d.Url}) • [Seller]({d.SellerProfileUrl})\n`{d.IngameWhisper.Replace('`', '\'')}`");
        return $"{results.Length} candidates • page {page}/{pages} • scanned <t:{index.CompletedAt.ToUnixTimeSeconds()}:R> • {index.FailedSearches} failed searches\n" +
            string.Join("\n\n", lines.DefaultIfEmpty("No candidates match those filters.")) + "\nAsks and availability can change. Resale is not guaranteed.";
    }
}
