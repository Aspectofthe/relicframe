"""Generate deterministic synthetic migration fixtures. Never loads tokens/private exports.

Python is a development oracle only; the C# executable does not invoke it.
Run from repository root: relicframe/.venv/Scripts/python.exe csharp/generate_parity.py
"""
import json
import random
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "relicframe"))
from relic_data import Relic, RelicReward, cheapest_combination_cost
from order_math import matching_entries
from riven_roll_rules import parse_rule_expression, parse_negative_expression, evaluate_curated_roll
from companion_vision import rarity_from_colors
from dataclasses import asdict
from riven_market import find_riven_deals, calculate_riven_stat_ranges, auction_roll_quality, parse_weekly, WeeklyPrice, auction_price
from relic_row import compute_row
from ranking import RANK_MODES, sort_key_for_mode
from filtering import passes_filters
from riven_trade_chat import RivenTradeChatLog, price_summary
from datetime import datetime, UTC
import tempfile

rng = random.Random(89213)
cases = []
for i in range(180):
    rewards = [RelicReward(f"Reward {j}", rarity) for j, rarity in enumerate(["common"] * 3 + ["uncommon"] * 2 + ["rare"])]
    relic = Relic(f"Lith Test{i}", rewards, [True, False, None][i % 3])
    prices = {r.reward_name.lower(): rng.choice([None, 0, 1, 5, 17, 35, 90, 120.5]) for r in rewards}
    cost = rng.choice([None, 0, 3, 20, 75]); rate = rng.choice([0, .03, .2]); tier = rng.choice(["intact", "exceptional", "flawless", "radiant"])
    n = rng.randrange(0, 7)
    base = {"relic": asdict(relic), "prices": prices, "cost": cost, "rate": rate, "tier": tier, "n": n}
    cases.extend([
        {"kind": "profit", **base, "expected": relic.profitability(tier, prices, cost, rate)},
        {"kind": "batch", **base, "expected": relic.buy_n_analysis(tier, prices, cost, rate, n)},
        {"kind": "compare", **base, "expected": relic.refinement_comparison(prices, cost, rate)},
    ])
for i in range(100):
    orders = [{"platinum": rng.choice([None, 0, 1, 20, 1000]), "perTrade": rng.choice([None, 0, 1, 6]),
               "quantity": rng.choice([None, 0, 1, 3, 9]), "subtype": rng.choice([None, "intact", "radiant"])} for _ in range(i % 18)]
    tier = rng.choice([None, "intact", "radiant", "missing"])
    entries, matched = matching_entries(orders, tier)
    cases.append({"kind": "orders", "orders": orders, "tier": tier, "expected": {"entries": entries, "subtype_matched": matched}})
    cases.append({"kind": "purchase", "entries": entries, "n": i % 20, "expected": cheapest_combination_cost(entries, i % 20)})
expressions = ["CD MS/TOX/DMG/FR/CC/PT", "CC MS FR/CD/DMG/HEAT", "CD AS RANGE", "CC/CD/MS", "CD ELEMENT", "CD MS or CC DMG"]
stats = ["critical_damage", "critical_chance", "multishot", "cold_damage", "toxin_damage", "ammo_maximum", "range"]
for expr in expressions:
    rule = {"alternatives": parse_rule_expression(expr), "harmless_negatives": parse_negative_expression("ZOOM/AMMO")}
    cases.append({"kind": "rule_parse", "expression": expr, "expected": rule["alternatives"]})
    for _ in range(20):
        pos = rng.sample(stats, rng.choice([2, 3])); neg = rng.choice([[], ["zoom"], ["ammo_maximum"], ["critical_damage"]])
        cases.append({"kind": "rule", "positives": pos, "negatives": neg, "rule": rule, "expected": asdict(evaluate_curated_roll(pos, neg, rule))})
for _ in range(100):
    colors = rng.choices(["ash grey", "sargas brown", "alad blue", "mars red", "unknown", "sargas brown (gold)"], k=rng.randrange(0, 5))
    cases.append({"kind": "colors", "colors": colors, "expected": rarity_from_colors(colors)})
