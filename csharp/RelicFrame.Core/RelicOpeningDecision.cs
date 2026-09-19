namespace RelicFrame.Core;

public sealed record SquadOpeningResult(int Players, double RareChancePct, double BestRewardEv,
    double? NetVersusSelling);

public static class RelicOpeningDecision
{
    public static IReadOnlyList<SquadOpeningResult> Compare(Relic relic, Refinement tier,
        IReadOnlyDictionary<string, double?> rewardPrices, double? relicSellPrice)
    {
        if (relic.Rewards.Count == 0 || relic.Rewards.Any(reward =>
            !rewardPrices.TryGetValue(reward.RewardName, out var price) || price is null or <= 0
            || !double.IsFinite(price.Value))) return [];
        var probabilities = relic.Rewards.Select(reward => new
        {
            Price = rewardPrices[reward.RewardName]!.Value,
            Chance = Relic.Chance(tier, reward.Rarity) / 100.0
        }).Where(row => row.Chance > 0).ToArray();
        var total = probabilities.Sum(row => row.Chance);
        if (total <= 0) return [];
        // A fissure squad exposes one independent reward choice per member. This is
        // the EV of the best visible choice for one player, not total group revenue.
        var levels = probabilities.GroupBy(row => row.Price).OrderBy(group => group.Key).ToArray();
        var results = new List<SquadOpeningResult>();
        for (var players = 1; players <= 4; players++)
        {
            var cumulative = 0.0;
            var previousCdf = 0.0;
            var bestEv = 0.0;
            foreach (var level in levels)
            {
                cumulative += level.Sum(row => row.Chance) / total;
                var cdf = Math.Pow(Math.Min(1, cumulative), players);
                bestEv += level.Key * (cdf - previousCdf);
                previousCdf = cdf;
            }
            var rare = relic.Rewards.Where(reward => reward.Rarity.Equals("rare", StringComparison.OrdinalIgnoreCase))
                .Sum(reward => Relic.Chance(tier, reward.Rarity));
            results.Add(new(players, Relic.AtLeastOne(rare, players), bestEv,
                relicSellPrice is > 0 && double.IsFinite(relicSellPrice.Value) ? bestEv - relicSellPrice.Value : null));
        }
        return results;
    }
}
