using System.Globalization;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using RelicFrame.Core;

internal static class RivenCommands
{
    public static ApplicationCommandProperties Build(string commandName = "rf-riven")
    {
        var group = new SlashCommandBuilder().WithName(commandName).WithDescription("Riven roll appraisals and evidence-backed flips");
        var appraise = new SlashCommandOptionBuilder().WithName("appraise").WithDescription("Estimate a sell price from this roll's market evidence").WithType(ApplicationCommandOptionType.SubCommand);
        appraise.AddOption("image", ApplicationCommandOptionType.Attachment, "Riven screenshot; review the private OCR draft before appraisal");
        appraise.AddOption(new SlashCommandOptionBuilder().WithName("weapon").WithDescription("Weapon family or named variant; optional with image").WithType(ApplicationCommandOptionType.String).WithAutocomplete(true));
        appraise.AddOption(Stat("positive_1", "First positive stat; optional with image", false));
        appraise.AddOption(Stat("positive_2", "Second positive stat; optional with image", false));
        appraise.AddOption(Stat("positive_3", "Optional third positive stat", false));
        appraise.AddOption(Stat("negative", "Optional negative stat", false));
        appraise.AddOption("positive_1_value", ApplicationCommandOptionType.Number, "Displayed value of the first positive");
        appraise.AddOption("positive_2_value", ApplicationCommandOptionType.Number, "Displayed value of the second positive");
        appraise.AddOption("positive_3_value", ApplicationCommandOptionType.Number, "Displayed value of the third positive");
        appraise.AddOption("negative_value", ApplicationCommandOptionType.Number, "Displayed negative value; either sign is accepted");
        appraise.AddOption("rerolled", ApplicationCommandOptionType.Boolean, "Has the Riven been rerolled? Default: true");
        appraise.AddOption("trade_chat", ApplicationCommandOptionType.String, "Optional copied WTS/WTB lines for this appraisal", maxLength: 2000);
        var flips = new SlashCommandOptionBuilder().WithName("flips").WithDescription("Find underpriced rolls with weapon-aware desirable stats").WithType(ApplicationCommandOptionType.SubCommand);
        flips.AddOption(new SlashCommandOptionBuilder().WithName("purpose").WithDescription("Resale roll or Endo conversion").WithType(ApplicationCommandOptionType.String)
            .AddChoice("Resale", "resale").AddChoice("Endo", "endo"));
        flips.AddOption("maximum_buy", ApplicationCommandOptionType.Integer, "Maximum platinum you are willing to spend", minValue: 1, maxValue: 100000);
        flips.AddOption("minimum_discount", ApplicationCommandOptionType.Number, "Minimum discount versus similar rolls", minValue: 10, maxValue: 80);
        flips.AddOption("maximum_endo_rate", ApplicationCommandOptionType.Number, "Maximum platinum per 1,000 Endo", minValue: 0.01, maxValue: 100);
        flips.AddOption("online_only", ApplicationCommandOptionType.Boolean, "Hide listings whose seller is offline");
        var desired = new SlashCommandOptionBuilder().WithName("desired").WithDescription("Browse desired rolls and their current price evidence").WithType(ApplicationCommandOptionType.SubCommand);
        desired.AddOption(new SlashCommandOptionBuilder().WithName("weapon").WithDescription("Optional weapon family or name search").WithType(ApplicationCommandOptionType.String).WithAutocomplete(true));
        desired.AddOption("page", ApplicationCommandOptionType.Integer, "Page of all known weapons", minValue: 1);
        group.AddOption(appraise).AddOption(flips).AddOption(desired);
        return group.Build();
    }
    private static SlashCommandOptionBuilder Stat(string name, string description, bool required) => new SlashCommandOptionBuilder()
        .WithName(name).WithDescription(description).WithType(ApplicationCommandOptionType.String).WithRequired(required).WithAutocomplete(true);