for i in range(120):
    pos = ["critical_chance", "critical_damage"] + (["multishot"] if i % 2 else [])
    neg = "zoom" if i % 3 else None
    kind = rng.choice(["rifle", "pistol", "archgun"]); disposition = rng.uniform(.5, 1.55)
    ranges = calculate_riven_stat_ranges(slugs=pos, negative=neg, stat_class=kind, disposition=disposition)
    cases.append({"kind": "riven_ranges", "positives": pos, "negative": neg, "stat_class": kind, "disposition": disposition, "expected": [asdict(r) for r in ranges]})
    auctions = []
    for j in range(rng.randrange(4, 24)):
        attrs = [{"url_name": r.slug, "positive": r.positive, "value": rng.uniform(r.minimum, r.maximum)} for r in ranges]
        a = {"id": str(j), "buyout_price": rng.choice([20, 100, 250, 500, 700, 999999]), "owner": {"slug": f"test-{j}", "ingame_name": f"Test{j}", "status": rng.choice(["online", "offline", "ingame"])}, "item": {"attributes": attrs}}
        auctions.append(a)
        cases.append({"kind": "riven_quality", "auction": a, "stat_class": kind, "disposition": disposition, "expected": auction_roll_quality(a, stat_class=kind, disposition=disposition)})
    weekly = WeeklyPrice("Test", "Rifle", True, 200, 250, 10, 2500, 50, 40) if i % 2 else None
    rule = {"alternatives": parse_rule_expression("CC CD MS/DMG"), "harmless_negatives": ["zoom"], "positive_expression": "CC CD MS/DMG", "notes": "Synthetic test"} if i % 3 else None
    on = bool(i % 2); curated = bool(i % 4); ceiling = 1500 if i % 5 else None
    kwargs = dict(weapon_slug="test", weapon_name="Test", weekly=weekly, stat_class=kind, disposition=disposition, minimum_discount_pct=10, maximum_price=ceiling, online_only=on, limit=100, roll_rule=rule, curated_only=curated)
    cases.append({"kind": "riven_deals", "auctions": auctions, "weekly": asdict(weekly) if weekly else None, "stat_class": kind, "disposition": disposition, "online_only": on, "curated_only": curated, "max_price": ceiling, "rule": rule, "expected": [asdict(d) for d in find_riven_deals(auctions, **kwargs)]})
for price in [None, 0, .5, 20, 20.5, "10", "10.5", "no"]:
    for direct in [True, False]:
        a = {"buyout_price": price, "is_direct_sell": direct, "starting_price": 30}
        cases.append({"kind": "auction_price", "auction": a, "expected": auction_price(a)})
trade_texts = [
    "[12:34] SellerOne: WTS [Kuva Bramma] 120p",
    "Buyer_2 WTB [Torid] 55 plat",
    "WTS Latron 100pl",
    "noise without an action",
    "Trader: WTT [Kuva Bramma] [Torid] 99p",
    "Seller: WTS [Unknown Weapon] 10p",
]
with tempfile.TemporaryDirectory() as trade_temp:
    trade = RivenTradeChatLog(Path(trade_temp) / "never-written.jsonl", ["Kuva Bramma", "Torid", "Latron"])
    stamp = datetime(2026, 1, 2, 3, 4, 5, 123456, tzinfo=UTC)
    for text in trade_texts:
        offers, unparsed = trade.parse(text, observed_at=stamp, source="synthetic")
        cases.append({"kind": "trade_parse", "text": text, "observed": stamp.isoformat(), "weapons": ["Kuva Bramma", "Torid", "Latron"], "expected": {"offers": [asdict(o) for o in offers], "unparsed": unparsed, "summary": price_summary(offers)}})
for group in range(40):
    scope = rng.choice(["Online only", "Offline only", "Both (best of either)"])
    tier = rng.choice(["intact", "exceptional", "flawless", "radiant"])
    configs = []; rows = []
    for i in range(12):
        relic = Relic(f"Test {i}", [RelicReward(f"Reward {j}", rarity) for j, rarity in enumerate(["common"] * 3 + ["uncommon"] * 2 + ["rare"])] if i else [], rng.choice([True, False, None]))
        prices = {r.reward_name.lower(): rng.choice([None, 0, 2, 10, 80, 300]) for r in relic.rewards}
        info = {"online": rng.choice([None, 0, 30, 60]), "offline_included": rng.choice([None, 0, 20, 70]), "online_quantity": rng.choice([None, 0, 1, 20]), "offline_quantity": rng.choice([None, 0, 1, 20]), "online_subtype_matched": bool(i % 2), "offline_subtype_matched": bool(i % 3), "online_is_outlier": bool(i % 4), "offline_is_outlier": bool(i % 5)}
        ducats = {r.reward_name.lower(): rng.choice([15, 45, 100]) for r in relic.rewards}
        configs.append({"relic": asdict(relic), "prices": prices, "info": info, "ducats": ducats})
        rows.append(compute_row(relic, tier, .05, prices, {relic.relic_name: info}, ducats, scope))
    filter_options = {"vault_filter": rng.choice(["All", "Unknown", "Vaulted", "Unvaulted"]), "min_roi": rng.choice([None, 0, 25]), "max_cost": rng.choice([None, 0, 50]), "min_reward": rng.choice([None, 0, 100]), "green_only": bool(group % 2)}
    expected = {"ranks": {mode: [r["relic"].relic_name for r in sorted(rows, key=sort_key_for_mode(mode, scope), reverse=True)] for mode in RANK_MODES}, "passes": [passes_filters(r, channel_scope=scope, **filter_options) for r in rows], "risk": [r["best_risk"] for r in rows], "category": [r["overall_category"] for r in rows]}
    cases.append({"kind": "ranking", "rows": configs, "scope": scope, "tier": tier, "filters": filter_options, "expected": expected})
path = ROOT / "csharp" / "fixtures.generated.json"
path.write_text(json.dumps(cases, ensure_ascii=False), encoding="utf-8")
print(f"Generated {len(cases)} synthetic parity cases; no private data read.")
