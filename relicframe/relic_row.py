"""
relic_row.py
The ONE calculation path for turning a relic + market data into the full
row of numbers every surface (the Tkinter GUI, and now the Discord bot)
displays. Extracted out of main.py's RelicToolApp so it's no longer tied
to tkinter StringVars - review item #10 ("online and offline should use
ONE calculation engine... instead of duplicated formulas") applies just as
much to "GUI vs bot" as it originally did to "online vs offline". Building
the bot against a second copy of this logic would have been exactly the
anti-pattern the review warned about, just one layer up.

Nothing in here touches tkinter, discord.py, or the network - it's pure
computation over already-fetched data (relic objects, a reward-price
snapshot, per-relic price-detail dicts, a ducat map), so it's directly
unit-testable and safe to call from any context.
"""

from relic_data import Relic, classify_category, combine_categories, compute_risk_score, TRACE_COST


def compute_channel(relic: Relic, cost, qty, refinement: str, trace_rate: float, prices_snapshot: dict):
    """
    ONE shared calculation path for both online and offline (and now for
    every caller of this module). A quantity of exactly 0 excludes the
    listing (nothing purchasable); missing/unknown quantity does not -
    same "unknown is never treated as zero" rule used throughout this
    project.

    Returns (profit_dict, category, zero_qty).
    """
    usable_cost = cost if (cost is not None and (qty is None or qty > 0)) else None
    zero_qty = cost is not None and qty == 0
    profit = relic.profitability(refinement, prices_snapshot, usable_cost, trace_rate)
    category = classify_category(profit)
    return profit, category, zero_qty


