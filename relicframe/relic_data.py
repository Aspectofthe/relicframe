"""
relic_data.py
The relic model, the standard drop-chance table, and all profitability math.
Nothing in here touches the network - see wfm_api.py for that.
"""

import csv
import random
from dataclasses import dataclass, field

# Standard DE drop-chance table (% per single reward slot), by refinement.
# Every relic has 3 "common" slots, 2 "uncommon" slots, and 1 "rare" slot;
# this gives the chance for each slot individually. Matches the table:
#   Quality       Traces  Common          Uncommon        Rare
#   Intact        0       76% (25.33%)    22% (11%)       2%
#   Exceptional   25      70% (23.33%)    26% (13%)       4%
#   Flawless      50      60% (20%)       34% (17%)       6%
#   Radiant       100     50% (16.67%)    40% (20%)       10%
RARITY_CHANCE = {
    "intact":      {"common": 25.33, "uncommon": 11.0, "rare": 2.0},
    "exceptional": {"common": 23.33, "uncommon": 13.0, "rare": 4.0},
    "flawless":    {"common": 20.0,  "uncommon": 17.0, "rare": 6.0},
    "radiant":     {"common": 16.67, "uncommon": 20.0, "rare": 10.0},
}

REFINEMENTS = list(RARITY_CHANCE.keys())

# Void Traces needed to refine one relic up to each tier (cumulative cost).
TRACE_COST = {
    "intact": 0,
    "exceptional": 25,
    "flawless": 50,
    "radiant": 100,
}


@dataclass
class RelicReward:
    reward_name: str
    rarity: str  # common / uncommon / rare


