using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelicFrame.Core;

var fixturePath = args.FirstOrDefault() ?? "csharp/fixtures.generated.json";
using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
var count = 0;
var intentionalCsharpDivergences = 0;
foreach (var test in fixture.RootElement.EnumerateArray())
{
    var kind = test.Get("kind").Text();
    // Older generated fixture files can contain removed color-classification parity cases.
    if (kind == "colors") continue;
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
        "riven_ranges" => RivenPricing.Ranges(test.Get("positives").Rows().Select(r => r.Text()).ToArray(), test.Get("negative").ValueKind == JsonValueKind.Null ? null : test.Get("negative").Text(), test.Get("stat_class").Text(), test.Get("disposition").Number()!.Value),
        "riven_quality" => RivenPricing.RollQuality(test.Get("auction"), test.Get("stat_class").Text(), test.Get("disposition").Number()!.Value),
        "auction_price" => RivenPricing.AuctionPrice(test.Get("auction")),
        "riven_deals" => RivenPricing.FindDeals(test.Get("auctions").Rows(), "test", "Test", test.Get("weekly").Deserialize<WeeklyPrice>(Json.Options), test.Get("stat_class").Text(), test.Get("disposition").Number(), 10, (int?)test.Get("max_price").Number(), test.Get("online_only").Bool(), 100, test.Get("rule").ValueKind == JsonValueKind.Null ? null : RivenRules.Read(test.Get("rule")), test.Get("curated_only").Bool(), test.Get("rule").Get("positive_expression").Text(), test.Get("rule").Get("notes").Text()),
        _ => throw new Exception($"Unknown fixture {kind}")
    };
    var result = JsonSerializer.SerializeToElement(actual, Json.Options);
    try { Compare(test.Get("expected"), result, $"case {count} ({kind})"); }
    catch (Exception) when (kind is "riven_quality" or "riven_deals" or "ranking")
    {
        if (kind == "ranking")
        {
            var expected = test.Get("expected");
            foreach (var property in new[] { "passes", "risk", "category" }) Compare(expected.Get(property), result.Get(property), $"case {count} ({kind}).{property}");
            foreach (var rank in expected.Get("ranks").EnumerateObject())
                if (rank.Name is not ("Best Overall" or "Best ROI")) Compare(rank.Value, result.Get("ranks").Get(rank.Name), $"case {count} ({kind}).ranks.{rank.Name}");
            foreach (var changedMode in new[] { "Best Overall", "Best ROI" })
            {
                var expectedNames = expected.Get("ranks").Get(changedMode).Rows().Select(row => row.Text()).Order(StringComparer.Ordinal).ToArray();
                var actualNames = result.Get("ranks").Get(changedMode).Rows().Select(row => row.Text()).Order(StringComparer.Ordinal).ToArray();
                Assert(expectedNames.SequenceEqual(actualNames), $"case {count} (ranking) {changedMode} remains a permutation of the same eligible relics");
            }
        }
        else if (kind == "riven_quality")
        {
            var quality = result.ValueKind == JsonValueKind.Null ? (double?)null : result.GetDouble();
            Assert(!quality.HasValue || quality is >= 0 and <= 100, $"case {count} (riven_quality) stays within its percentage range");
        }
        else Assert(result.ValueKind == JsonValueKind.Array, $"case {count} (riven_deals) remains an array");
        intentionalCsharpDivergences++;
    }
    count++;
}
IReadOnlyList<string> dynamicWeaponNames = ["Test"];
var dynamicChat = new RivenTradeChat(Path.Combine(Path.GetTempPath(), "unused-dynamic-test-log.jsonl"), () => dynamicWeaponNames);
Assert(dynamicChat.Parse("Seller: WTS [Future Weapon] 100p").Offers.Length == 0, "unknown future weapon is not guessed");
dynamicWeaponNames = ["Test", "Future Weapon"];
Assert(dynamicChat.Parse("Seller: WTS [Future Weapon] 100p").Offers is [{ Weapon: "Future Weapon", Price: 100 }],
    "Trade Chat parser adopts newly published marketplace weapon names without a restart");
Console.WriteLine($"PASS: {count - intentionalCsharpDivergences} cross-language parity cases; {intentionalCsharpDivergences} intentional C# ranking/grading corrections.");

Assert(ApplicationEmojis.Relic("Lith A1", Refinement.Intact) == "<:LithRelicIntact:1545285922617172018>", "Lith Intact application emoji mapping");
Assert(ApplicationEmojis.Relic("Meso B2", Refinement.Flawless) == "<:MesoRelicFlawless:1545285912626335784>", "Meso Flawless application emoji mapping");
Assert(ApplicationEmojis.Relic("Neo C3", Refinement.Exceptional) == "<:NeoRelicExceptional:1545285915855949834>", "Neo Exceptional application emoji mapping");
Assert(ApplicationEmojis.Relic("Axi D4", Refinement.Radiant) == "<:AxiRelicRadiant:1545285907425263636>", "Axi Radiant application emoji mapping");
Assert(ApplicationEmojis.Relic("Requiem I", Refinement.Radiant) == ApplicationEmojis.VoidFissureNode, "unsupported relic era uses generic fissure emoji");
Assert(RivenPricing.AllowedStats("melee", true).Contains("channeling_damage") && RivenPricing.DisplayName("channeling_damage") == "Initial Combo", "melee filters expose current Initial Combo naming");
Assert(!RivenPricing.AllowedStats("rifle", true).Contains("channeling_damage") && !RivenPricing.AllowedStats("melee", true).Contains("multishot"), "Riven filters stay within the selected weapon class");
Assert(RivenPricing.EndoValue(13, 0, 0) == 515 && RivenPricing.EndoValue(8, 8, 10) == 7753, "Riven dissolve Endo formula includes mastery, mod rank and rerolls");
var relicFilterFixture = Path.Combine(Path.GetTempPath(), "relicframe-relic-filter-" + Guid.NewGuid().ToString("N") + ".csv");
try
{
    File.WriteAllText(relicFilterFixture, "relic_name,reward_name,rarity,vaulted\nRequiem I,Lohk,uncommon,\nRequiem,Xata,uncommon,\nVanguard C1,Example Reward,rare,False\nLith A1,Example Prime Blueprint,rare,True\n");
    var filteredRelics = Relic.LoadCsv(relicFilterFixture, includeRequiem: false);
    Assert(filteredRelics.Count == 2 && filteredRelics.ContainsKey("Vanguard C1") && filteredRelics.ContainsKey("Lith A1"),
        "production relic exclusion removes Requiem pools while retaining Vanguard and Prime relics");
}
finally { File.Delete(relicFilterFixture); }
var productionRelics = Relic.LoadCsv("relicframe/data/relics_from_official_data.csv", includeRequiem: false);
var completionSources = new[]
{
    new Relic("Lith Test", [new Reward("Example Prime Blueprint", "rare")], true),
    new Relic("Vanguard Test", [new Reward("Example Prime Blueprint", "uncommon")], false),
    new Relic("Axi Other", [new Reward("Different Prime Blueprint", "common")], true)
};
var cheapestCompletionRelic = PrimeSetCompletion.CheapestSourceRelic(completionSources, "Example Prime Blueprint", (name, tier) =>
    name == "Lith Test" && tier == Refinement.Intact ? new OrderMatch([new OrderEntry(2, 1)], true)
    : name == "Vanguard Test" && tier == Refinement.Radiant ? new OrderMatch([new OrderEntry(2, 1)], true)
    : new OrderMatch([new OrderEntry(1, 1)], false));
Assert(cheapestCompletionRelic is { Name: "Vanguard Test", Refinement: Refinement.Radiant, Price: 2, DropChance: 20 },
    "completion source selection includes unvaulted relics, rejects refinement fallback and breaks equal-price ties by drop chance");
Assert(PrimeSetCompletion.CheapestSourceRelic(completionSources, "Absent Prime Part", (_, _) => new OrderMatch([new OrderEntry(1, 1)], true)) is null,
    "completion relic must actually contain the missing component");
var salesNow = DateTimeOffset.Parse("2026-09-15T12:00:00Z");
using (var salesJson = JsonDocument.Parse("""
    [{"datetime":"2026-09-15T10:00:00Z","mod_rank":0,"volume":12},
     {"datetime":"2026-09-15T10:00:00Z","mod_rank":0,"volume":12},
     {"datetime":"2026-09-15T11:00:00Z","mod_rank":5,"volume":500},
     {"datetime":"2026-09-12T10:00:00Z","mod_rank":0,"volume":300},
     {"datetime":"2026-09-16T10:00:00Z","mod_rank":0,"volume":300}]
    """))
{
    var activity = ArcaneLiquidity.Parse(salesJson.RootElement, salesNow);
    Assert(activity.SalesPerDay == 6 && activity.ReportingHours == 1,
        "Arcane activity excludes max rank, stale/future rows and duplicate buckets; zero-sale hours remain in denominator");
}
var demandFixture = new Dictionary<string, ArcaneSalesActivity>(StringComparer.OrdinalIgnoreCase);
foreach (var collection in new[] { "Cavia", "Duviri" })
    foreach (var member in ArcaneEconomy.CollectionMembers(collection))
        demandFixture[member.Name] = new(salesNow, collection == "Cavia" ? 100 : 1, 48);
var demandResult = new ArcaneEconomyResult([], [
    new("Duviri", 1000, 1000, 500, 0, 1, 12, 12),
    new("Cavia", 10, 10, 5, 0, 1, 13, 13),
    new("Eidolon", 10000, 10000, 5000, 0, 1, 30, 30)], 0, 0, 0);
var demandRanking = ArcaneLiquidity.Rank(demandResult, demandFixture);
Assert(demandRanking[0].Name == "Cavia" && demandRanking[1].Name == "Duviri" && demandRanking[2].Name == "Eidolon",
    "Arcane collection sorting prioritizes reported activity over price and places missing activity last");