def compute_row(
    relic: Relic,
    refinement: str,
    trace_rate: float,
    prices: dict,
    relic_prices: dict,
    ducats: dict,
    channel_scope: str = "Both (best of either)",
) -> dict:
    """
    Builds the full row of numbers for one relic - everything the GUI
    table/details panel and the Discord bot's embeds both read from.

    prices: reward_name (lowercase) -> plat price snapshot (relic_data's
        prices dict shape).
    relic_prices: relic_name -> price-detail dict, as returned by
        wfm_api.get_relic_prices() (online/offline cost, quantity, subtype
        match, outlier flags).
    ducats: reward_name (lowercase) -> ducat value.
    channel_scope: "Both (best of either)", "Online only", or "Offline
        only" - must match whichever value the caller's filtering/ranking
        will also use, so the category badge, risk score, and any
        filtered/ranked view of this row all agree on what "channel" means
        for this row. Passing a mismatched scope between compute_row and a
        caller's own filtering step is how the channel-scope bugs fixed
        earlier in this project happened in the first place.
    """
    price_info = relic_prices.get(relic.relic_name) or {}

    online_cost = price_info.get("online")
    online_qty = price_info.get("online_quantity")
    online_matched = price_info.get("online_subtype_matched", True)
    online_outlier = price_info.get("online_is_outlier", False)
    online_profit, online_cat, online_zero = compute_channel(
        relic, online_cost, online_qty, refinement, trace_rate, prices
    )

    offline_cost = price_info.get("offline_included")
    offline_qty = price_info.get("offline_quantity")
    offline_matched = price_info.get("offline_subtype_matched", True)
    offline_outlier = price_info.get("offline_is_outlier", False)
    offline_profit, offline_cat, offline_zero = compute_channel(
        relic, offline_cost, offline_qty, refinement, trace_rate, prices
    )

    if channel_scope == "Online only":
        overall_category = online_cat
    elif channel_scope == "Offline only":
        overall_category = offline_cat
    else:
        overall_category = combine_categories(online_cat, offline_cat)

    odds = relic.best_reward_odds(refinement, prices) or {
        "name": "", "price": 0.0, "chance_pct": 0.0, "expected_openings": None, "chance_within_10": 0.0
    }
    plat_per_trace = relic.plat_per_trace(refinement, prices)

    # Quantity-aware batch summary (x{qty} expected profit shown in list
    # views, and the batch total shown in detail views) - CHEAP by
    # construction, not a Buy-N simulation. expected_profit/worst_case_profit
    # scale EXACTLY linearly with N (linearity of expectation for the
    # former; the worst single outcome repeated N times for the latter -
    # relic_data.Relic.buy_n_analysis computes these two fields the exact
    # same way, see its own source), so no DP/Monte Carlo is needed to get
    # them right.
    #
    # What genuinely CAN'T be gotten cheaply is a batch-quantity-scaled
    # prob_loss_pct (the true probability of ending up behind across N
    # CORRELATED draws, where N tracks online_qty/offline_qty and can be
    # arbitrarily large) - that stays None here on purpose. A prior version
    # of this function called buy_n_analysis() with that real N for every
    # relic on every list/table build, which is a reasonable thing to WANT,
    # but doing so was the actual root cause of a real production incident:
    # it ran a full DP-or-Monte-Carlo simulation twice per relic (online +
    # offline) for the ENTIRE relic set on every refresh, synchronously,
    # with no await points - blocking the asyncio event loop for 10-30+
    # seconds at a time, which starved Discord's gateway heartbeat and
    # cascaded into forced reconnects and a crash loop. The precise
    # per-relic, per-N probability is still available on demand via the
    # dedicated Buy-N command/calculator for a SINGLE selected relic
    # (cheap - one relic, not the whole set) - it just doesn't belong in
    # the per-relic hot path that runs for every relic, every time.
    #
    # What's cheap regardless of N, and IS now computed here for every
    # relic (see online_n1/offline_n1 below): the probability of profit
    # from a SINGLE open (n=1, hardcoded, independent of quantity) across
    # every reward slot weighted by its real rarity odds - not just
    # whether the rarest ("gold") slot alone would be profitable. The DP
    # loop for n=1 only runs once over <=6 reward slots per relic, so this
    # is microseconds even across the full relic set (benchmarked at
    # ~0.03ms/relic for both channels combined) - nothing like the N-scaled
    # case above.
    def _cheap_batch_summary(cost, qty, single_profit):
        if cost is None:
            return None
        n = qty if isinstance(qty, int) and qty > 0 else 1
        trace_cost_total = TRACE_COST[refinement] * trace_rate * n
        total_cost = cost * n + trace_cost_total
        expected_revenue = single_profit["expected_value"] * n
        worst_case_revenue = single_profit["worst_case_value"] * n
        return {
            "n": n,
            "price_known": single_profit["price_known"],
            "total_cost": total_cost,
            "expected_revenue": expected_revenue,
            "expected_profit": expected_revenue - total_cost,
            "worst_case_revenue": worst_case_revenue,
            "worst_case_profit": worst_case_revenue - total_cost,
            "prob_profit_pct": None,
            "prob_loss_pct": None,
            "prob_breakeven_pct": None,
            "approx": True,  # exact for profit/revenue fields; probability fields intentionally omitted, see above
        }

    online_batch = _cheap_batch_summary(online_cost, online_qty, online_profit)
    # Exact probability that buying and opening ONE of this relic actually
    # turns a profit - weighted across every reward slot by its real
    # rarity odds (relic.expected_value already does this for the AVERAGE
    # outcome; this is the same weighting applied to "what fraction of
    # outcomes clear the cost line" instead). This reuses buy_n_analysis,
    # but hardcoded to n=1 - deliberately NOT online_qty/offline_qty - so
    # it stays cheap regardless of listing size: the DP loop below only
    # runs ONE iteration over this relic's <=6 reward slots (microseconds),
    # unlike a real batch-quantity buy_n_analysis call, which is the
    # expensive case the comment above warns about and this does not
    # reintroduce.
    online_cost_for_n1 = online_cost if online_profit["price_known"] else None
    online_n1 = relic.buy_n_analysis(refinement, prices, online_cost_for_n1, trace_rate, n=1)
    # buy_n_analysis treats an unknown cost as free (0p) for its own math
    # (see its price_known handling), which makes prob_profit_pct come
    # out near 100% when we don't actually know the price at all - not
    # "very likely to profit". Same "unknown is never treated as zero"
    # rule this project uses everywhere else: null these out rather than
    # expose a number computed against a fake price.
    online_prob_profit_pct = online_n1["prob_profit_pct"] if online_profit["price_known"] else None
    online_prob_loss_pct = online_n1["prob_loss_pct"] if online_profit["price_known"] else None
    online_risk = compute_risk_score(
        online_profit, prob_loss_pct=online_prob_loss_pct,
        subtype_matched=online_matched, is_outlier=online_outlier, has_listing=True,
    )
    # Offline: even if a price exists, the seller isn't online, so it's not
    # immediately actionable the way an online listing is.
    offline_batch = _cheap_batch_summary(offline_cost, offline_qty, offline_profit)
    offline_cost_for_n1 = offline_cost if offline_profit["price_known"] else None
    offline_n1 = relic.buy_n_analysis(refinement, prices, offline_cost_for_n1, trace_rate, n=1)
    offline_prob_profit_pct = offline_n1["prob_profit_pct"] if offline_profit["price_known"] else None
    offline_prob_loss_pct = offline_n1["prob_loss_pct"] if offline_profit["price_known"] else None
    offline_risk = compute_risk_score(
        offline_profit, prob_loss_pct=offline_prob_loss_pct,
        subtype_matched=offline_matched, is_outlier=offline_outlier, has_listing=False,
    )
    # Must respect the SAME channel_scope as overall_category above - not
    # just min(online, offline) unconditionally, or a caller scoped to
    # "Online only" could see a Risk score pulled from the offline channel
    # they excluded.
    if channel_scope == "Online only":
        best_risk = online_risk
    elif channel_scope == "Offline only":
        best_risk = offline_risk
    else:
        best_risk = min(online_risk, offline_risk)

    # Ducat efficiency - a distinct, lower-priority objective from plat
    # profitability, kept as its own field. Uses the cheapest ACTIONABLE
    # cost (online first, offline fallback) - never averaged.
    ducat_cost = online_cost if online_cost is not None else offline_cost
    ducat_info = relic.ducat_efficiency(refinement, ducats, ducat_cost, trace_rate)
    best_ducat_reward = relic.best_ducat_reward(refinement, ducats)

    return {
        "relic": relic,
        "online_cost": online_cost, "online_qty": online_qty,
        "online_matched": online_matched, "online_outlier": online_outlier,
        "online_zero": online_zero, "online_profit": online_profit, "online_cat": online_cat,
        "online_risk": online_risk, "online_prob_profit_pct": online_prob_profit_pct,
        "online_prob_loss_pct": online_prob_loss_pct,
        "offline_cost": offline_cost, "offline_qty": offline_qty,
        "offline_matched": offline_matched, "offline_outlier": offline_outlier,
        "offline_zero": offline_zero, "offline_profit": offline_profit, "offline_cat": offline_cat,
        "offline_risk": offline_risk, "offline_prob_profit_pct": offline_prob_profit_pct,
        "offline_prob_loss_pct": offline_prob_loss_pct,
        "online_batch": online_batch,
        "offline_batch": offline_batch,
        "best_risk": best_risk,
        "overall_category": overall_category,
        "odds": odds,
        "plat_per_trace": plat_per_trace,
        "prices_snapshot": prices,
        "ducat_info": ducat_info,
        "best_ducat_reward": best_ducat_reward,
    }