@dataclass
class Relic:
    relic_name: str
    rewards: list[RelicReward] = field(default_factory=list)
    vaulted: bool | None = None  # None = unknown, NOT the same as False (unvaulted)

    def expected_value(self, refinement: str, prices: dict[str, float | None]) -> float:
        """
        Expected plat value of opening one of this relic at the given
        refinement, using current prices. Missing prices are treated as 0
        so they don't wrongly inflate the estimate.
        """
        chances = RARITY_CHANCE[refinement]
        total = 0.0
        for reward in self.rewards:
            chance_pct = chances.get(reward.rarity, 0.0)
            price = prices.get(reward.reward_name.strip().lower(), 0.0) or 0.0
            total += (chance_pct / 100.0) * price
        return total

    def worst_case_value(self, prices: dict[str, float | None]) -> float:
        """
        The guaranteed floor: the cheapest single reward this relic can
        possibly give you. Every open yields exactly one reward from the
        pool, so this is what you're guaranteed no matter how unlucky you
        get - the number that answers "is this still profitable worst case".
        """
        prices_found = [
            prices.get(r.reward_name.strip().lower(), 0.0) or 0.0
            for r in self.rewards
        ]
        return min(prices_found) if prices_found else 0.0

    def best_reward_odds(self, refinement: str, prices: dict[str, float | None]) -> dict:
        """
        Finds the single most valuable reward in this relic (by current
        price, whatever rarity it happens to be - a common item can
        sometimes be worth more than the rare one) and the odds of pulling
        it at the given refinement.

        Returns {"name", "price", "rarity", "chance_pct", "expected_openings",
        "chance_within_10"}.

        expected_openings = 100 / chance_pct - the AVERAGE number of relics
        you'd need to crack to see this reward once. This is not a
        guarantee: it does not mean the Nth opening is guaranteed to hit,
        just that it's the long-run average. chance_within_10 is the
        probability of seeing it at least once within 10 opens
        (1 - (1-p)^10), which is usually a more useful number for planning
        a real farming session than the raw average.
        """
        chances = RARITY_CHANCE[refinement]
        best = None
        for reward in self.rewards:
            price = prices.get(reward.reward_name.strip().lower(), 0.0) or 0.0
            if best is None or price > best["price"]:
                chance_pct = chances.get(reward.rarity, 0.0)
                p = chance_pct / 100.0
                best = {
                    "name": reward.reward_name,
                    "price": price,
                    "rarity": reward.rarity,
                    "chance_pct": chance_pct,
                    "expected_openings": (1.0 / p) if p > 0 else None,
                    "chance_within_10": (1 - (1 - p) ** 10) * 100 if p > 0 else 0.0,
                }
        return best

    def top_rewards(self, refinement: str, prices: dict[str, float | None], n: int = 3) -> list[dict]:
        """
        The n most valuable rewards in this relic (not just the single
        best), each with its own price and chance - so a relic with
        several good outcomes doesn't look like a one-trick relic.
        Returns a list of {"name", "price", "rarity", "chance_pct"}, sorted
        by price descending.
        """
        chances = RARITY_CHANCE[refinement]
        rows = []
        for reward in self.rewards:
            price = prices.get(reward.reward_name.strip().lower(), 0.0) or 0.0
            rows.append({
                "name": reward.reward_name,
                "price": price,
                "rarity": reward.rarity,
                "chance_pct": chances.get(reward.rarity, 0.0),
            })
        rows.sort(key=lambda r: r["price"], reverse=True)
        return rows[:n]

    def expected_ducats(self, refinement: str, ducats: dict[str, int]) -> float:
        """
        Expected ducat value of opening one of this relic at the given
        refinement - same weighted-sum shape as expected_value(), just
        against the ducat map instead of the plat price map. Missing
        ducat data (item has no ducat value, or wasn't matched) counts
        as 0, same "unknown is never free/positive" rule as prices.
        """
        chances = RARITY_CHANCE[refinement]
        total = 0.0
        for reward in self.rewards:
            chance_pct = chances.get(reward.rarity, 0.0)
            d = ducats.get(reward.reward_name.strip().lower(), 0) or 0
            total += (chance_pct / 100.0) * d
        return total

    def best_ducat_reward(self, refinement: str, ducats: dict[str, int]) -> dict | None:
        """
        The single highest-ducat-value reward in this relic's pool, mirroring
        best_reward_odds() but for ducats instead of plat. Returns
        {"name", "ducats", "rarity", "chance_pct"}, or None if this relic
        has no reward with known ducat value at all.
        """
        chances = RARITY_CHANCE[refinement]
        best = None
        for reward in self.rewards:
            d = ducats.get(reward.reward_name.strip().lower(), 0) or 0
            if d <= 0:
                continue
            if best is None or d > best["ducats"]:
                best = {
                    "name": reward.reward_name,
                    "ducats": d,
                    "rarity": reward.rarity,
                    "chance_pct": chances.get(reward.rarity, 0.0),
                }
        return best

    def ducat_efficiency(
        self,
        refinement: str,
        ducats: dict[str, int],
        relic_cost: float | None,
        plat_per_trace: float = 0.0,
    ) -> dict:
        """
        Answers "is this relic a good ducat farm" - not the same question
        as plat profitability, and deliberately kept separate (review item
        #13: this is a distinct, lower-priority objective, not something
        that should distort the core plat-profit ranking).

        Returns {
            "price_known", "total_cost", "expected_ducats",
            "plat_per_ducat", "ducats_per_plat",
        }
        plat_per_ducat is the cost basis to farm one expected ducat - LOWER
        is better (cheaper ducats). ducats_per_plat is its inverse - HIGHER
        is better - kept alongside since UIs sorting "best X first" read
        more naturally off an ascending-is-worse metric than a
        descending-is-worse one. Both are None when cost is unknown, cost
        is 0 (nothing spent - the ratio is undefined, not infinite), or
        expected_ducats is 0 (nothing to divide into).
        """
        price_known = relic_cost is not None
        cost_for_math = relic_cost if price_known else 0.0
        total_cost = cost_for_math + TRACE_COST[refinement] * plat_per_trace
        expected_ducats = self.expected_ducats(refinement, ducats)

        plat_per_ducat = None
        ducats_per_plat = None
        if price_known and total_cost > 0 and expected_ducats > 0:
            plat_per_ducat = total_cost / expected_ducats
            ducats_per_plat = expected_ducats / total_cost

        return {
            "price_known": price_known,
            "total_cost": total_cost,
            "expected_ducats": expected_ducats,
            "plat_per_ducat": plat_per_ducat,
            "ducats_per_plat": ducats_per_plat,
        }

    def plat_per_trace(self, refinement: str, prices: dict[str, float | None]) -> float | None:
        """
        Expected plat value per Void Trace spent refining TO this tier
        (from Intact). None at Intact itself (0 traces spent, no ratio).
        """
        traces = TRACE_COST[refinement]
        if traces == 0:
            return None
        return self.expected_value(refinement, prices) / traces

    def marginal_trace_value(
        self, from_refinement: str, to_refinement: str, prices: dict[str, float | None]
    ) -> dict:
        """
        Answers "should I actually spend the traces to push this relic from
        one tier to another" - not just "what's Radiant worth," but what do
        the EXTRA traces buy you over where you already are.

        Returns {"ev_gain", "trace_cost", "value_per_trace"}. value_per_trace
        is None if trace_cost is 0 (nothing spent, nothing to divide by).
        """
        ev_from = self.expected_value(from_refinement, prices)
        ev_to = self.expected_value(to_refinement, prices)
        trace_cost = TRACE_COST[to_refinement] - TRACE_COST[from_refinement]
        ev_gain = ev_to - ev_from
        return {
            "ev_gain": ev_gain,
            "trace_cost": trace_cost,
            "value_per_trace": (ev_gain / trace_cost) if trace_cost > 0 else None,
        }

    def _single_open_distribution(self, refinement: str, prices: dict[str, float | None]) -> list[tuple[float, float]]:
        """The outcome distribution of ONE relic open: [(reward_value, probability), ...]."""
        chances = RARITY_CHANCE[refinement]
        dist = []
        for reward in self.rewards:
            price = prices.get(reward.reward_name.strip().lower(), 0.0) or 0.0
            p = chances.get(reward.rarity, 0.0) / 100.0
            if p > 0:
                dist.append((price, p))
        return dist

    def buy_n_analysis(
        self,
        refinement: str,
        prices: dict[str, float | None],
        relic_cost: float | None,
        plat_per_trace: float,
        n: int,
        exact_threshold: int = 120,
        mc_trials: int = 20000,
        total_relic_cost_override: float | None = None,
    ) -> dict:
        """
        Answers "if I buy N of this relic, what actually happens" - not
        just N times the single-relic numbers, but the real probability
        distribution across N independent opens. A relic can have positive
        expected value from one rare, high-value slot while still being a
        coin flip (or worse) on any given batch of opens - this is the
        calculation that tells you that.

        For n <= exact_threshold, computes the EXACT outcome distribution
        via dynamic-programming convolution (fast in practice: under 2s up
        to ~150 opens, since each relic only has up to 6 possible outcomes
        per draw so distinct running totals stay manageable). For larger n,
        falls back to Monte Carlo simulation (mc_trials random draws) -
        NOT a normal/CLT approximation, which was tested and found to be
        wildly inaccurate here (over 20 percentage points off at n=15)
        because relic payouts are heavily skewed: mostly cheap commons
        plus one rare, disproportionately large payout, which converges to
        a normal distribution far too slowly for CLT to be trustworthy at
        realistic n. approx=True flags when Monte Carlo (not exact DP) was
        used - treat those probabilities as a good estimate, not exact.

        total_relic_cost_override: pass the REAL total cost of buying N
        relics (e.g. from cheapest_combination_cost(), which sweeps
        multiple sellers' order books instead of assuming one seller has
        unlimited stock at the single cheapest price) to use that instead
        of relic_cost * n. This matters because relic_cost * n silently
        assumes N units are all purchasable at that one listing's price,
        which is routinely false once N exceeds any single seller's
        quantity - the review's "Best Available Deal" scenario. Trace
        cost is still added per-relic on top of this override, since
        traces aren't bought from the same order book at all.

        Returns: {
            "n", "price_known", "total_cost",
            "expected_revenue", "expected_profit",
            "worst_case_revenue", "worst_case_profit",
            "prob_profit_pct", "prob_loss_pct", "prob_breakeven_pct",
            "approx",
        }
        """
        trace_cost_total = TRACE_COST[refinement] * plat_per_trace * n
        if total_relic_cost_override is not None:
            price_known = True
            total_cost = total_relic_cost_override + trace_cost_total
        else:
            price_known = relic_cost is not None
            cost_per_relic = (relic_cost if price_known else 0.0) + TRACE_COST[refinement] * plat_per_trace
            total_cost = cost_per_relic * n

        ev_per = self.expected_value(refinement, prices)
        worst_per = self.worst_case_value(prices)
        expected_revenue = ev_per * n
        worst_case_revenue = worst_per * n

        dist = self._single_open_distribution(refinement, prices)

        if not dist or n <= 0:
            prob_profit = prob_loss = prob_breakeven = 0.0
            approx = False
        elif n <= exact_threshold:
            cur = {0.0: 1.0}
            for _ in range(n):
                nxt: dict[float, float] = {}
                for total, prob in cur.items():
                    for value, p in dist:
                        key = round(total + value, 4)
                        nxt[key] = nxt.get(key, 0.0) + prob * p
                cur = nxt
            prob_profit = sum(p for v, p in cur.items() if v > total_cost) * 100
            prob_loss = sum(p for v, p in cur.items() if v < total_cost) * 100
            prob_breakeven = sum(p for v, p in cur.items() if abs(v - total_cost) < 1e-6) * 100
            approx = False
        else:
            values = [v for v, p in dist]
            weights = [p for v, p in dist]
            profit_count = 0
            loss_count = 0
            breakeven_count = 0
            rng = random.Random(1234567)  # fixed seed - repeatable results for the same inputs
            for _ in range(mc_trials):
                total = sum(rng.choices(values, weights=weights, k=n))
                if total > total_cost:
                    profit_count += 1
                elif total < total_cost:
                    loss_count += 1
                else:
                    breakeven_count += 1
            prob_profit = profit_count / mc_trials * 100
            prob_loss = loss_count / mc_trials * 100
            prob_breakeven = breakeven_count / mc_trials * 100
            approx = True

        return {
            "n": n,
            "price_known": price_known,
            "total_cost": total_cost,
            "expected_revenue": expected_revenue,
            "expected_profit": expected_revenue - total_cost,
            "worst_case_revenue": worst_case_revenue,
            "worst_case_profit": worst_case_revenue - total_cost,
            "prob_profit_pct": prob_profit,
            "prob_loss_pct": prob_loss,
            "prob_breakeven_pct": prob_breakeven,
            "approx": approx,
        }

    def refinement_comparison(
        self,
        prices: dict[str, float | None],
        relic_cost: float | None,
        plat_per_trace: float = 0.0,
    ) -> list[dict]:
        """
        Review item #6: a full Intact -> Exceptional -> Flawless -> Radiant
        comparison, not just a single selected tier. Holds the relic's own
        purchase cost fixed across all four rows - the question this
        answers is "I already have (or am about to buy) this relic, how
        far is it actually worth refining," not "which tier's market
        listing is cheapest" (that's a separate question the main table
        already answers per-refinement via the price fetch).

        Each row is a profitability() result (same engine, not a
        duplicated formula) for that tier, plus:
            "tier", "trace_cost" (traces needed, cumulative from Intact),
            "chance_of_profit_pct", "chance_of_loss_pct" (from the real
                single-open distribution, not a guess),
            "rare_slot_chance_pct" (the rare-slot drop rate at this tier -
                refining doesn't just change EV, it shifts probability mass
                toward the rare slot specifically, which is worth showing
                on its own),
            "incremental_ev_per_trace" (EV gained per ADDITIONAL trace
                spent vs. the previous row - None for Intact, since there's
                no previous tier to compare against). This is the number
                that actually answers "is refining further worth it,"
                since a tier can have a great total EV while still being a
                bad trace investment if most of that EV was already
                present at a cheaper tier.
        """
        rows = []
        prev_ev = None
        prev_traces = None
        for tier in REFINEMENTS:
            prof = self.profitability(tier, prices, relic_cost, plat_per_trace)
            dist = self._single_open_distribution(tier, prices)
            total_cost = prof["total_cost"]
            if dist:
                chance_of_profit = sum(p for v, p in dist if v > total_cost) * 100
                chance_of_loss = sum(p for v, p in dist if v < total_cost) * 100
            else:
                chance_of_profit = chance_of_loss = 0.0

            traces = TRACE_COST[tier]
            incremental = None
            if prev_ev is not None and traces > prev_traces:
                incremental = (prof["expected_value"] - prev_ev) / (traces - prev_traces)

            rows.append({
                "tier": tier,
                "trace_cost": traces,
                "expected_value": prof["expected_value"],
                "expected_profit": prof["expected_profit"],
                "expected_roi_pct": prof["expected_roi_pct"],
                "worst_case_profit": prof["worst_case_profit"],
                "chance_of_profit_pct": chance_of_profit,
                "chance_of_loss_pct": chance_of_loss,
                "rare_slot_chance_pct": RARITY_CHANCE[tier]["rare"],
                "incremental_ev_per_trace": incremental,
                "price_known": prof["price_known"],
            })
            prev_ev = prof["expected_value"]
            prev_traces = traces
        return rows

    def best_refinement(
        self,
        prices: dict[str, float | None],
        relic_cost: float | None,
        plat_per_trace: float = 0.0,
        objective: str = "expected_profit",
    ) -> dict | None:
        """
        "Radiant recommended" or "Flawless recommended" - explicit per the
        review, since Radiant is NOT always optimal (a low-value relic can
        easily lose more in trace cost than refining gains it in EV).

        objective: one of "expected_profit", "expected_roi_pct",
        "worst_case_profit", "chance_of_profit_pct". Picks the tier that
        maximizes that field among refinement_comparison()'s rows. Returns
        None only if the relic somehow has no reward slots at all
        (shouldn't happen with real data, but this stays safe rather than
        raising on malformed input). ROI comparisons only make sense when
        price is known - with an unknown price this still returns a tier
        (so the UI always has something to show) but the caller should
        treat it as illustrative, same as any other unknown-price number.
        """
        rows = self.refinement_comparison(prices, relic_cost, plat_per_trace)
        if not rows:
            return None
        valid_objectives = {
            "expected_profit", "expected_roi_pct", "worst_case_profit", "chance_of_profit_pct",
        }
        if objective not in valid_objectives:
            objective = "expected_profit"

        def sort_key(row):
            v = row[objective]
            return v if v is not None else float("-inf")

        best_row = max(rows, key=sort_key)
        return {"tier": best_row["tier"], "objective": objective, "row": best_row}

    def profitability(
        self,
        refinement: str,
        prices: dict[str, float | None],
        relic_cost: float | None,
        plat_per_trace: float = 0.0,
    ) -> dict:
        """
        Core answer to "is buying this relic actually worth it":

        price_known          False if we don't have a real relic cost (no
                             usable listing). Numbers below still compute
                             with cost=0 so they're viewable, but they must
                             NOT be trusted or ranked against relics with a
                             real price - unknown cost is not the same as
                             free. always_profitable is forced False here.
        total_cost           relic's own market price, plus the plat-
                             equivalent cost of traces needed to reach this
                             refinement tier (0 if you farm traces yourself)
        expected_profit      expected_value - total_cost (average outcome)
        expected_roi_pct     expected_profit / total_cost * 100
        worst_case_profit    worst_case_value - total_cost (guaranteed
                             floor, even if every open gives the cheapest
                             possible reward)
        worst_case_roi_pct   worst_case_profit / total_cost * 100
        always_profitable    True only if price_known AND worst_case_profit
                             > 0 - this relic cannot lose you plat no matter
                             what you pull, and that's actually verified
        """
        price_known = relic_cost is not None
        cost_for_math = relic_cost if price_known else 0.0

        trace_cost_plat = TRACE_COST[refinement] * plat_per_trace
        total_cost = cost_for_math + trace_cost_plat

        ev = self.expected_value(refinement, prices)
        worst = self.worst_case_value(prices)

        expected_profit = ev - total_cost
        worst_case_profit = worst - total_cost

        return {
            "price_known": price_known,
            "total_cost": total_cost,
            "expected_value": ev,
            "expected_profit": expected_profit,
            "expected_roi_pct": (expected_profit / total_cost * 100) if total_cost > 0 else None,
            "worst_case_value": worst,
            "worst_case_profit": worst_case_profit,
            "worst_case_roi_pct": (worst_case_profit / total_cost * 100) if total_cost > 0 else None,
            "always_profitable": price_known and worst_case_profit > 0,
        }