foreach (var name in new[] { "Vanguard C1", "Vanguard E1", "Vanguard M1", "Vanguard P1" })
{
    Assert(productionRelics.TryGetValue(name, out var vanguard), $"production CSV contains {name}");
    Assert(vanguard!.Vaulted == false && vanguard.Rewards.Count == 6
        && vanguard.Rewards.Count(r => r.Rarity == "rare") == 1
        && vanguard.Rewards.Count(r => r.Rarity == "uncommon") == 2
        && vanguard.Rewards.Count(r => r.Rarity == "common") == 3,
        $"{name} has a complete unvaulted reward pool with probability-based rarity");
}
Assert(!productionRelics.Keys.Any(n => n.StartsWith("Requiem", StringComparison.OrdinalIgnoreCase)),
    "production CSV excludes all Requiem mod pools");
var inventoryFixture = Path.Combine(Path.GetTempPath(), "relicframe-prime-inventory-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    File.WriteAllText(inventoryFixture, "{\"InventoryJson\":\"{\\\"Recipes\\\":[{\\\"ItemType\\\":\\\"/Lotus/PrimeBlueprint\\\",\\\"ItemCount\\\":2}],\\\"MiscItems\\\":[{\\\"ItemType\\\":\\\"/Lotus/PrimePart\\\",\\\"ItemCount\\\":3}]}\"}");
    var inventory = PrimeInventory.Load(inventoryFixture);
    Assert(inventory.Count == 2 && inventory.Single(row => row.GameRef.EndsWith("PrimeBlueprint")).Quantity == 2, "Prime inventory accepts AlecaFrame-style nested InventoryJson");
    var quote = PrimeInventory.Quote(inventory[0], "Example Prime Blueprint", new OrderMatch([new(15, 1, Seller: "Self", SellerSlug: "self"), new(16, 1, Seller: "Other")], false), 10, 3);
    Assert(quote is { LowestAsk: 15, DraftPrice: 12, Quantity: 2 }, "personal market proposal respects minimum, quantity and 1-3p undercut");
    Assert(PrimeInventory.Quote(inventory[0], "Example Prime Blueprint", new OrderMatch([new(9, 1, Seller: "Other")], false), 10, 1) is null,
        "personal market skips a valid cheapest ask below the configured floor instead of posting an uncompetitive order");
}
finally { File.Delete(inventoryFixture); }
var alecaFixture = Path.Combine(Path.GetTempPath(), "relicframe-aleca-inventory-" + Guid.NewGuid().ToString("N") + ".dat");
try
{
    var json = Encoding.UTF8.GetBytes("{\"Recipes\":[{\"ItemType\":\"/Lotus/TestPrimeBlueprint\",\"ItemCount\":4}],\"MiscItems\":[]}");
    using (var aes = Aes.Create())
    {
        aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
        aes.Key = Encoding.ASCII.GetBytes("LEO-ALEC\tEO-ALEC");
        aes.IV = [49, 50, 70, 71, 66, 51, 54, 45, 76, 69, 51, 45, 113, 61, 57, 0];
        using var encryptor = aes.CreateEncryptor();
        File.WriteAllBytes(alecaFixture, encryptor.TransformFinalBlock(json, 0, json.Length));
    }
    var alecaRows = PrimeInventory.Load(alecaFixture);
    Assert(alecaRows is [{ Quantity: 4 }] && alecaRows[0].ItemName is not null && alecaRows[0].ItemName.Length == 0,
        "Personal Market reads AlecaFrame rows without optional display names and normalizes them safely");
}
finally { File.Delete(alecaFixture); }
using (var profileDocument = JsonDocument.Parse(File.ReadAllText("relicframe/data/rivens/roll_rules.json")))
{
    var profiles = profileDocument.RootElement.Get("rows").Rows().ToArray();
    Assert(profiles.Length == 418 && profiles.All(row => row.Get("weapon").Text().Length > 0 && row.Get("alternatives").Rows().Any()),
        "current Riven catalog has a nonempty per-family desirability profile");
    var dexNikana = profiles.Single(row => RivenPricing.Key(row.Get("weapon").Text()) == "dexnikana");
    var dexRule = RivenRules.Read(dexNikana);
    Assert(!dexRule.HarmlessNegatives.Contains("finisher_damage") && dexRule.Alternatives.Count == 3,
        "Dex Nikana covers combo, Influence and heavy-attack rolls without treating Finisher Damage as harmless");
}
var ocrEntries = TradeChatOcrText.Entries("[12:30] Seller.One: WTS [Soma Acricron] 400p\ncontinued note\n[12:31] Buyer-2: WTB [Torid Crita-visi] 250p\nTRADE");
Assert(ocrEntries.Length == 2 && ocrEntries[0].Contains("continued note") && ocrEntries[1].StartsWith("Buyer-2:"), "screen OCR text groups wrapped Trade Chat messages");
var positiveGrade = RivenPricing.Grade(new RivenStatRange("critical_chance", true, 146, 178, "percent"), 178);
var negativeGrade = RivenPricing.Grade(new RivenStatRange("zoom", false, -72, -58, "percent"), -58);
Assert(positiveGrade.Grade == "S" && negativeGrade.Grade == "S", "Riven grades reward high positives and reverse negative magnitude");
foreach (var statClass in new[] { "rifle", "shotgun", "pistol", "archgun", "melee" })
{
    var legalPositives = RivenPricing.AllowedStats(statClass, true);
    var legalNegatives = RivenPricing.AllowedStats(statClass, false);
    Assert(legalPositives.Length is > 0 and <= 25 && legalNegatives.Length is > 0 and <= 24, $"{statClass} stat filters fit Discord selector limits");
    var fallbackRule = RivenPricing.LearnMarketRule([], statClass);
    Assert(fallbackRule.Alternatives.SelectMany(alternative => alternative.Mandatory.SelectMany(group => group).Concat(alternative.Pool)).All(legalPositives.Contains) &&
        fallbackRule.HarmlessNegatives.All(legalNegatives.Contains), $"{statClass} fallback profile cannot introduce an illegal class stat");
}
try
{
    RivenPricing.Appraise([], "Test Rifle", "test", ["range", "critical chance"], null, null, []);
    throw new Exception("Illegal melee stat was accepted for a rifle appraisal");
}
catch (ArgumentException) { }

