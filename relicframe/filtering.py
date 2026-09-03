"""
filtering.py
Turns the same set of filter criteria the GUI's filter row exposes into a
single passes_filters(row, ...) predicate - extracted for the same reason
as relic_row.py and ranking.py: the Discord bot's /top command needs to
filter identically to the GUI table, not via a second hand-copied
implementation that can silently drift from the original.
"""


def passes_filters(
    row: dict,
    vault_filter: str = "All",
    channel_scope: str = "Both (best of either)",
    min_roi: float | None = None,
    max_cost: float | None = None,
    min_reward: float | None = None,
    green_only: bool = False,
) -> bool:
    """
    vault_filter: "All", "Unvaulted", "Vaulted", or "Unknown".
    channel_scope: must match whatever scope row was computed with (see
        relic_row.compute_row) - see that module's docstring for why this
        matters.
    min_roi / max_cost / min_reward: None means "no filter" for that
        field; a relic with no data for a filtered field never silently
        passes just because the number is missing.
    """
    relic = row["relic"]

    if vault_filter == "Unvaulted" and relic.vaulted is not False:
        return False
    if vault_filter == "Vaulted" and relic.vaulted is not True:
        return False
    if vault_filter == "Unknown" and relic.vaulted is not None:
        return False

    if channel_scope == "Online only":
        profits = [row["online_profit"]]
    elif channel_scope == "Offline only":
        profits = [row["offline_profit"]]
    else:
        profits = [row["online_profit"], row["offline_profit"]]

    if min_roi is not None:
        rois = [p["expected_roi_pct"] for p in profits if p["price_known"] and p["expected_roi_pct"] is not None]
        if not rois or max(rois) < min_roi:
            return False

    if max_cost is not None:
        costs = [p["total_cost"] for p in profits if p["price_known"]]
        if not costs or min(costs) > max_cost:
            return False

    if min_reward is not None:
        reward_price = row["odds"].get("price")
        if reward_price is None or reward_price < min_reward:
            return False

    if green_only and row["overall_category"] != "green":
        return False

    return True