def find_relics_for_reward(
    reward_name: str, relics: dict[str, "Relic"], refinement: str
) -> list[dict]:
    """
    "Which relics actually drop this thing, and how good are my odds at
    this refinement" - the core of Phase-2 item #12, "specific Prime part
    mode". Pure and price-independent on purpose: drop chance doesn't need
    market data at all, so this works even before prices have been fetched,
    and stays trivially testable without mocking the network.

    Matching is substring-based, case-insensitive, in both directions, so
    "ember prime bp" still finds a reward stored as "Ember Prime Blueprint"
    (and vice versa) without requiring an exact match either caller has to
    guess at.

    Returns a list of {\"relic_name\", \"rarity\", \"chance_pct\",
    \"expected_openings\", \"vaulted\"}, one entry per (relic, matching
    reward) pair - a relic can appear more than once if the search term
    somehow matches two of its reward slots, which is correct, not a bug.
    Sorted by chance_pct descending (best odds first); the caller layers
    price/cost-per-expected-copy on top once market data is available.
    """
    needle = reward_name.strip().lower()
    if not needle:
        return []
    chances = RARITY_CHANCE[refinement]
    results = []
    for relic in relics.values():
        for reward in relic.rewards:
            hay = reward.reward_name.strip().lower()
            if needle in hay or hay in needle:
                chance_pct = chances.get(reward.rarity, 0.0)
                results.append({
                    "relic_name": relic.relic_name,
                    "reward_name": reward.reward_name,
                    "rarity": reward.rarity,
                    "chance_pct": chance_pct,
                    "expected_openings": (100.0 / chance_pct) if chance_pct > 0 else None,
                    "vaulted": relic.vaulted,
                })
    results.sort(key=lambda r: r["chance_pct"], reverse=True)
    return results


