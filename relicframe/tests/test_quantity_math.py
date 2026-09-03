from relic_data import Relic, RelicReward
import relic_row

def test_quantity_is_used_for_batch_profit_and_risk():
    relic = Relic("Test A1", rewards=[
        RelicReward("Cheap", "common"),
        RelicReward("Cheap2", "common"),
        RelicReward("Cheap3", "common"),
        RelicReward("Mid", "uncommon"),
        RelicReward("Mid2", "uncommon"),
        RelicReward("Rare", "rare"),
    ])
    prices = {"cheap": 1.0, "cheap2": 1.0, "cheap3": 1.0, "mid": 5.0, "mid2": 5.0, "rare": 30.0}
    relic_prices = {"Test A1": {
        "online": 5.0, "online_quantity": 4, "online_subtype_matched": True, "online_is_outlier": False,
        "offline_included": 15.0, "offline_quantity": 6, "offline_subtype_matched": True, "offline_is_outlier": False,
    }}
    row = relic_row.compute_row(relic, "radiant", 0.0, prices, relic_prices, {}, "Both (best of either)")
    assert row["online_batch"]["n"] == 4
    assert row["offline_batch"]["n"] == 6
    assert row["online_batch"]["expected_profit"] == row["online_profit"]["expected_profit"] * 4
    assert row["offline_batch"]["expected_profit"] == row["offline_profit"]["expected_profit"] * 6