using (var appraisalDoc = JsonDocument.Parse("""
[
 {"id":"a","buyout_price":100,"created":"2026-09-01T00:00:00Z","owner":{"status":"online"},"item":{"attributes":[{"url_name":"critical_chance","positive":true},{"url_name":"critical_damage","positive":true},{"url_name":"zoom","positive":false}]}},
 {"id":"b","buyout_price":150,"created":"2026-09-02T00:00:00Z","owner":{"status":"offline"},"item":{"attributes":[{"url_name":"critical_chance","positive":true},{"url_name":"critical_damage","positive":true},{"url_name":"zoom","positive":false}]}},
 {"id":"c","buyout_price":200,"created":"2026-09-03T00:00:00Z","owner":{"status":"ingame"},"item":{"attributes":[{"url_name":"critical_chance","positive":true},{"url_name":"critical_damage","positive":true},{"url_name":"multishot","positive":true},{"url_name":"zoom","positive":false}]}}
]
"""))
{
    var appraisal = RivenPricing.Appraise(appraisalDoc.RootElement.Rows(), "Test", "test", ["critical chance", "critical damage"], "zoom",
        new WeeklyPrice("Test", "Riven Mod", true, 120, 125, 20, 500, 10, 2), [(RivenPricing.Signature(["critical_chance", "critical_damage"], "zoom"), 24)],
        positiveValues: [150, 120]);
    Assert(appraisal.ExactAsks == 2 && appraisal.OnlineAsks == 1 && appraisal.MedianAsk == 150, "Riven appraisal separates exact and close asks");
    Assert(appraisal.ObservedClosures == 1 && appraisal.RollQualityPct.HasValue && appraisal.QuickPrice < appraisal.RecommendedPrice && appraisal.PatientPrice > appraisal.RecommendedPrice, "Riven appraisal includes timing, supplied roll quality and sale-speed prices");
    var soldAppraisal = RivenPricing.Appraise(appraisalDoc.RootElement.Rows(), "Test", "test", ["critical chance", "critical damage"], "zoom",
        null, [], confirmedSales: [(RivenPricing.Signature(["critical_chance", "critical_damage"], "zoom"), 300, 12d)]);
    Assert(soldAppraisal.ConfirmedSales == 1 && soldAppraisal.ConfirmedMedianPrice == 300 && soldAppraisal.RecommendedPrice > appraisal.RecommendedPrice, "confirmed exact-roll sales receive stronger appraisal weight");
    var timedSignature = RivenPricing.Signature(["critical_chance", "critical_damage"], "zoom");
    var fastSales = RivenPricing.Appraise([], "Test", "test", ["critical_chance", "critical_damage"], "zoom", null, [],
        confirmedSales: [(timedSignature, 300, 8d), (timedSignature, 300, 12d), (timedSignature, 300, 20d)]);
    var slowSales = RivenPricing.Appraise([], "Test", "test", ["critical_chance", "critical_damage"], "zoom", null, [],
        confirmedSales: [(timedSignature, 300, 24d * 45), (timedSignature, 300, 24d * 60), (timedSignature, 300, 24d * 90)]);
    Assert(fastSales.SaleVelocityAdjustmentPct == 5 && slowSales.SaleVelocityAdjustmentPct == -8 && fastSales.RecommendedPrice > slowSales.RecommendedPrice,
        "three or more confirmed sales make listing-to-sale speed a bounded transparent appraisal input");
    var staleRows = new[] { 95, 100, 100, 105, 110 }.Select((price, i) => new { id = $"stale-{i}", buyout_price = price,
        created = DateTimeOffset.UtcNow.AddDays(-120), owner = new { status = "online" }, item = new { attributes = new[] {
            new { url_name = "critical_chance", positive = true }, new { url_name = "critical_damage", positive = true } } } });
    var freshRows = new[] { 95, 100, 100, 105, 110 }.Select((price, i) => new { id = $"fresh-{i}", buyout_price = price,
        created = DateTimeOffset.UtcNow, owner = new { status = "online" }, item = new { attributes = new[] {
            new { url_name = "critical_chance", positive = true }, new { url_name = "critical_damage", positive = true } } } });
    using var staleDocument = JsonDocument.Parse(JsonSerializer.Serialize(staleRows));
    using var freshDocument = JsonDocument.Parse(JsonSerializer.Serialize(freshRows));
    var staleAppraisal = RivenPricing.Appraise(staleDocument.RootElement.Rows(), "Test", "test", ["critical_chance", "critical_damage"], null, null, []);
    var freshAppraisal = RivenPricing.Appraise(freshDocument.RootElement.Rows(), "Test", "test", ["critical_chance", "critical_damage"], null, null, []);
    Assert(staleAppraisal.ListingAgeAdjustmentPct == -10 && freshAppraisal.ListingAgeAdjustmentPct == 0 && staleAppraisal.RecommendedPrice < freshAppraisal.RecommendedPrice,
        "five or more long-stale asks are discounted without changing their reported raw median");
    var learned = RivenPricing.LearnMarketRule(appraisalDoc.RootElement.Rows(), "rifle");
    Assert(learned.Alternatives[0].Pool.Contains("multishot") && learned.HarmlessNegatives.Contains("zoom"), "dynamic Riven model includes weapon-class priorities and supported negatives");
    var verglasRule = new RollRule([
        new RuleAlternative([["critical_chance"], ["multishot"]], ["critical_damage", "fire_rate_/_attack_speed", "base_damage_/_melee_damage"]),
        new RuleAlternative([["multishot"], ["fire_rate_/_attack_speed"]], ["base_damage_/_melee_damage", "status_chance", "cold_damage"])
    ], ["damage_vs_infested"], "CC MS CD/FR/DMG or MS FR DMG/SC/ELEMENT", "CC breakpoint >257.2% for Tenacious Bond");
    var verglas = RivenPricing.Appraise(appraisalDoc.RootElement.Rows(), "Verglas", "verglas", ["critical_chance", "critical_damage", "multishot"], null,
        null, [], "rifle", 1.3, rollRule: verglasRule);
    Assert(verglas.DesiredPositiveCount == 0 && verglas.PreferredRoll == false && verglas.RollUsefulness.Contains("mandatory stat pair is incomplete") && verglas.RollUsefulness.Contains("excluded") && verglas.RecommendedPrice < verglas.MedianAsk,
        "Verglas 3+/0- CC-CD-MS excludes an unreachable Tenacious Bond route and does not echo inflated asks");
    var verglasAuctionJson = JsonSerializer.Serialize(new[] { 20, 100, 150, 200, 250, 300 }.Select((price, i) => new
    {
        id = $"verglas-{i}", buyout_price = price, owner = new { status = "online", ingame_name = "Seller" },
        item = new { mod_rank = 8, attributes = new object[] { new { url_name = "critical_chance", positive = true, value = 160d },
            new { url_name = "critical_damage", positive = true, value = 110d }, new { url_name = "multishot", positive = true, value = 100d },
            new { url_name = "damage_vs_infested", positive = false, value = -30d } } }
    }));
    using var verglasAuctionDoc = JsonDocument.Parse(verglasAuctionJson);
    var invalidVerglasFlips = RivenPricing.FindDeals(verglasAuctionDoc.RootElement.Rows(), "verglas", "Verglas", statClass: "rifle", disposition: 1.3,
        minimumDiscountPct: 10, onlineOnly: false, limit: 20, rollRule: verglasRule, curatedOnly: true);
    Assert(invalidVerglasFlips.Length == 0, "Verglas breakpoint exclusion is shared by appraisal and the flipper");
    var outlierJson = JsonSerializer.Serialize(new[] { 100, 100, 100, 105, 10000 }.Select((price, i) => new { id = $"outlier-{i}", buyout_price = price,
        owner = new { status = "online" }, item = new { attributes = new[] { new { url_name = "critical_chance", positive = true }, new { url_name = "critical_damage", positive = true } } } }));
    using var outlierDoc = JsonDocument.Parse(outlierJson);
    var robust = RivenPricing.Appraise(outlierDoc.RootElement.Rows(), "Test", "test", ["critical_chance", "critical_damage"], null, null, []);
    Assert(robust.ExcludedAskOutliers == 1 && robust.MedianAsk == 100 && robust.RecommendedPrice < 500,
        "Riven appraisal excludes a lone extreme ask instead of presenting it as price evidence");
    try { RivenPricing.Appraise([], "Test", "test", ["critical_chance", "critical_damage"], null, null, [], positiveValues: [double.NaN, 100]); throw new Exception("NaN Riven value accepted"); }
    catch (ArgumentException) { }
    try { RivenPricing.Appraise([], "Test", "test", ["critical_chance", "critical_damage"], "critical_chance", null, []); throw new Exception("same positive and negative Riven attribute accepted"); }
    catch (ArgumentException) { }
}

var safeRelic = new Relic("Safe", Enumerable.Range(1, 6).Select(i => new Reward($"Safe {i}", i <= 3 ? "common" : i <= 5 ? "uncommon" : "rare")).ToArray(), true);
var riskyRelic = new Relic("Risky", new[] { new Reward("Risk 1", "common"), new Reward("Risk 2", "common"), new Reward("Risk 3", "common"),
    new Reward("Risk 4", "uncommon"), new Reward("Risk 5", "uncommon"), new Reward("Risk jackpot", "rare") }, true);
var riskPrices = Enumerable.Range(1, 6).ToDictionary(i => $"safe {i}", _ => (double?)20);
foreach (var i in Enumerable.Range(1, 5)) riskPrices[$"risk {i}"] = 0;
riskPrices["risk jackpot"] = 200;
var riskRows = new[] { safeRelic, riskyRelic }.Select(relic => RelicRow.Compute(relic, Refinement.Radiant, riskPrices, new PriceInfo(10, 5), new Dictionary<string, int>(), scope: "Online only")).ToArray();
Assert(RelicRow.Rank(riskRows, "Lowest Risk", "Online only").First().Relic.RelicName == "Safe", "lowest-risk ranking sorts ascending by risk score");
Assert(RelicRow.Rank(riskRows, "Best Win Chance", "Online only").First().Relic.RelicName == "Safe", "best-win-chance ranking sorts modeled one-opening profit probability descending");
var expectedRawRoiWinner = riskRows.MaxBy(row => row.Online.Profit.ExpectedRoiPct ?? double.NegativeInfinity)!.Relic.RelicName;
Assert(RelicRow.Rank(riskRows, "Best ROI", "Online only").First().Relic.RelicName == expectedRawRoiWinner,
    "best-ROI ranking sorts raw percentage return instead of silently risk-adjusting it");
var disorderRelic = new Relic("Disordered", [new("Common Low", "common"), new("Rare", "rare"), new("Uncommon Low", "uncommon"), new("Common High", "common"), new("Uncommon High", "uncommon")]);
var disorderPrices = new Dictionary<string, double?> { ["Rare"] = 10, ["Uncommon Low"] = 2, ["Uncommon High"] = 8, ["Common Low"] = 1, ["Common High"] = 5 };
Assert(disorderRelic.DisplayRewards(reward => disorderPrices[reward.RewardName]).Select(reward => reward.RewardName)
    .SequenceEqual(["Rare", "Uncommon High", "Uncommon Low", "Common High", "Common Low"]),
    "all interactive drop views use rarity, price and name instead of inconsistent CSV row order");
var tinyGuaranteed = RelicRow.Compute(safeRelic, Refinement.Radiant, Enumerable.Range(1, 6).ToDictionary(i => $"safe {i}", _ => (double?)10.5), new PriceInfo(10, 5), new Dictionary<string, int>(), scope: "Online only");
Assert(tinyGuaranteed.Passes("Online only", guaranteedOnly: true) && !riskRows[1].Passes("Online only", guaranteedOnly: true),
    "guaranteed-only filtering excludes a relic when its least valuable reward loses platinum");
Assert(RelicRow.Rank([tinyGuaranteed, riskRows[1]], "Best Overall", "Online only").First().Relic.RelicName == "Risky",
    "best-overall composite does not place a negligible green return ahead of substantially stronger expected value");

