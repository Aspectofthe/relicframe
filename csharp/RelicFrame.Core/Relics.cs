using Microsoft.VisualBasic.FileIO;

namespace RelicFrame.Core;

public enum Refinement { Intact, Exceptional, Flawless, Radiant }
public sealed record Reward(string RewardName, string Rarity);
public sealed record Profit(bool PriceKnown, double TotalCost, double ExpectedValue, double ExpectedProfit,
    double? ExpectedRoiPct, double WorstCaseValue, double WorstCaseProfit, double? WorstCaseRoiPct, bool AlwaysProfitable)
{
    public string Category => !PriceKnown ? "white" : WorstCaseProfit > 0 ? "green" : ExpectedProfit > 0 ? "yellow" : "red";
    public int Risk(double? loss = null, bool matched = true, bool outlier = false, bool hasListing = true)
    {
        if (!PriceKnown) return 100;
        var score = loss.HasValue ? Math.Min(40, loss.Value * .4) : 0;
        if (WorstCaseProfit < 0 && TotalCost > 0) score += Math.Min(1, Math.Abs(WorstCaseProfit) / TotalCost) * 25;
        return (int)Math.Round(Math.Min(100, score + (matched ? 0 : 15) + (outlier ? 10 : 0) + (hasListing ? 0 : 10)));
    }
}
public sealed record Odds(string Name, double Price, string Rarity, double ChancePct, double? ExpectedOpenings, double ChanceWithin10);
public sealed record DucatEfficiency(bool PriceKnown, double TotalCost, double ExpectedDucats, double? PlatPerDucat, double? DucatsPerPlat);
public sealed record BatchAnalysis(int N, bool PriceKnown, double TotalCost, double ExpectedRevenue, double ExpectedProfit,
    double WorstCaseRevenue, double WorstCaseProfit, double ProbProfitPct, double ProbLossPct, double ProbBreakevenPct, bool Approx);
public sealed record RefinementComparison(string Tier, int TraceCost, double ExpectedValue, double ExpectedProfit,
    double? ExpectedRoiPct, double WorstCaseProfit, double ChanceOfProfitPct, double ChanceOfLossPct,
    double RareSlotChancePct, double? IncrementalEvPerTrace, bool PriceKnown);