def cheapest_combination_cost(entries: list[dict], n: int) -> dict:
    """
    Answers "what's the REAL cheapest way to buy N of this" (review item
    #11, "Best Available Deal") - not cheapest-single-listing x N, which
    silently assumes one seller has unlimited stock. Walks the sell book
    in ascending price order, taking as many units as each seller
    actually has before moving on to the next-cheapest seller.

    entries: list of {"price", "quantity", ...} - not required to be
    pre-sorted (sorted internally); extra keys are ignored here.

    An entry with quantity=None (present but unknown) is conservatively
    treated as exactly 1 available, never as unlimited - matching this
    module's "unknown is never treated as free/infinite" rule elsewhere.
    A combination that would need more than 1 unit from an unknown-
    quantity seller correctly comes back as NOT fully filled, rather than
    silently overpromising a total that might not actually be purchasable.

    Returns {
        "total_cost", "units_filled", "fully_filled" (bool - could all N
        units actually be sourced from the given listings at all),
        "avg_price_per_unit" (None if units_filled == 0),
        "orders_used": [{"price", "quantity_used"}, ...] in draw order.
    }
    """
    sorted_entries = sorted(entries, key=lambda e: e["price"])
    total_cost = 0.0
    units_filled = 0
    orders_used: list[dict] = []
    for e in sorted_entries:
        if units_filled >= n:
            break
        raw_qty = e.get("quantity")
        available = 1 if raw_qty is None else max(0, raw_qty)
        take = min(available, n - units_filled)
        if take <= 0:
            continue
        total_cost += take * e["price"]
        units_filled += take
        orders_used.append({"price": e["price"], "quantity_used": take})

    return {
        "total_cost": total_cost,
        "units_filled": units_filled,
        "fully_filled": units_filled >= n,
        "avg_price_per_unit": (total_cost / units_filled) if units_filled > 0 else None,
        "orders_used": orders_used,
    }