    public static async Task<string> ExecuteAsync(SocketSlashCommand command, RivenMarket market, RivenTradeChat chat, RivenEvidenceControls controls, RivenImageWorkflow images, CancellationToken ct)
    {
        var sub = command.Data.Options.Single();
        string Get(string name, string fallback = "") => sub.Options.FirstOrDefault(o => o.Name == name)?.Value?.ToString() ?? fallback;
        if (sub.Name == "appraise")
        {
            if (sub.Options.FirstOrDefault(option => option.Name == "image")?.Value is Attachment image)
                return await images.BeginAsync(command.Id, command.User.Id, image, ct);
            var pastedChat = Get("trade_chat");
            var imported = pastedChat.Length > 0 ? chat.Import(pastedChat, source: "appraisal-discord") : null;
            var positives = new[] { Get("positive_1"), Get("positive_2"), Get("positive_3") }.Where(s => s.Length > 0).ToArray();
            if (Get("weapon").Length == 0 || positives.Length < 2)
                throw new ArgumentException("Attach a Riven screenshot, or enter a weapon and at least two positive stats.");
            double? Number(string name) => double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
            var values = new[] { Number("positive_1_value"), Number("positive_2_value"), Number("positive_3_value") }.Take(positives.Length).ToArray();
            var weeklyRerolled = bool.TryParse(Get("rerolled", "true"), out var rerolled) && rerolled;
            var appraisal = await market.AppraiseAsync(Get("weapon"), positives, Get("negative"), weeklyRerolled,
                values, Number("negative_value"), ct: ct);
            var trade = RivenTradeChat.Summary(chat.Recent(appraisal.WeaponName, 30)); appraisal = ApplyTradeChat(appraisal, trade);
            controls.Prepare(command.Id, command.User.Id, appraisal);
            return FormatAppraisal(appraisal, trade, imported);
        }
        var index = market.Index;
        if (index.CompletedAt == default) return $"The automatic Riven scan has not completed yet. {market.Status}. Check /rf-status for progress.";
        if (sub.Name == "desired")
        {
            var rows = index.DesiredRolls ?? [];
            var query = Get("weapon").Trim();
            if (query.Length > 0)
            {
                var exact = rows.Where(row => RivenPricing.Key(row.WeaponName) == RivenPricing.Key(query) || RivenPricing.Key(row.WeaponSlug) == RivenPricing.Key(query)).ToArray();
                rows = (exact.Length > 0 ? exact : rows.Where(row => row.WeaponName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
            }
            if (rows.Length == 0) return query.Length == 0 ? "The completed index has no desired-roll summaries yet. Wait for the automatic scan; /rf-status shows its progress." : $"No indexed weapon matched **{query}**.";
            const int pageSize = 4;
            var requestedPage = int.TryParse(Get("page", "1"), out var parsedPage) ? parsedPage : 1;
            var totalPages = Math.Max(1, (int)Math.Ceiling(rows.Length / (double)pageSize)); var page = Math.Clamp(requestedPage, 1, totalPages);
            string Price(int? value) => value.HasValue ? $"{value.Value:N0}p" : "n/a";
            string Weekly(WeeklyPrice? value) => value is null ? "n/a" : $"{value.Median:0.#}p";
            static string Short(string value, int maximum) => value.Length <= maximum ? value : value[..(maximum - 1)] + "…";
            var desiredLines = rows.Skip((page - 1) * pageSize).Take(pageSize).Select(row =>
            {
                var ask = row.DesiredAsks; var current = ask.Total == 0 ? "no qualifying asks indexed" :
                    $"{Price(ask.Low)} / **{Price(ask.Median)}** / {Price(ask.High)} low/median/high ({ask.Total} asks; {ask.Online} online)";
                var negatives = row.HarmlessNegatives.Length == 0 ? "none confirmed" : string.Join(", ", row.HarmlessNegatives.Take(5).Select(Pretty));
                return $"**{row.WeaponName}** · `{Short(row.PositiveExpression, 150)}`\nDesired-roll asks: {current}\nSupported negatives: {Short(negatives, 120)} · old DE family median U {Weekly(row.Unrolled)} / R {Weekly(row.Rolled)} · {row.ProfileSource}";
            });
            return $"**Most desired Riven rolls** · page {page}/{totalPages} · {rows.Length} weapon{(rows.Length == 1 ? "" : "s")} · indexed <t:{index.CompletedAt.ToUnixTimeSeconds()}:R>\n\n" +
                string.Join("\n\n", desiredLines) +
                "\n\nCurrent figures are filtered listing asks for rolls that pass each weapon profile, not confirmed sales. U/R are stale 13 May 2024 DE all-roll family medians and are shown only as a baseline.";
        }
        var online = bool.TryParse(Get("online_only", "false"), out var parsedOnline) && parsedOnline;
        var budget = double.TryParse(Get("maximum_buy"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedBudget) ? parsedBudget : double.MaxValue;
        if (Get("purpose", "resale") == "endo")
        {
            var maxRate = double.TryParse(Get("maximum_endo_rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRate) ? parsedRate : 6.67;
            var endoResults = (index.EndoDeals ?? []).Where(d => d.Price <= budget && d.PlatPerThousandEndo <= maxRate && (!online || d.SellerStatus.ToLowerInvariant() is "online" or "ingame")).Take(6).ToArray();
            var endoLines = endoResults.Select(d => $"**{d.WeaponName}** · buy {d.Price}p · dissolve {d.Endo:N0} Endo · **{d.PlatPerThousandEndo:0.##}p/1k** · {d.SellerStatus}\n[Listing]({d.Url}) · `{d.IngameWhisper.Replace('`', '\'')}`");
            return $"Best indexed Riven-to-Endo purchases · maximum {maxRate:0.##}p per 1,000 Endo\n" + string.Join("\n\n", endoLines.DefaultIfEmpty("No indexed Riven listings match those Endo filters.")) +
                "\nCompare the rate with your local sculpture price. Listing availability can change; the bot never contacts sellers.";
        }
        var minimumDiscount = double.TryParse(Get("minimum_discount", "10"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDiscount) ? parsedDiscount : 10;
        var results = index.Deals.Where(d => d.Price <= budget && d.DiscountPct >= minimumDiscount && (!online || d.SellerStatus.ToLowerInvariant() is "online" or "ingame")).Take(4).ToArray();
        var lines = results.Select(d =>
            $"**{d.WeaponName}: {d.Price}p → target {d.ProjectedResale}p** · +{d.PotentialMargin}p / {d.RoiPct:0.#}% ROI\n" +
            string.Join(" | ", d.PositiveRolls.Select(r => Roll(r, true)).Concat(d.NegativeRolls.Select(r => Roll(r, false)))) +
            $"\nBest-stat model: {string.Join(", ", d.CuratedProfile.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Pretty))}; supported low-impact negative · {d.ComparableCount} similar asks\n" +
            $"[Listing]({d.Url}) · [Seller]({d.SellerProfileUrl}) · `{d.IngameWhisper.Replace('`', '\'')}`");
        return $"{index.Deals.Length} indexed candidates · scanned <t:{index.CompletedAt.ToUnixTimeSeconds()}:R> · {index.FailedSearches} failed searches\n" +
            string.Join("\n\n", lines.DefaultIfEmpty("No candidates match those filters.")) +
            "\nThe model combines weapon-class damage priorities with current per-weapon price premiums. Asking prices and resale targets are estimates, never guaranteed sales.";
    }
    internal static RivenAppraisal ApplyTradeChat(RivenAppraisal appraisal, TradeChatSummary trade)
    {
        var anchors = new List<double>();
        if (trade.WtbCount >= 3 && trade.WtbMedian.HasValue) anchors.Add(trade.WtbMedian.Value);
        if (trade.WtsCount >= 3 && trade.WtsMedian.HasValue) anchors.Add(trade.WtsMedian.Value);
        if (anchors.Count == 0 || appraisal.RecommendedPrice <= 0) return appraisal;
        var chatAnchor = Math.Clamp(anchors.Average(), appraisal.RecommendedPrice * .35, appraisal.RecommendedPrice * 3);
        int Adjust(int value) => Math.Max(1, (int)Math.Round((value * .90 + chatAnchor * .10) / 5) * 5);
        var fair = Adjust(appraisal.RecommendedPrice);
        var quick = Math.Min(Adjust(appraisal.QuickPrice), fair > 5 ? fair - 5 : fair);
        var patient = Math.Max(Adjust(appraisal.PatientPrice), fair + 5);
        return appraisal with { RecommendedPrice = fair, QuickPrice = quick, PatientPrice = patient };
    }
    internal static string FormatAppraisal(RivenAppraisal appraisal, TradeChatSummary trade, TradeChatImport? imported = null)
    {
        string P(int? value) => value.HasValue && value.Value > 0 ? $"{value.Value:N0}p" : "unavailable";
        var stats = string.Join(" · ", appraisal.Positives.Select(Pretty).Select(s => "+" + s).Concat(appraisal.Negative is null ? [] : ["−" + Pretty(appraisal.Negative)]));
        var bands = appraisal.PriceBands.Count == 0 ? "none" : string.Join("; ", appraisal.PriceBands.Select(p => $"{p.Key}: {p.Value}"));
        var weekly = appraisal.WeeklyCompletedTrades is null ? "No matching legacy DE aggregate." : $"Legacy DE completed {(appraisal.WeeklyCompletedTrades.Rerolled ? "rerolled" : "unrolled")} trades (archive last updated 13 May 2024): median {appraisal.WeeklyCompletedTrades.Median:0.#}p, average {appraisal.WeeklyCompletedTrades.Average:0.#}p, range {appraisal.WeeklyCompletedTrades.Minimum:0.#}–{appraisal.WeeklyCompletedTrades.Maximum:0.#}p. This receives low prior weight.";
        var age = appraisal.MedianListingAgeHours.HasValue ? $" Median current listing age: {Duration(appraisal.MedianListingAgeHours.Value)}" +
            (appraisal.ListingAgeAdjustmentPct is { } ageAdjustment and not 0 ? $"; stale-ask adjustment {ageAdjustment:+0;-0}%" : "") + "." : "";
        var outliers = appraisal.ExcludedAskOutliers > 0 ? $" {appraisal.ExcludedAskOutliers} extreme ask(s) excluded from pricing." : "";
        var closures = appraisal.ObservedClosures > 0 ? $" Bot-observed matching closures: {appraisal.ObservedClosures}; median listing-to-observed-close time {Duration(appraisal.MedianObservedLifetimeHours!.Value)} (sale unconfirmed)." : " No matching closures have been observed by this bot yet.";
        var confirmed = appraisal.ConfirmedSales > 0 ? $" Confirmed user-reported matching sales: {appraisal.ConfirmedSales}; median {appraisal.ConfirmedMedianPrice:0.#}p" +
            (appraisal.ConfirmedMedianLifetimeHours.HasValue ? $" in {Duration(appraisal.ConfirmedMedianLifetimeHours.Value)}" : "") +
            (appraisal.SaleVelocityAdjustmentPct is { } velocity and not 0 ? $"; sale-velocity adjustment {velocity:+0;-0}%" : "") + "." : " No confirmed exact-roll sales recorded yet.";
        var chatLine = trade.Observations > 0 ? $"Trade chat (30d): {trade.WtbCount} WTB, median {trade.WtbMedian:0.#}p; {trade.WtsCount} WTS, median {trade.WtsMedian:0.#}p." : "No matching local trade-chat observations in the last 30 days.";
        var importLine = imported is null ? "" : $" Imported {imported.Added.Length} new trade-chat observations; {imported.DuplicateCount} duplicates ignored.";
        var quality = appraisal.RollQualityPct.HasValue ? $" · entered roll quality {appraisal.RollQualityPct:0.#}%" : "";
        var grades = appraisal.StatGrades is { Count: > 0 } ? "\nGrades: " + string.Join(" · ", appraisal.StatGrades.Select(g => $"{(g.Positive ? "+" : "−")}{Pretty(g.Slug)} **{g.Grade}** ({g.VariancePct:+0.###;-0.###;0}%)")) : "";
        var guidance = appraisal.BuildGuidance.Length > 0 ? $"\nBuild check: {appraisal.BuildGuidance}" : "";
        var harmless = appraisal.SupportedHarmlessNegatives is { Length: > 0 }
            ? string.Join(", ", appraisal.SupportedHarmlessNegatives.Select(Pretty)) : "none confirmed";
        var usefulness = appraisal.DesiredPositiveCount.HasValue
            ? $"\nWeapon profile ({appraisal.DesirabilityProfileSource}): `{appraisal.DesirabilityExpression}`\n" +
              $"Supported harmless negatives: {harmless}.\n" +
              $"Assessment: **{appraisal.DesiredPositiveCount}/{appraisal.Positives.Length} desired positives** · {(appraisal.PreferredRoll == true ? "preferred selling roll" : "not a preferred selling roll")}. {appraisal.RollUsefulness}"
            : "";
        const string disclaimer = "Fair uses the weighted 35th percentile of comparable asks; Quick/Patient use the 18th/65th percentiles. Asks are not sales. Confirmed matching sales get the strongest weight; the DE archive is stale and has no roll details.";
        var content = $"**{appraisal.WeaponName} appraisal** · {appraisal.Confidence} confidence{quality}\n{stats}\n" +
            grades + guidance + usefulness + "\n" +
            $"Quick sale: **{P(appraisal.QuickPrice)}** · Fair: **{P(appraisal.RecommendedPrice)}** · Patient: **{P(appraisal.PatientPrice)}**\n" +
            $"Exact live asks: {appraisal.ExactAsks} ({appraisal.OnlineAsks} online); close-roll asks: {appraisal.SimilarAsks}. Comparable range {P(appraisal.LowestAsk)}–{P(appraisal.HighestAsk)}; median {P(appraisal.MedianAsk)}.\n" +
            $"Ask bands — {bands}.{age}{outliers}{closures}{confirmed}\n{weekly}\n{chatLine}{importLine}\n" + disclaimer;
        if (content.Length <= 1900) return content;
        var suffix = "\n… Details shortened to fit Discord.\n" + disclaimer;
        return content[..Math.Max(0, 1900 - suffix.Length)] + suffix;
    }
    private static string Pretty(string slug) => RivenPricing.DisplayName(slug);
    private static string Duration(double hours) => hours < 1 ? $"{Math.Max(1, Math.Round(hours * 60)):0}m" : hours < 48 ? $"{hours:0.#}h" : $"{hours / 24:0.#}d";
    private static string Roll(object[] roll, bool positive)
    {
        var slug = roll[0] is JsonElement s ? s.Text() : roll[0].ToString() ?? "";
        double? value = roll[1] is JsonElement j ? j.Number() : roll[1] is double d ? d : null;
        var suffix = RivenPricing.Unit(slug) switch { "meters" => "m", "seconds" => "s", "flat" => "", _ => "%" };
        return $"{(value.HasValue ? value.Value.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + suffix : positive ? "+" : "−")} {Pretty(slug)}";
    }
}