public sealed record Relic(string RelicName, IReadOnlyList<Reward> Rewards, bool? Vaulted = null)
{
    public static readonly int[] TraceCosts = [0, 25, 50, 100];
    // Preserve the rounded slot probabilities of the Python engine, including its 99.99/100.01 totals.
    private static readonly double[][] Chances = [[25.33, 11, 2], [23.33, 13, 4], [20, 17, 6], [16.67, 20, 10]];
    public static double Chance(Refinement tier, string rarity) => rarity.ToLowerInvariant() switch
    {
        "common" => Chances[(int)tier][0], "uncommon" => Chances[(int)tier][1], "rare" => Chances[(int)tier][2], _ => 0
    };
    public static double AtLeastOne(double chancePct, int n) => n <= 0 ? 0 : (1 - Math.Pow(1 - Math.Clamp(chancePct / 100, 0, 1), n)) * 100;
    public static int RarityDisplayOrder(string rarity) => rarity.Trim().ToLowerInvariant() switch
    {
        "rare" => 0, "uncommon" => 1, "common" => 2, _ => 3
    };
    public IReadOnlyList<Reward> DisplayRewards(Func<Reward, double?> price) => Rewards
        .OrderBy(reward => RarityDisplayOrder(reward.Rarity))
        .ThenByDescending(reward => price(reward) ?? double.NegativeInfinity)
        .ThenBy(reward => reward.RewardName, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    private static double Price(IReadOnlyDictionary<string, double?> prices, Reward r) => prices.GetValueOrDefault(r.RewardName.Trim().ToLowerInvariant()) ?? 0;
    public double ExpectedValue(Refinement tier, IReadOnlyDictionary<string, double?> prices) => Rewards.Sum(r => Chance(tier, r.Rarity) / 100 * Price(prices, r));
    public double WorstCaseValue(IReadOnlyDictionary<string, double?> prices) => Rewards.Count == 0 ? 0 : Rewards.Min(r => Price(prices, r));
    public Odds? BestRewardOdds(Refinement tier, IReadOnlyDictionary<string, double?> prices) => TopRewards(tier, prices, 1).FirstOrDefault();
    public IReadOnlyList<Odds> TopRewards(Refinement tier, IReadOnlyDictionary<string, double?> prices, int n = 3) => Rewards.Select(r =>
    {
        var chance = Chance(tier, r.Rarity);
        return new Odds(r.RewardName, Price(prices, r), r.Rarity, chance, chance > 0 ? 100 / chance : null, AtLeastOne(chance, 10));
    }).OrderByDescending(r => r.Price).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, n)).ToArray();
    public double ExpectedDucats(Refinement tier, IReadOnlyDictionary<string, int> ducats) => Rewards.Sum(r => Chance(tier, r.Rarity) / 100 * ducats.GetValueOrDefault(r.RewardName.Trim().ToLowerInvariant()));
    public DucatEfficiency DucatEfficiency(Refinement tier, IReadOnlyDictionary<string, int> ducats, double? cost, double traceRate = 0)
    {
        var total = (cost ?? 0) + TraceCosts[(int)tier] * traceRate;
        var expected = ExpectedDucats(tier, ducats);
        var ratiosKnown = cost.HasValue && total > 0 && expected > 0;
        return new(cost.HasValue, total, expected, ratiosKnown ? total / expected : null, ratiosKnown ? expected / total : null);
    }
    public double? PlatPerTrace(Refinement tier, IReadOnlyDictionary<string, double?> prices) => TraceCosts[(int)tier] == 0 ? null : ExpectedValue(tier, prices) / TraceCosts[(int)tier];
    public Profit Profitability(Refinement tier, IReadOnlyDictionary<string, double?> prices, double? cost, double traceRate = 0)
    {
        var total = (cost ?? 0) + TraceCosts[(int)tier] * traceRate;
        var ev = ExpectedValue(tier, prices); var worst = WorstCaseValue(prices);
        return new(cost.HasValue, total, ev, ev - total, total > 0 ? (ev - total) / total * 100 : null,
            worst, worst - total, total > 0 ? (worst - total) / total * 100 : null, cost.HasValue && worst > total);
    }
    public BatchAnalysis BuyN(Refinement tier, IReadOnlyDictionary<string, double?> prices, double? cost, double traceRate,
        int n, double? totalRelicCost = null, int exactThreshold = 120, int trials = 20000, CancellationToken ct = default)
    {
        if (n < 0 || trials < 1) throw new ArgumentOutOfRangeException(nameof(n));
        var total = (totalRelicCost ?? (cost ?? 0) * n) + TraceCosts[(int)tier] * traceRate * n;
        var dist = Rewards.Select(r => (Value: Price(prices, r), P: Chance(tier, r.Rarity) / 100)).Where(r => r.P > 0).ToArray();
        double win = 0, loss = 0, even = 0; bool approx = false;
        if (dist.Length > 0 && n > 0)
        {
            if (n <= exactThreshold)
            {
                var cur = new Dictionary<double, double> { [0] = 1 };
                for (var i = 0; i < n; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var next = new Dictionary<double, double>();
                    foreach (var (value, p) in cur)
                    {
                        foreach (var reward in dist)
                        {
                            var key = Math.Round(value + reward.Value, 4);
                            next[key] = next.GetValueOrDefault(key) + p * reward.P;
                            if (next.Count > 250_000) throw new InvalidOperationException("Exact distribution exceeds the memory budget; use Monte Carlo mode.");
                        }
                    }
                    // Fail explicitly rather than exhausting the host with a combinatorial distribution.
                    cur = next;
                }
                win = cur.Where(r => r.Key > total).Sum(r => r.Value) * 100;
                loss = cur.Where(r => r.Key < total).Sum(r => r.Value) * 100;
                even = cur.Where(r => Math.Abs(r.Key - total) < 1e-6).Sum(r => r.Value) * 100;
            }
            else
            {
                approx = true;
                var random = new Random(1234567); var weight = dist.Sum(r => r.P);
                for (var trial = 0; trial < trials; trial++)
                {
                    ct.ThrowIfCancellationRequested(); double sum = 0;
                    for (var i = 0; i < n; i++)
                    {
                        var roll = random.NextDouble() * weight;
                        foreach (var reward in dist) { roll -= reward.P; if (roll <= 0) { sum += reward.Value; break; } }
                    }
                    if (sum > total) win++; else if (sum < total) loss++; else even++;
                }
                win = win / trials * 100; loss = loss / trials * 100; even = even / trials * 100;
            }
        }
        var expected = ExpectedValue(tier, prices) * n; var worst = WorstCaseValue(prices) * n;
        return new(n, totalRelicCost.HasValue || cost.HasValue, total, expected, expected - total, worst, worst - total, win, loss, even, approx);
    }
    public IReadOnlyList<RefinementComparison> Compare(IReadOnlyDictionary<string, double?> prices, double? cost, double traceRate = 0)
    {
        var rows = new List<RefinementComparison>(); double? lastEv = null; int lastTraces = 0;
        foreach (var tier in Enum.GetValues<Refinement>())
        {
            var p = Profitability(tier, prices, cost, traceRate); var traces = TraceCosts[(int)tier];
            var win = Rewards.Where(r => Price(prices, r) > p.TotalCost).Sum(r => Chance(tier, r.Rarity));
            var loss = Rewards.Where(r => Price(prices, r) < p.TotalCost).Sum(r => Chance(tier, r.Rarity));
            rows.Add(new(tier.ToString().ToLowerInvariant(), traces, p.ExpectedValue, p.ExpectedProfit, p.ExpectedRoiPct,
                p.WorstCaseProfit, win, loss, Chance(tier, "rare"), lastEv.HasValue ? (p.ExpectedValue - lastEv) / (traces - lastTraces) : null, p.PriceKnown));
            lastEv = p.ExpectedValue; lastTraces = traces;
        }
        return rows;
    }
    public static IReadOnlyDictionary<string, Relic> LoadCsv(string path, bool includeRequiem = true)
    {
        using var reader = new TextFieldParser(path, System.Text.Encoding.UTF8) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        reader.SetDelimiters(",");
        var headers = reader.ReadFields() ?? throw new InvalidDataException("Missing CSV header");
        int Column(string name) => Array.FindIndex(headers, h => h.TrimStart('\uFEFF') == name);
        var ni = Column("relic_name"); var ri = Column("reward_name"); var qi = Column("rarity"); var vi = Column("vaulted");
        if (ni < 0 || ri < 0 || qi < 0) throw new InvalidDataException("Missing relic_name, reward_name or rarity column");
        var result = new Dictionary<string, Relic>(StringComparer.Ordinal);
        while (!reader.EndOfData)
        {
            var row = reader.ReadFields(); if (row is null) continue;
            var name = row[ni].Trim(); var reward = new Reward(row[ri].Trim(), row[qi].Trim().ToLowerInvariant());
            // Exclude the whole relic, not individual reward slots: otherwise its
            // expected value and drop probabilities would describe an incomplete pool.
            if (!includeRequiem && (name.Equals("Requiem", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Requiem ", StringComparison.OrdinalIgnoreCase))) continue;
            if (!result.TryGetValue(name, out var relic))
            {
                bool? vault = (vi >= 0 && vi < row.Length ? row[vi].Trim().ToLowerInvariant() : "") switch
                { "true" or "1" or "yes" => true, "false" or "0" or "no" => false, _ => null };
                result[name] = relic = new(name, new List<Reward>(), vault);
            }
            ((List<Reward>)relic.Rewards).Add(reward);
        }
        return result;
    }
}