def chance_of_at_least_one(chance_pct: float, n_opens: list[int]) -> list[dict]:
    """
    Review item #5: "for N relics, calculate the probability of getting
    at least one specific item" - a small table like:
        Opens   Chance of >= 1
        1       10.0%
        3       27.1%
        6       46.9%
        10      65.1%
        20      87.8%
    rather than just the single-open drop chance or the long-run average
    (expected_openings). This is genuinely different information: someone
    planning "I'm going to crack 6 of these tonight" wants THIS number,
    not the average-case one.

    chance_pct: the per-open percentage chance (e.g. 10.0 for 10%).
    n_opens: which N values to compute the cumulative chance for - any
    list of non-negative ints, not required to be sorted or deduplicated
    (results preserve the caller's order so a UI can list them however
    it wants).

    Formula: 1 - (1-p)^n, the standard "at least one success in n
    independent trials" complement-of-none calculation. chance_pct <= 0
    returns 0.0% for every N (nothing to ever pull); chance_pct >= 100
    returns 100.0% for every N >= 1 and 0.0% for N == 0.

    Returns [{"n", "chance_at_least_one_pct"}, ...].
    """
    p = max(0.0, min(1.0, chance_pct / 100.0))
    results = []
    for n in n_opens:
        if n <= 0:
            chance = 0.0
        else:
            chance = (1 - (1 - p) ** n) * 100
        results.append({"n": n, "chance_at_least_one_pct": chance})
    return results


