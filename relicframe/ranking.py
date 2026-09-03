"""
ranking.py
Turns a rank-mode name into a sort key over compute_row() rows. Extracted
out of main.py for the same reason as relic_row.py: this logic must be
identical for the GUI table and the Discord bot's /top command, or the
two surfaces could disagree about which relic is "#1" for no reason other
than a copy-paste drift between two implementations.
"""

RANK_MODES = [
    "Best Overall", "Guaranteed Profit", "Expected Profit",
    "Best ROI", "Best Plat/Trace", "Cheapest", "Best Ducat Farming",
]


def sort_key_for_mode(mode: str, channel_scope: str = "Both (best of either)"):
    """
    Returns a function(row) -> sortable key, for `rows.sort(key=..., reverse=True)`
    (higher key = better = should sort first).

    channel_scope must match whatever scope the caller's rows were
    computed/filtered with (see relic_row.compute_row's channel_scope
    param) - otherwise a mode like "Guaranteed Profit" could rank using an
    offline number that filtering has already excluded from consideration
    for "Online only", making the #1 result not actually be #1 among what
    was asked to be shown.
    """
    def channel_profits(row):
        if channel_scope == "Online only":
            return [row["online_profit"]]
        if channel_scope == "Offline only":
            return [row["offline_profit"]]
        return [row["online_profit"], row["offline_profit"]]

    def best_of(row, key):
        return max(p[key] for p in channel_profits(row))

    if mode == "Guaranteed Profit":
        return lambda r: (best_of(r, "price_known"), best_of(r, "worst_case_profit"))
    if mode == "Expected Profit":
        return lambda r: (best_of(r, "price_known"), best_of(r, "expected_profit"))
    if mode == "Best ROI":
        def roi_key(r):
            rois = [
                p["expected_roi_pct"] for p in channel_profits(r)
                if p["price_known"] and p["expected_roi_pct"] is not None
            ]
            if not rois:
                return (False, float("-inf"), float("-inf"))
            # Risk-adjusted, not raw ROI% - a single-listing/thin-quantity
            # "70% ROI" flier that vanishes after one purchase shouldn't
            # outrank a well-supported, lower-risk opportunity just
            # because its nominal ROI% happens to be higher. best_risk is
            # already scoped to channel_scope (see relic_row.compute_row),
            # so this stays consistent with the risk number actually
            # shown for this row. Raw ROI stays as the tiebreaker so two
            # equally risky rows still order by ROI.
            best_roi = max(rois)
            risk_factor = 1.0 - (r["best_risk"] / 100.0)
            return (True, best_roi * risk_factor, best_roi)
        return roi_key
    if mode == "Best Plat/Trace":
        return lambda r: (r["plat_per_trace"] is not None, r["plat_per_trace"] or float("-inf"))
    if mode == "Cheapest":
        def cheap_key(r):
            costs = [p["total_cost"] for p in channel_profits(r) if p["price_known"]]
            # Cheapest first -> negate for descending sort, unknowns sort last
            return (bool(costs), -min(costs) if costs else float("-inf"))
        return cheap_key
    if mode == "Best Ducat Farming":
        def ducat_key(r):
            dpp = r["ducat_info"]["ducats_per_plat"]
            return (dpp is not None, dpp if dpp is not None else float("-inf"))
        return ducat_key
    # "Best Overall" (default/fallback too): category first (green beats
    # all), then guaranteed profit, then expected profit.
    cat_rank = {"green": 3, "yellow": 2, "red": 1, "white": 0}
    return lambda r: (
        cat_rank[r["overall_category"]],
        best_of(r, "worst_case_profit"),
        best_of(r, "expected_profit"),
    )