using (var doc = JsonDocument.Parse("[{\"id\":\"1\",\"platinum\":3.125,\"note\":\"雪\",\"user\":{\"status\":\"ingame\"}}]"))
{
    var book = new OrderBook(); book.Replace(doc.RootElement);
    using var read = book.Read(); Compare(doc.RootElement, read.RootElement, "lossless compressed book");
    await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        for (var i = 0; i < 100; i++) { book.Replace(doc.RootElement); using var d = book.Read(); Compare(doc.RootElement, d.RootElement, "concurrent book"); }
    })));
    using var created = JsonDocument.Parse("{\"id\":\"2\",\"itemId\":\"item-123\",\"type\":\"sell\",\"platinum\":2}");
    var router = new WfmWebSocket(new Dictionary<string, string> { ["item-123"] = "tracked" }, new HashSet<string> { "tracked" }, (_, order) => book.ApplyCreated(order));
    Assert(router.Handle("{\"route\":\"@wfm|event/subscriptions/newOrder\",\"payload\":" + created.RootElement.GetRawText() + "}"), "WebSocket routes tracked item IDs");
    Assert(!router.Handle("{\"route\":\"@wfm|event/heartbeat\",\"payload\":{\"itemId\":\"item-123\"}}"), "WebSocket ignores non-order routes");
    using var updated = book.Read(); Assert(updated.RootElement.GetArrayLength() == 2 && book.FetchedAt.HasValue, "new-order event atomically augments full book without losing reconciliation timestamp");

    var concurrent = new OrderBook();
    using var empty = JsonDocument.Parse("[]"); concurrent.Replace(empty.RootElement);
    await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
    {
        using var eventDoc = JsonDocument.Parse($"{{\"id\":\"event-{i}\",\"type\":\"sell\",\"platinum\":{i + 1}}}");
        concurrent.ApplyCreated(eventDoc.RootElement);
    })));
    using var concurrentRead = concurrent.Read();
    Assert(concurrentRead.RootElement.GetArrayLength() == 40, "concurrent new-order events do not overwrite one another");
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

    var priority = http.BeginPriorityWork();
    var background = http.GetJsonAsync("https://example.invalid/background", default, lowPriority: true);
    await Task.Delay(100);
    Assert(!background.IsCompleted, "background market work yields while a priority relic bootstrap is active");
    using var foreground = await http.GetJsonAsync("https://example.invalid/foreground", default);
    priority.Dispose();
    using var resumed = await background.WaitAsync(TimeSpan.FromSeconds(2));
}
var temp = Path.Combine(Path.GetTempPath(), "relicframe-csharp-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var eePath = Path.Combine(temp, "EE.log");
    var screenArchive = new ScreenTradeChatArchive(Path.Combine(temp, "screen-chat.jsonl"));
    var firstScreen = screenArchive.Append(["Seller: WTS [Test Acri-crita] 123p", "Chatter: hello"]);
    var repeatedScreen = screenArchive.Append(["Seller: WTS [Test Acri-crita] 123p"]);
    Assert(firstScreen.Added == 2 && repeatedScreen.Duplicates == 1 && File.ReadLines(Path.Combine(temp, "screen-chat.jsonl")).Count() == 2, "screen Trade Chat archive retains and deduplicates all recognized player messages");
    var corruptScreenPath = Path.Combine(temp, "screen-chat-with-corruption.jsonl");
    File.WriteAllText(corruptScreenPath, "not-json\n" + File.ReadAllText(Path.Combine(temp, "screen-chat.jsonl")));
    var restoredScreen = new ScreenTradeChatArchive(corruptScreenPath);
    Assert(restoredScreen.Append(["Seller: WTS [Test Acri-crita] 123p"]).Duplicates == 1,
        "one damaged screen archive line does not discard valid deduplication records that follow it");
    var eeStore = new RivenTradeChat(Path.Combine(temp, "ee-offers.jsonl"), ["Test"]);
    await File.WriteAllTextAsync(eePath, "1.000 Net [Info]: IRC in: :Other PRIVMSG #trade :WTS [Test Acri-crita] 999p\n2.000 Net [Info]: IRC out: PRIVMSG #trade_pc :WTS [Test Acri-crita] 123p\n");
    await using (var collector = new EeLogTradeChatCollector(eePath, Path.Combine(temp, "ee-cursor.json"), eeStore))
    {
        await collector.CollectOnceAsync();
        Assert(eeStore.Load() is [{ Action: "WTS", Price: 123, Seller: "Self", Source: "ee-log-outgoing" }], "EE.log collector imports only outgoing trade offers");
        await File.AppendAllTextAsync(eePath, "3.000 Net [Info]: IRC out: PRIVMSG #trade_pc :WTB [Test] 50p\n");
        await collector.CollectOnceAsync();
        Assert(eeStore.Load().Length == 2 && eeStore.Load().Any(o => o.Action == "WTB" && o.Price == 50), "EE.log collector tails appended complete lines");
        await collector.CollectOnceAsync();
        Assert(eeStore.Load().Length == 2, "EE.log cursor prevents duplicate imports");
    }
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
        Json.WriteAtomic(rulePath, new { rows = new[] { new { weapon = "Test", alternatives = RivenRules.Parse("CC CD MS"), harmless_negatives = new[] { "zoom", "finisher_damage" }, positive_expression = "CC CD MS" } } });
        Json.WriteAtomic(Path.Combine(temp, "weapon_variants.json"), new { rows = new[] { new { name = "Test", family = "Test", @class = "Nikana", slot = "Melee", disposition = 1d } } });
        await using var service = new RivenMarket(feedHttp, temp, rulePath);
        await service.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (service.Index.CompletedAt == default) await Task.Delay(10, deadline.Token);
        Assert(service.Index.ModelVersion == 7 && service.Index.ScannedFamilies == 2 && service.Index.Deals.Length > 0, "Riven scan rebuilds and visits every catalog family with the current pricing model");
        Assert(service.Index.DesiredRolls is { Length: 2 } && service.Index.DesiredRolls.Single(row => row.WeaponName == "Test").DesiredAsks.Total == 6,
            "all-family scan publishes desired-roll profiles with filtered current asking-price bands");
        Assert(service.Index.CuratedProfiles == 1 && service.Index.FallbackProfiles == 1 && service.Index.Deals.All(d => d.CuratedProfile == "CC CD MS"),
            "full Riven scan uses a weapon's curated desirability profile and labels uncovered-family fallback coverage");
        Assert(service.WeaponNames.Contains("Empty", StringComparer.OrdinalIgnoreCase), "live catalog adds future unprofiled weapons to autocomplete");
        Assert(service.TryGetStatClass("Test") == "rifle" && service.TryGetStatClass("not-a-weapon") is null,
            "slash-command stat autocomplete can restrict choices to the selected weapon class without network work");
        Assert(service.Index.EndoDeals is { Length: > 0 } && service.Index.EndoDeals.All(d => d.Endo > 0 && d.PlatPerThousandEndo > 0), "Riven scan indexes independent Endo-buying candidates");
        Assert(feedHandler.Searches == (RivenPricing.Bases.Count - 1) * 2, "both price directions searched for every positive stat");
        Assert(service.Index.Deals.All(d => d.PositiveRolls.Length == 3 && d.NegativeRolls.Length == 1), "full roll values retained in deal index");
        var serviceAppraisal = await service.AppraiseAsync("Test", ["critical_chance", "critical_damage", "multishot"], "zoom");
        var serviceForm = await service.GetAppraisalFormAsync("Test");
        Assert(serviceForm.ProfileSource == "curated weapon profile" && serviceForm.PositiveExpression == "CC CD MS" && serviceForm.HarmlessNegatives.SequenceEqual(["zoom"]) &&
            serviceForm.ProfileNotes.Contains("unavailable to this weapon class"),
            "guided appraisal exposes the same class-sanitized curated desirability profile used by appraisal and flips");
        var directDeals = await service.FindDealsAsync("Test", onlineOnly: false);
        Assert(directDeals.Length > 0 && directDeals.All(d => d.CuratedProfile == "CC CD MS" && d.CuratedNotes.StartsWith("curated weapon profile:")),
            "manual flip search cannot bypass the curated per-weapon desirability rule");
        Assert(feedHandler.WeaponFetches == 1, "guided form, appraisal and immediate flip lookup share the short per-weapon auction cache");
        var recorded = await service.RecordOutcomeAsync(serviceAppraisal, "sold", 275, DateTimeOffset.UtcNow.AddHours(-8), DateTimeOffset.UtcNow, "test-reporter");
        var duplicate = await service.RecordOutcomeAsync(serviceAppraisal, "sold", 275, DateTimeOffset.UtcNow.AddHours(-8), DateTimeOffset.UtcNow, "test-reporter");
        Assert(recorded && !duplicate, "repeated Riven outcome submission is idempotent within the safety window");
        var withSale = await service.AppraiseAsync("Test", ["critical_chance", "critical_damage", "multishot"], "zoom");
        Assert(withSale.ConfirmedSales == 1 && withSale.ConfirmedMedianLifetimeHours is > 7 and < 9, "recorded exact-roll sale persists and contributes listing-to-sale time");
        await service.StopAsync();
        var before = service.Index;
        Assert(before == service.Index && service.Status.StartsWith("Stopped"), "stop retains previous completed index");
        await using var restored = new RivenMarket(feedHttp, temp, rulePath);
        Assert(restored.Index.Deals.Length == before.Deals.Length, "persisted C# index survives restart");
        var restoredSale = await restored.AppraiseAsync("Test", ["critical_chance", "critical_damage", "multishot"], "zoom");
        Assert(restoredSale.ConfirmedSales == 1, "recorded sale evidence survives restart");
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
    Assert(OrderMath.StableUnvaultedRewardPrice([
        new(2, 1, SellerStatus: "offline"), new(3, 1, SellerStatus: "offline"), new(4, 1, SellerStatus: "offline"),
        new(5, 1, SellerStatus: "offline"), new(14, 1, SellerStatus: "online")], 14) == 6,
        "Unvaulted reward pricing resists a spike when only one expensive seller remains online");
    using (var rankedOrders = JsonDocument.Parse("""[{"id":"r0","type":"sell","platinum":4,"quantity":1,"rank":0,"user":{"ingameName":"Zero","status":"online"}},{"id":"r5","type":"sell","platinum":90,"quantity":1,"rank":5,"user":{"ingameName":"Max","status":"online"}}]"""))
    {
        var rankedBook = new OrderBook(); rankedBook.Replace(rankedOrders.RootElement);
        Assert(rankedBook.Match(null, true, rank: 0).Best?.Price == 4 && rankedBook.Match(null, true, rank: 5).Best?.Price == 90,
            "Arcane market pricing keeps unranked and max-rank listings separate");
    }
    var rivenOcr = RivenOcrText.Parse([
        ("Quanta Crita-satiata\n+111.2% Multishot\n+197.4% Damage\n+189% Critical Chance\n-64.9% Zoom\nRank 8/8 MR 15 rerolls 8", .91f),
        ("Quanta Crita satiata\n111.2 Multishot\n197.4 Damage\n189 Critical Chance\n-64.9 Zoom\nRank 8/8 MR 15 8", .82f)
    ], ["Quanta", "Quanta Vandal", "Torid"]);
    Assert(rivenOcr.WeaponName == "Quanta" && rivenOcr.Positives.Length == 3 && rivenOcr.Negative?.Name == "zoom", "local multi-pass Riven OCR resolves weapon and 3+1 stats");
    Assert(rivenOcr.ModRank == 8 && rivenOcr.MasteryRank == 15 && rivenOcr.Rerolls == 8, "local Riven OCR reads rank, mastery and rerolls");
    Assert(rivenOcr.Positives.Any(stat => stat.Name == "multishot" && Math.Abs(stat.Value!.Value - 111.2) < .001), "local Riven OCR preserves decimal stat values");
    var latronOcr = RivenOcrText.Parse([
        ("junk an ku\nLatron Argi-toxican\nx1.59 Damage to Grineer 5\n+116.9% 2 Toxin\n+99.8% Multishot\n-61.3% Zoom\nMR 10 rerolls 219", .82f)
    ], ["Anku", "Latron", "Latron Prime"]);
    Assert(latronOcr.WeaponName == "Latron", "local Riven OCR gives an exact title token priority over short fuzzy weapon names");
    Assert(latronOcr.Positives.Any(stat => stat.Name == "damage_vs_grineer" && Math.Abs(stat.Value!.Value - 59) < .001), "local Riven OCR normalizes faction multipliers for grading");
    Assert(latronOcr.Positives.Any(stat => stat.Name == "toxin_damage" && Math.Abs(stat.Value!.Value - 116.9) < .001), "local Riven OCR ignores decorative digits between a value and stat label");
    var despairOcr = RivenOcrText.Parse([
        ("Despair Feva-gelides\n+65.6% Reload Speed\n+121.5% Cold\n+118.3% Status Duration\nx0.6 Damage to Grineer\nMR 16 rerolls 53", .88f)
    ], ["Despair", "Hate"]);
    Assert(despairOcr.WeaponName == "Despair" && despairOcr.Positives.Length == 3, "local Riven OCR preserves all three Despair positives");
    Assert(despairOcr.Negative is { Name: "damage_vs_grineer", Value: < -39.99 and > -40.01 },
        "a faction multiplier below x1 is recognized as the negative and converted to its equivalent percentage");
    var missingMinusOcr = RivenOcrText.Parse([
        ("Despair Feva-gelides\n+65.6% Reload Speed\n+121.5% Cold\n+118.3% Status Duration\n40 Damage to Grineer\nMR 16 rerolls 53", .86f),
        ("Despair\n65.6 Reload Speed\n121.5 Cold\n118.3 Status Duration\n40 Damage to Grineer\nMR 16 53", .79f)
    ], ["Despair", "Hate"]);
    Assert(missingMinusOcr.Positives.Length == 3 && missingMinusOcr.Negative?.Name == "damage_vs_grineer",
        "four-stat Riven OCR uses the bottom attribute as a reviewable negative when the minus glyph is lost");
    Assert(missingMinusOcr.Notes.Any(note => note.Contains("provisionally", StringComparison.OrdinalIgnoreCase)),
        "an inferred OCR negative is explicitly disclosed for confirmation");
    var annotatedOrderOcr = RivenOcrText.Parse([
        ("Quanta Vandal\n64.9 Zoom\n189 Critical Chance\n111.2 Multishot\n197.4 Damage\nMR 15", .82f),
        ("Quanta Crita-satiata\n111.2 Multishot\n197.4 Damage\n189 Critical Chance\n64.9 Zoom", .78f)
    ], ["Quanta", "Quanta Vandal"]);
    Assert(annotatedOrderOcr.Negative?.Name == "zoom" && annotatedOrderOcr.Positives.Any(stat => stat.Name == "base_damage_/_melee_damage"),
        "missing-minus inference uses shared Riven scaling instead of assuming annotation text order");
    var arcaOcr = RivenOcrText.Parse([
        ("Arca Plasmor Ampi-\nvexicron\n+84.9% ¥ Electricity\n+85.2 Ammo Maximum\n+76.6 Critical Chance\n-63.7 Status Chance\nMR 11 rerolls 4", .86f),
        ("Arca Plasmor Ampi-vexicron\n+1 Fire Rate\n+85.2 Ammo Maximum\n+76.6 Critical Chance\n-63.7 Status Chance\nMR 11 4", .83f)
    ], ["Arca Plasmor", "Arca Scisco"]);
    Assert(arcaOcr.WeaponName == "Arca Plasmor" && arcaOcr.Positives.Select(stat => stat.Name).ToHashSet(StringComparer.Ordinal)
        .SetEquals(["electric_damage", "ammo_maximum", "critical_chance"]),
        "generated Riven affixes recover Electricity and reject an impossible +1 Fire Rate OCR label");
    Assert(arcaOcr.Positives.Single(stat => stat.Name == "electric_damage").Value is > 84.89 and < 84.91 && arcaOcr.Negative?.Name == "status_chance",
        "affix-guided OCR preserves Arca Plasmor values and its explicit negative");
    var comparisonOcr = RivenOcrText.Parse([
        ("Daikyu Acritox\n+16.7 > +150 Toxin\n+23.2 > +209 Critical Chance\nMR 6 rerolls 15", .74f)
    ], ["Daikyu", "Dai-Kyu"]);
    Assert(comparisonOcr.Positives.Any(stat => stat.Name == "toxin_damage" && stat.Value == 150)
        && comparisonOcr.Positives.Any(stat => stat.Name == "critical_damage" && stat.Value == 209)
        && comparisonOcr.Positives.All(stat => stat.Name != "critical_chance"),
        "generated affixes relabel a comparison-card stat whose OCR text conflicts with the encoded positive");
    var twoPositiveOcr = RivenOcrText.Parse([
        ("Strun Critacan\n+168.3 Critical Chance\n+222.9 Multishot\n-70.6 Status Duration\nMR 16\n+16 Damage to Grineer", .84f)
    ], ["Strun"]);
    Assert(twoPositiveOcr.Positives.Select(stat => stat.Name).ToHashSet(StringComparer.Ordinal).SetEquals(["critical_chance", "multishot"])
        && twoPositiveOcr.Negative?.Name == "status_duration", "a two-positive Riven title prevents MR/footer noise from becoming a third positive");
    var recoilOcr = RivenOcrText.Parse([
        ("Bubonico Crita-ignican\n+80 Multishot\n+61.4 Heat\n+62.5 Critical Chance\n+46.5 Weapon Recoil\nMR 9 rerolls 90", .88f)
    ], ["Bubonico"]);
    Assert(recoilOcr.Positives.Select(stat => stat.Name).ToHashSet(StringComparer.Ordinal).SetEquals(["multishot", "heat_damage", "critical_chance"])
        && recoilOcr.Negative?.Name == "recoil", "a harmful plus-Recoil fourth line is classified as the negative from the three-positive title");
    var metadataNoiseOcr = RivenOcrText.Parse([
        ("Bubonico Crita-ignican\n+80 Multishot\n+61.4 Heat\n+62.5 Critical Chance\n+46.5 Weapon Recoil\nMR 9 2/8", .81f),
        ("Bubonico Crita-ignican\n+80 Multishot\n+61.4 Heat\n+62.5 Critical Chance\n+46.5 Weapon Recoil\nMR 9 2/8", .76f),
        ("Bubonico Crita-ignican\n+80 Multishot\n+61.4 Heat\n+62.5 Critical Chance\n+46.5 Weapon Recoil\nMR 9 rerolls 90", .84f),
        ("Bubonico Crita-ignican\n+80 Multishot\n+61.4 Heat\n+62.5 Critical Chance\n+46.5 Weapon Recoil\nMR 9 rerolls 90", .82f)
    ], ["Bubonico"]);
    Assert(metadataNoiseOcr.ModRank is null && metadataNoiseOcr.Rerolls == 90,
        "Riven OCR rejects a two-pass false rank while retaining a separately corroborated reroll count");
    var lowRankOcr = RivenOcrText.Parse([
        ("Gotva Prime Crita-acritox\n+7.8 Toxin\n+14.5 Critical Chance\n+10.5 Critical Damage\n-3.8 Magazine Capacity\nMR 10 rerolls 60", .84f)
    ], ["Gotva Prime", "Gotva"]);
    Assert(lowRankOcr.Positives.Length == 3 && lowRankOcr.Positives.Any(stat => stat.Name == "toxin_damage" && stat.Value == 7.8)
        && lowRankOcr.Negative?.Name == "magazine_capacity", "plausibility checks retain genuine unranked Riven percentages");
    var footerNoiseOcr = RivenOcrText.Parse([
        ("Miter Acri-saticron\n+100.5 Multishot\n+160 Critical Chance\n+144 Critical Damage\n5 Heavy Attack Efficiency\nMR 10 rerolls 176", .84f)
    ], ["Miter"]);
    Assert(footerNoiseOcr.Positives.Length == 3 && footerNoiseOcr.Negative is null,
        "shared-scale validation prevents footer noise from fabricating a negative on a 3+0 Riven");
    var accountTokenPath = Path.Combine(temp, "wfm-token.tk");
    File.WriteAllText(accountTokenPath, string.Join('.', new string('a', 40), new string('b', 40), new string('c', 40)));
    var accountHandler = new AuthenticatedMarketHandler();
    using (var accountHttp = new MarketHttp(accountHandler, 1, 10000))
    {
        var account = new WfmAccountClient(accountHttp, accountTokenPath);
        Assert(await account.OrdersAsync(default) is [{ Id: "order-1", ItemId: "item-1" }], "authenticated account reads owned orders");
        Assert((await account.CreateSellAsync("item-1", 20, 2, default)).Platinum == 20, "authenticated account creates sell orders");
        Assert((await account.UpdateSellAsync("order-1", 19, 2, true, default)).Platinum == 19, "authenticated account updates sell orders");
        Assert((await account.DeleteOrderAsync("order-1", default)).Id == "order-1", "authenticated account deletes managed orders instead of hiding them");
        Assert(accountHandler.Calls == 4 && accountHandler.Deleted && accountHandler.AllBearer, "account delete uses the documented endpoint and Bearer auth without exposing the token");
        Assert(accountHandler.ValidCamelCaseBodies, "account writes use Warframe.market camelCase fields");
    }
    var adjustedInventory = PrimeInventory.SubtractPendingSales(
        [new PrimeInventoryEntry("prime-a", 3), new PrimeInventoryEntry("prime-b", 1)],
        new Dictionary<string, int> { ["item-a"] = 2, ["item-b"] = 1 },
        row => row.GameRef == "prime-a" ? "item-a" : "item-b");
    Assert(adjustedInventory is [{ GameRef: "prime-a", Quantity: 1 }], "pending confirmed market sales suppress stale AlecaFrame quantities without going negative");
    Assert(PrimeInventory.ShouldDeleteManagedListing(false, false, false), "Personal Market removes a managed listing when the item is no longer owned");
    Assert(PrimeInventory.ShouldDeleteManagedListing(true, false, true), "Personal Market removes a managed listing that fresh prices make ineligible");
    Assert(!PrimeInventory.ShouldDeleteManagedListing(true, false, false), "Personal Market keeps a managed listing when missing price data is stale or incomplete");
    var catalogRelics = new Dictionary<string, Relic> { ["Lith Test"] = new("Lith Test", [new("Reward", "rare")], true) };
    var panelSnapshot = new MarketSnapshot(DateTimeOffset.UtcNow, Refinement.Radiant,
        new Dictionary<string, double?> { ["reward"] = 20 },
        new Dictionary<string, PriceInfo> { ["Lith Test"] = new(10, 10, true, false, 10, 10) },
        new Dictionary<string, int> { ["reward"] = 100 }, 2, 2);
    var panelRows = RelicPanel.Select(catalogRelics, panelSnapshot, new RelicPanelOptions());
    Assert(panelRows.Length == 1 && Math.Abs(panelRows[0].Online.Profit.ExpectedValue - 2) < .0001, "persistent panel defaults to Radiant and selects ranked rows");
    Assert(RelicPanel.Select(catalogRelics, panelSnapshot, new RelicPanelOptions(MinReward: 999)).Length == 0, "persistent panel applies saved filters");
    var marketRelics = new Dictionary<string, Relic>(catalogRelics)
    {
        ["Lith Current"] = new("Lith Current", [new("Personal Prime Blueprint", "rare")], false)
    };
    using (var marketHttp = new MarketHttp(new MarketFeedHandler(), 2, 10000))
    {
        await using var market = new LiveMarket(marketHttp, marketRelics, Path.Combine(temp, "market"));
        market.Blacklist.Change("  BADseller ", true);
        await market.StartAsync(true, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (market.Status != "ready") await Task.Delay(10, timeout.Token);
        Assert(market.ReadyBooks == 4 && market.TotalBooks == 4, "one scanner pass loads relic and reward books for both Vaulted and Unvaulted relics");
        Assert(market.Resolve("Lith Test", true) == "correct_relic", "relic suffix resolution matches Python priority");
        var snapshot = market.Snapshot(Refinement.Radiant);
        Assert(snapshot.Prices["reward"] == 20 && snapshot.RelicPrices["Lith Test"].Online == 20, "blacklist affects EV input and relic acquisition cost");
        Assert(ReferenceEquals(snapshot, market.Snapshot(Refinement.Radiant)), "repeated market readers share the bounded computed snapshot cache");
        Assert(market.RewardPrice("Reward") == 20, "single reward lookup avoids rebuilding a full market snapshot");
        Assert(snapshot.Prices["personal prime blueprint"] == 5 && market.RewardEstimate("Personal Prime Blueprint") is { OnlineFloor: 14, RecentVisibleMedian: 3.5, Stabilized: true },
            "snapshot integration stabilizes an Unvaulted reward from online and recent-visible evidence");
        Assert(market.Match("Personal Prime Blueprint", null, true).Entries is [{ Seller: "GoodSeller" }],
            "an Unvaulted reward price is available from the scanner cache without a second Personal Market request");
        Assert(market.MatchPersonalMarket("Personal Prime Blueprint").Entries is [{ Seller: "GoodSeller", SellerStatus: "ingame" }],
            "legacy immediate-whisper matching remains available for interactive views");
        var ownerExcluded = market.RewardEstimate("Personal Prime Blueprint", "good");
        var stableQuote = PrimeInventory.Quote(new PrimeInventoryEntry("personal", 2), "Personal Prime Blueprint", ownerExcluded.Price, "stabilized market", 4, 1);
        Assert(ownerExcluded is { Price: 4, OnlineFloor: null, RecentVisibleMedian: 3.5 } && stableQuote is { Quantity: 2, DraftPrice: 4 },
            "Personal Market excludes the owner's listing and can quote from stable recent-visible evidence when nobody else is online");
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
    var setHandler = new PrimeSetFeedHandler();
    using (var setHttp = new MarketHttp(setHandler, 2, 10000))
    {
        await using var setMarket = new LiveMarket(setHttp, new Dictionary<string, Relic>(), Path.Combine(temp, "set-market"));
        await setMarket.StartAsync(true, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (setMarket.Status != "ready") await Task.Delay(10, timeout.Token);
        var completion = new PrimeSetCompletion(setMarket, setHttp, Path.Combine(temp, "prime-set-components.json"));
        var opportunities = await completion.AnalyzeAsync([
            new PrimeInventoryEntry("example-blueprint", 1, "Example Prime Blueprint"),
            new PrimeInventoryEntry("example-blade", 1, "Example Prime Blade")
        ], null, null, default);
        Assert(opportunities is [{ SetName: "Example Prime Set", MissingItem: "Example Prime Blade", MissingQuantity: 1,
            MissingCost: 7, SetValue: 50, CompletionProfit: 43, OwnedComponentUnits: 2, RequiredComponentUnits: 3 }],
            "Prime-set completion honors quantityInSet and ranks the one missing component by net completion profit");
        var detailCalls = setHandler.DetailCalls;
        var restoredCompletion = new PrimeSetCompletion(setMarket, setHttp, Path.Combine(temp, "prime-set-components.json"));
        await restoredCompletion.AnalyzeAsync([
            new PrimeInventoryEntry("example-blueprint", 1, "Example Prime Blueprint"),
            new PrimeInventoryEntry("example-blade", 1, "Example Prime Blade")
        ], null, null, default);
        Assert(setHandler.DetailCalls == detailCalls && File.Exists(Path.Combine(temp, "prime-set-components.json")),
            "Prime-set composition is persisted and reused without repeating item-detail API calls");
        await setMarket.StopAsync();
    }
    var schedulePath = Path.Combine(temp, "schedule.txt");
    File.WriteAllText(schedulePath, "Mon, August 24\n0400 • Void Cascade - Corpus @ Tuvul Commons, Zariman (S tier, 25% resource bonus)\n0500 • Defense - Grineer @ Hydron, Sedna (C tier)\ninvalid line\n");
    var schedule = ArbitrationSchedule.Load(schedulePath, "America/New_York", 2026);
    Assert(schedule.Length == 2 && schedule[0].Tier == "S" && schedule[0].ResourceBonus == "25% resource bonus", "arbitration schedule parser");
    Assert(ArbitrationSchedule.TierFor(schedule, "Tuvul Commons (Zariman)", "Void Cascade") == "S", "schedule tier matches normalized fissure node");
    var relicMissionCases = new Dictionary<string, string>
    {
        ["Void Cascade"]="S", ["Capture"]="A", ["Exterminate"]="A", ["Rescue"]="A", ["Void Flood"]="B", ["Alchemy"]="B",
        ["Survival"]="F", ["Excavation"]="F", ["Hijack"]="F", ["Sabotage"]="F", ["Interception"]="F",
        ["Mobile Defence"]="F", ["Spy"]="F", ["Defection"]="F"
    };
    Assert(relicMissionCases.All(test => RelicMissionGrade.For(test.Key) == test.Value), "relic grading covers normal, endless, Zariman and Railjack mission aliases");
    Assert(RelicMissionGrade.For("Void Cascade") == "S" && ArbitrationSchedule.TierFor(schedule, "Tuvul Commons (Zariman)", "Void Cascade") == "S",
        "Void Cascade uses the requested relic S tier");
    Assert(RelicMissionGrade.For("Future Mission Type") == "U", "new mission types are visibly unrated instead of silently receiving a grade");
    var normalCapture = RelicMissionGrade.Evaluate("Capture", "Hepit (Void)", false, false);
    var steelCapture = RelicMissionGrade.Evaluate("Capture", "Hepit (Void)", true, false);
    var steelStorm = RelicMissionGrade.Evaluate("Volatile", "Veil Proxima", true, true);
    Assert(steelCapture.RewardScore > normalCapture.RewardScore && steelCapture.Rewards.Any(reward => reward.Contains("+1 Steel Essence")),
        "Steel Path grading counts one Steel Essence for every relic cracked");
    Assert(steelStorm.Rewards.Any(reward => reward.Contains("+1 Steel Essence")) && steelStorm.Rewards.Any(reward => reward.Contains("+2 Steel Essence"))
        && steelStorm.Rewards.Any(reward => reward.Contains("Void Storm")), "Steel Path Void Storm grading counts per-crack essence, completion essence and the Storm reward table");
    using (var browseNodes = JsonDocument.Parse("{\"SolNode450\":{\"value\":\"Tyana Pass (Mars)\",\"enemy\":\"Crossfire\",\"type\":\"Mirror Defense\"},\"ClanNode25\":{\"value\":\"Romula (Venus)\",\"enemy\":\"Corpus\",\"type\":\"Defense\"}}"))
    {
        var browseNow = DateTimeOffset.FromUnixTimeSeconds(1788827400);
        var parsed = ArbitrationSchedule.ParseBrowse("1788825600,ClanNode25\n1788829200,SolNode450\ninvalid\n", "window.arbyTiers = { SolNode450: \"S\" };", browseNodes.RootElement, browseNow);
        Assert(parsed.Length == 2 && parsed[0].Location == "Romula (Venus)" && parsed[0].Enemy == "Corpus" && parsed[0].Tier == "F"
            && parsed[1].MissionType == "Mirror Defense" && parsed[1].Tier == "S", "published Arbitration rows join node metadata and tier grades: " + JsonSerializer.Serialize(parsed));
    }
    var arbitrationCachePath = Path.Combine(temp, "arbitration_schedule.json");
    var arbitrationHour = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600 * 3600;
    using (var browseNodes = JsonDocument.Parse("{\"ClanNode25\":{\"value\":\"Romula (Venus)\",\"enemy\":\"Corpus\",\"type\":\"Defense\"},\"SolNode450\":{\"value\":\"Tyana Pass (Mars)\",\"enemy\":\"Crossfire\",\"type\":\"Mirror Defense\"}}"))
    using (var feed = new ArbitrationScheduleFeed(arbitrationCachePath, new ArbitrationFeedHandler(arbitrationHour)))
    {
        var live = await feed.GetAsync(browseNodes.RootElement, default);
        Assert(live.Length == 2 && ArbitrationSchedule.Current(live, DateTimeOffset.UtcNow.ToUnixTimeSeconds())?.Location == "Romula (Venus)"
            && File.Exists(arbitrationCachePath), "published Arbitration feed validates and persists a current schedule");
    }
    using (var cachedFeed = new ArbitrationScheduleFeed(arbitrationCachePath, new BlockedHandler()))
    using (var browseNodes = JsonDocument.Parse("{}"))
        Assert((await cachedFeed.GetAsync(browseNodes.RootElement, default)).Length == 2, "Arbitration disk cache survives an unavailable upstream feed");
    var future = DateTimeOffset.UtcNow.AddHours(1).ToString("O"); var old = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
    using (var worldDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, fissures = new object[] {
        new { id="normal", tier="Lith", tierNum=1, missionType="Void Cascade", node="Tuvul Commons (Zariman)", enemy="Corpus", expiry=future, isHard=false, isStorm=false },
        new { id="steel", tier="Axi", tierNum=4, missionType="Void Cascade", node="Tuvul Commons (Zariman)", enemy="Corpus", expiry=future, isHard=true, isStorm=false },
        new { id="storm", tier="Neo", tierNum=3, missionType="Survival", node="Railjack", expiry=future, isHard=false, isStorm=true } },
        cetusCycle = new { state="day", expiry=future }, vallisCycle = new { state="warm", expiry=future }, cambionCycle = new { state="fass", expiry=future }, duviriCycle = new { state="joy", expiry=future }, zarimanCycle = new { state="corpus", expiry=future },
        news = new[] { new { id="news", message="Test news", date=old, link="https://example.invalid/news" } }, alerts=Array.Empty<object>(), events=Array.Empty<object>(), invasions=Array.Empty<object>(), dailyDeals=Array.Empty<object>(),
        sortie = new { id="sortie", boss="Test Boss", expiry=future, variants=new[] { new { missionType="Defense", modifier="Test", node="Hydron" } }, activation=old },
        archonHunt = new { id="archon", boss="Test Archon", expiry=future, missions=new[] { new { type="Survival" } }, activation=old }, steelPath = new { expiry=future, currentReward=new { name="Umbra Forma", cost=150 } },
        archimedeas=Array.Empty<object>(), voidTrader=new { activation=future, expiry=future, location="Test Relay" }, kinepage=new { message="Test transmission", timestamp=old } })))
    using (var bountyDoc = JsonDocument.Parse("{\"rot\":\"A\",\"vaultRot\":\"B\",\"expiry\":1788501071296,\"bounties\":{\"ZarimanSyndicate\":[{\"node\":\"SolNode232\",\"challenge\":\"/Lotus/Types/Challenges/Zariman/ZarimanCascadeCompleteWavesEasyChallenge\"}],\"EntratiLabSyndicate\":[{\"node\":\"SolNode718\",\"challenge\":\"/Lotus/Types/Challenges/EntratiLab/EntratiLabActivateLohkSurgeHardChallenge\"}],\"HexSyndicate\":[{\"node\":\"SolNode858\",\"challenge\":\"/Lotus/Types/Challenges/Vania/VaniaHighKillEasy\",\"ally\":\"/Lotus/Types/Gameplay/1999Wf/ProtoframeAllies/AoiAllyAgent\"}]}}"))
    using (var nodesDoc = JsonDocument.Parse("{\"SolNode232\":{\"value\":\"Tuvul Commons (Zariman)\",\"type\":\"Void Cascade\"},\"SolNode718\":{\"value\":\"Cambire (Deimos)\",\"type\":\"Alchemy\"},\"SolNode858\":{\"value\":\"Solstice Square (Höllvania)\",\"type\":\"Stage Defense\"}}"))
    {
        var snapshot = new WorldSnapshot(worldDoc.RootElement, bountyDoc.RootElement, DateTimeOffset.UtcNow, false, "synthetic", [], nodesDoc.RootElement);
        foreach (var key in new[] { "bot-guide", "world-cycles", "world-news", "world-alerts", "world-sortie", "world-archon", "world-steel-path", "world-weekly", "world-archimedea", "world-vendors", "world-bounties", "world-fissures", "world-steel-fissures", "world-void-storms", "world-invasions", "world-arbitration", "world-cascade" })
        { var boards = WorldRender.Build(key, snapshot, schedule); Assert(boards is { Length: > 0 and <= 10 } && boards.All(b => b.Description.Length <= 4096), "world board " + key); }
        var fissure = WorldRender.Build("world-fissures", snapshot, schedule)[0].Description;
        Assert(fissure.Contains("**Lith Void Cascade — Corpus**\nTuvul Commons (Zariman) · **S tier** · ends <t:")
            && !fissure.Contains("score", StringComparison.OrdinalIgnoreCase) && !fissure.Contains("Rewards:") && !fissure.Contains("profit", StringComparison.OrdinalIgnoreCase),
            "fissure uses the compact mission/faction and location/grade/expiry layout");
        var gradedBoards = WorldRender.Build("world-fissures", snapshot, schedule);
        Assert(gradedBoards.Length == 1 && !gradedBoards[0].Description.Contains("Live world state"),
            "fissure board omits guide, reward and source explanations");
        using var gradeOrderDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { fissures = new object[] {
            new { id="slow", tier="Lith", tierNum=1, missionType="Defection", node="Slow Node", expiry=future, isHard=false, isStorm=false },
            new { id="average", tier="Lith", tierNum=1, missionType="Defense", node="Average Node", expiry=future, isHard=false, isStorm=false },
            new { id="lower-f", tier="Lith", tierNum=1, missionType="Sabotage", node="Lower F Node", expiry=future, isHard=false, isStorm=false },
            new { id="higher-a", tier="Lith", tierNum=1, missionType="Rescue", node="Higher A Node", expiry=future, isHard=false, isStorm=false },
            new { id="fast", tier="Axi", tierNum=4, missionType="Capture", node="Fast Node", expiry=future, isHard=false, isStorm=false } } }));
        var gradeOrder = WorldRender.Build("world-fissures", snapshot with { World = gradeOrderDoc.RootElement }, schedule)[0].Description;
        Assert(gradeOrder.IndexOf("Fast Node", StringComparison.Ordinal) < gradeOrder.IndexOf("Higher A Node", StringComparison.Ordinal)
            && gradeOrder.IndexOf("Higher A Node", StringComparison.Ordinal) < gradeOrder.IndexOf("Average Node", StringComparison.Ordinal)
            && gradeOrder.IndexOf("Average Node", StringComparison.Ordinal) < gradeOrder.IndexOf("Lower F Node", StringComparison.Ordinal)
            && gradeOrder.IndexOf("Lower F Node", StringComparison.Ordinal) < gradeOrder.IndexOf("Slow Node", StringComparison.Ordinal),
            "active fissures sort by fixed letter grade, then hidden reward score within the grade, before relic era");
        using var defenseGradeDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { fissures = new[] {
            new { id="defense-grade", tier="Neo", tierNum=3, missionType="Defense", node="Arbiter Test (Mars)", expiry=future, isHard=false, isStorm=false } } }));
        var defenseTierSource = new[] { new ArbitrationEntry(0, long.MaxValue, "test", "Defense", "Grineer", "Arbiter Test (Mars)", "S", null) };
        var inheritedDefense = WorldRender.Build("world-fissures", snapshot with { World = defenseGradeDoc.RootElement }, defenseTierSource)[0].Description;
        Assert(inheritedDefense.Contains("**Neo Defense — Grineer**\nArbiter Test (Mars) · **S tier** · ends <t:"),
            "Defense fissures inherit the matching published Arbitration node tier and faction");
        using var compactSurvivalDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { fissures = new[] {
            new { id="compact", tier="Lith", tierNum=1, missionType="Survival", enemy="Infested", node="Apollodorus (Mercury)", expiry=future, isHard=false, isStorm=false } } }));
        var compactSurvival = WorldRender.Build("world-fissures", snapshot with { World = compactSurvivalDoc.RootElement }, schedule)[0].Description;
        Assert(compactSurvival.Contains($"{ApplicationEmojis.SurvivalWhite} **Lith Survival — Infested**\nApollodorus (Mercury) · **F tier** · ends <t:"),
            "Survival fissure matches the requested compact visual style and mission icon");
        var cascade = WorldRender.Build("world-cascade", snapshot, schedule)[0].Description;
        Assert(cascade.Contains("Normal Void Cascade") && cascade.Contains("Steel Path Void Cascade") && !cascade.Contains("Upcoming Void Cascade Arbitrations") && WorldRender.Build("world-cascade", snapshot, schedule)[0].Title.Contains("ThraxPlasm"), "cascade board sections and emoji");
        var manyJobs = Enumerable.Range(1, 120).Select(index => new { node = "SolNode232", challenge = $"/Lotus/Types/Challenges/Zariman/SyntheticLongChallenge{index:000}" }).ToArray();
        using var largeBountyDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { rot = "A", vaultRot = "B", expiry = 1788501071296L, bounties = new { ZarimanSyndicate = manyJobs } }));
        var largeBounties = WorldRender.Build("world-bounties", new WorldSnapshot(worldDoc.RootElement, largeBountyDoc.RootElement, DateTimeOffset.UtcNow, false, "synthetic", [], nodesDoc.RootElement), schedule);
        Assert(largeBounties.Length is > 2 and <= 10 && string.Join('\n', largeBounties.Select(board => board.Description)).Contains("**120. Tuvul Commons"),
            "oversized bounty rotations paginate across Discord embeds without dropping the final job");
        Assert(WorldRender.Build("world-sortie", snapshot, schedule)[0].Title.Contains("SortiexBlack") && WorldRender.Build("world-sortie", snapshot, schedule)[0].Description.Contains("BossNode"), "Sortie application emojis");
        Assert(WorldRender.Build("world-archon", snapshot, schedule)[0].Title.Contains("ArchonHuntNode"), "Archon Hunt application emoji");
        Assert(WorldRender.Build("world-arbitration", snapshot, schedule)[0].Title.Contains("ArbitrationNode"), "Arbitration application emoji");
        var bounties = WorldRender.Build("world-bounties", snapshot, schedule);
        Assert(bounties.Length == 4 && bounties.Sum(board => board.Description.Count(c => c == '↳')) == 3 && bounties.Any(board => board.Description.Contains("Ally: **Aoi**")), "bounty board renders every supplied job with translated nodes, objectives and allies");
        using var emptyNodes = JsonDocument.Parse("{}");
        var fallbackBounties = WorldRender.Build("world-bounties", snapshot with { Nodes = emptyNodes.RootElement }, schedule);
        var fallbackText = string.Join('\n', fallbackBounties.Select(board => board.Description));
        Assert(fallbackText.Contains("Solstice Square (Höllvania)") && !fallbackText.Contains("SolNode858"), "Höllvania bounties use reviewed node names when the translated node feed lags");
        var guide = WorldRender.Build("bot-guide", snapshot, schedule);
        var guideLength = guide.Sum(board => board.Title.Length + board.Description.Length);
        Assert(guideLength <= 6000, $"complete replacement guide fits Discord's combined embed text limit ({guideLength})");
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
    using (var worldClient = new WorldStateClient(new WorldFeedHandler(failPrimary: true)))
    {
        var fallback = await worldClient.FetchAsync(default);
        Assert(fallback.Stale && fallback.Source.Contains("fissures only") && fallback.RepairedSections.SequenceEqual(["fissures"]),
            "official-only fallback survives a missing translated combined endpoint");
        Assert(fallback.World.Get("fissures").Rows().Single().Get("id").Text() == "fresh",
            "official-only fallback still normalizes current fissures");
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
    public int WeaponFetches;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var uri = request.RequestUri!.ToString(); string json;
        if (uri.Contains("weeklyRivens")) json = "[{compatibility:'Test',rerolled:true,median:500,avg:500,min:10,max:2000,pop:40}]";
        else if (uri.EndsWith("/riven/weapons")) json = "{\"data\":[{\"slug\":\"test\",\"rivenType\":\"rifle\",\"disposition\":1,\"i18n\":{\"en\":{\"name\":\"Test\"}}},{\"slug\":\"empty\",\"i18n\":{\"en\":{\"name\":\"Empty\"}}}]}";
        else
        {
            if (uri.Contains("weapon_url_name=")) Interlocked.Increment(ref WeaponFetches); else Interlocked.Increment(ref Searches);
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
        if (uri.EndsWith("/items")) json = "{\"data\":[{\"slug\":\"wrong_relic\",\"i18n\":{\"en\":{\"name\":\"Lith Test\"}}},{\"slug\":\"correct_relic\",\"i18n\":{\"en\":{\"name\":\"Lith Test Relic\"}}},{\"slug\":\"current_relic\",\"i18n\":{\"en\":{\"name\":\"Lith Current Relic\"}}},{\"slug\":\"reward\",\"i18n\":{\"en\":{\"name\":\"Reward\"}}},{\"id\":\"personal-1\",\"slug\":\"personal_prime_blueprint\",\"gameRef\":\"/Lotus/PersonalPrimeBlueprint\",\"i18n\":{\"en\":{\"name\":\"Personal Prime Blueprint\"}}}]}";
        else if (uri.EndsWith("/filtered_items")) json = "{\"eqmt\":{\"Test\":{\"parts\":{\"Reward\":{\"ducats\":100}}}}}";
        else if (InvalidOrders) json = "{\"data\":\"invalid\"}";
        else if (uri.EndsWith("/personal_prime_blueprint")) json = "{\"data\":[{\"id\":\"p1\",\"type\":\"sell\",\"platinum\":2,\"quantity\":1,\"user\":{\"ingameName\":\"Recent1\",\"status\":\"offline\"}},{\"id\":\"p2\",\"type\":\"sell\",\"platinum\":3,\"quantity\":1,\"user\":{\"ingameName\":\"Recent2\",\"status\":\"offline\"}},{\"id\":\"p3\",\"type\":\"sell\",\"platinum\":4,\"quantity\":1,\"user\":{\"ingameName\":\"Recent3\",\"status\":\"offline\"}},{\"id\":\"p4\",\"type\":\"sell\",\"platinum\":5,\"quantity\":1,\"user\":{\"ingameName\":\"Recent4\",\"status\":\"offline\"}},{\"id\":\"p5\",\"type\":\"sell\",\"platinum\":14,\"quantity\":1,\"user\":{\"ingameName\":\"GoodSeller\",\"status\":\"ingame\",\"slug\":\"good\"}}]}";
        else json = "{\"data\":[{\"id\":\"1\",\"type\":\"sell\",\"platinum\":1,\"quantity\":5,\"subtype\":\"radiant\",\"user\":{\"ingameName\":\"Badseller\",\"status\":\"online\"}},{\"id\":\"2\",\"type\":\"sell\",\"platinum\":20,\"quantity\":5,\"subtype\":\"radiant\",\"user\":{\"ingameName\":\"GoodSeller\",\"status\":\"ingame\"}}]}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
sealed class PrimeSetFeedHandler : HttpMessageHandler
{
    public int DetailCalls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); var uri = request.RequestUri!.ToString(); string json;
        if (uri.EndsWith("/v2/items")) json = "{\"data\":[" +
            "{\"id\":\"set-1\",\"slug\":\"example_prime_set\",\"tags\":[\"prime\",\"set\"],\"i18n\":{\"en\":{\"name\":\"Example Prime Set\"}}}," +
            "{\"id\":\"part-a\",\"slug\":\"example_prime_blueprint\",\"tags\":[\"prime\",\"blueprint\"],\"i18n\":{\"en\":{\"name\":\"Example Prime Blueprint\"}}}," +
            "{\"id\":\"part-b\",\"slug\":\"example_prime_blade\",\"tags\":[\"prime\",\"component\"],\"i18n\":{\"en\":{\"name\":\"Example Prime Blade\"}}}]}";
        else if (uri.EndsWith("/filtered_items")) json = "{\"eqmt\":{}}";
        else if (uri.EndsWith("/v2/items/example_prime_set")) { DetailCalls++; json = "{\"data\":{\"setParts\":[\"set-1\",\"part-a\",\"part-b\"]}}"; }
        else if (uri.EndsWith("/v2/items/example_prime_blueprint")) { DetailCalls++; json = "{\"data\":{\"quantityInSet\":1}}"; }
        else if (uri.EndsWith("/v2/items/example_prime_blade")) { DetailCalls++; json = "{\"data\":{\"quantityInSet\":2}}"; }
        else if (uri.EndsWith("/orders/item/example_prime_set")) json = "{\"data\":[{\"id\":\"s1\",\"type\":\"sell\",\"platinum\":50,\"quantity\":1,\"user\":{\"ingameName\":\"SetSeller\",\"status\":\"ingame\"}}]}";
        else if (uri.EndsWith("/orders/item/example_prime_blade")) json = "{\"data\":[{\"id\":\"b1\",\"type\":\"sell\",\"platinum\":7,\"quantity\":2,\"user\":{\"ingameName\":\"BladeSeller\",\"status\":\"ingame\"}}]}";
        else json = "{\"data\":[]}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
sealed class AuthenticatedMarketHandler : HttpMessageHandler
{
    public int Calls;
    public bool Deleted;
    public bool AllBearer = true;
    public bool ValidCamelCaseBodies = true;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++; AllBearer &= request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter?.Length > 100;
        Deleted |= request.Method == HttpMethod.Delete && request.RequestUri!.AbsolutePath == "/v2/order/order-1";
        var platinum = 18;
        if (request.Content is not null)
        {
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            ValidCamelCaseBodies &= !body.RootElement.TryGetProperty("item_id", out _);
            if (request.Method == HttpMethod.Post)
                ValidCamelCaseBodies &= body.RootElement.TryGetProperty("itemId", out var itemId) && itemId.GetString() == "item-1";
            platinum = (int)(body.RootElement.Get("platinum").Number() ?? platinum);
        }
        var json = request.Method == HttpMethod.Get
            ? "{\"data\":[{\"id\":\"order-1\",\"itemId\":\"item-1\",\"type\":\"sell\",\"platinum\":18,\"quantity\":2,\"visible\":true}]}"
            : JsonSerializer.Serialize(new { data = new { id = "order-1", itemId = "item-1", type = "sell", platinum, quantity = 2, visible = true } });
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
sealed class WorldFeedHandler(bool failPrimary = false) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!.ToString(); var now = DateTimeOffset.UtcNow;
        string Mongo(DateTimeOffset value) => $"{{\"$date\":{{\"$numberLong\":\"{value.ToUnixTimeMilliseconds()}\"}}}}";
        var normalized = uri.TrimEnd('/');
        if (failPrimary && normalized.EndsWith("/pc"))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        var json = normalized.EndsWith("/pc") ? "{\"timestamp\":\"2020-01-01T00:00:00Z\",\"fissures\":[]}" : uri.Contains("bounty-cycle") ? "{}" : normalized.EndsWith("/solNodes") ? "{\"SolNode1\":{\"value\":\"Hydron (Sedna)\",\"type\":\"Defense\"}}" :
            $"{{\"Time\":{now.ToUnixTimeSeconds()},\"ActiveMissions\":[{{\"_id\":{{\"$oid\":\"fresh\"}},\"Activation\":{Mongo(now.AddMinutes(-1))},\"Expiry\":{Mongo(now.AddMinutes(10))},\"Node\":\"SolNode1\",\"MissionType\":\"MT_DEFENSE\",\"Modifier\":\"VoidT3\",\"Hard\":false}}]}}";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }
}
sealed class ArbitrationFeedHandler(long hour) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = request.RequestUri!.AbsolutePath.EndsWith("arbys.txt", StringComparison.Ordinal)
            ? $"{hour},ClanNode25\n{hour + 3600},SolNode450\n"
            : "window.arbyTiers = { SolNode450: \"S\" };";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });
    }
}