def compute_risk_score(
    profit: dict,
    prob_loss_pct: float | None = None,
    subtype_matched: bool = True,
    is_outlier: bool = False,
    has_listing: bool = True,
) -> int:
    """
    A 0-100 heuristic risk score, higher = riskier. This is deliberately a
    simple weighted composite, not a rigorously derived statistic - it's
    meant to be a quick "how nervous should I be about this" gut-check
    alongside the actual numbers, not a replacement for them.

    Weights (of 100 max):
      - Unknown price at all: an automatic 100 (nothing to evaluate)
      - Loss probability (buy_n_analysis prob_loss_pct, if given): up to 40
      - Worst-case severity (how much of the total cost you could lose in
        the worst outcome, relative to cost): up to 25
      - No listing at all for this exact refinement tier (subtype fallback
        used): +15
      - Outlier / unconfirmed lone listing: +10
      - Not immediately actionable (no listing at all, e.g. offline-only
        with nothing online): +10
    """
    if not profit["price_known"]:
        return 100

    score = 0.0
    if prob_loss_pct is not None:
        score += min(40.0, prob_loss_pct * 0.4)

    if profit["worst_case_profit"] < 0 and profit["total_cost"] > 0:
        severity = min(1.0, abs(profit["worst_case_profit"]) / profit["total_cost"])
        score += severity * 25

    if not subtype_matched:
        score += 15
    if is_outlier:
        score += 10
    if not has_listing:
        score += 10

    return round(min(100.0, score))


def classify_category(profit: dict) -> str:
    """
    The four-way category from the spec:
        "green"  - Guaranteed Profit: worst-case profit > 0
        "yellow" - Expected Profit Only: expected profit > 0, worst-case <= 0
        "red"    - Guaranteed Loss: expected profit <= 0
        "white"  - Unknown Price: insufficient market data
    Operates on a single profitability() dict (one channel at a time) -
    see combine_categories() to merge online + offline into one badge.
    """
    if not profit["price_known"]:
        return "white"
    if profit["worst_case_profit"] > 0:
        return "green"
    if profit["expected_profit"] > 0:
        return "yellow"
    return "red"


_CATEGORY_RANK = {"green": 3, "yellow": 2, "red": 1, "white": 0}


def combine_categories(a: str, b: str) -> str:
    """
    Best category across two channels (e.g. online + offline) - if EITHER
    channel offers a guaranteed-profit opportunity, that's real and
    actionable, so the combined badge should reflect the best available
    option, not be dragged down by the other channel being unverified.
    """
    return a if _CATEGORY_RANK[a] >= _CATEGORY_RANK[b] else b


def _parse_vaulted(raw: str) -> bool | None:
    """Blank/missing = unknown (None), never silently assumed False."""
    v = raw.strip().lower()
    if v in ("true", "1", "yes"):
        return True
    if v in ("false", "0", "no"):
        return False
    return None


def load_relics_from_csv(path: str) -> dict[str, Relic]:
    """
    Loads a relics CSV (relic_name,reward_name,rarity[,vaulted]) into a
    dict of relic_name -> Relic. Works with relics_template.csv, the output
    of parse_official_drops.py, or fetch_relic_data.py.
    """
    relics: dict[str, Relic] = {}
    with open(path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            name = row["relic_name"].strip()
            reward = RelicReward(
                reward_name=row["reward_name"].strip(),
                rarity=row["rarity"].strip().lower(),
            )
            if name not in relics:
                vaulted = _parse_vaulted(str(row.get("vaulted", "")))
                relics[name] = Relic(relic_name=name, vaulted=vaulted)
            relics[name].rewards.append(reward)
    return relics


def all_reward_names(relics: dict[str, Relic]) -> list[str]:
    names = set()
    for relic in relics.values():
        for r in relic.rewards:
            names.add(r.reward_name.strip())
    return sorted(names)

