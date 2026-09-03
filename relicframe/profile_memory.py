"""Run isolated reproducible memory probes without logging into Discord.

python profile_memory.py orders [--compressed]
python profile_memory.py world   # read-only public world-state requests
"""
import argparse
import asyncio
import gc
import json
import time

from memory_usage import log_memory


def orders(compressed):
    from order_book import OrderBookStore
    store = OrderBookStore(compressed=True) if compressed else OrderBookStore()
    log_memory("orders baseline")
    started = time.perf_counter()
    for book in range(500):
        # Decode separate objects like real HTTP JSON responses, not shared fixture strings.
        rows = json.loads(json.dumps([{
            "id": f"{book:08d}{seller:016d}", "type": "sell", "platinum": seller + 1,
            "quantity": 5, "perTrade": 1, "subtype": "radiant", "visible": True,
            "createdAt": "2026-09-03T00:00:00Z", "updatedAt": "2026-09-03T01:00:00Z",
            "user": {"id": f"{seller:024d}", "ingameName": f"Seller{seller}", "status": "ingame",
                     "avatar": "avatar/" + "a" * 100, "reputation": 30, "locale": "en", "platform": "pc"},
        } for seller in range(200)]))
        store.apply_full_fetch(f"item_{book}", rows)
    del rows
    gc.collect()
    log_memory("100000 orders retained")
    build_seconds = time.perf_counter() - started
    started = time.perf_counter()
    checksum = sum(store.get(slug).best_matching_entry_online("radiant")[0]["price"] for slug in store.slugs())
    print(json.dumps({"compressed": compressed, "books": 500, "orders": 100000,
                      "build_seconds": round(build_seconds, 3),
                      "query_seconds": round(time.perf_counter() - started, 3), "checksum": checksum}))
    return store


def riven_pool(compressed):
    from auction_pool import AuctionPool
    pool = AuctionPool() if compressed else {}
    log_memory("riven pool baseline")
    try:
        for batch in range(60):
            rows = json.loads(json.dumps([{
                "id": str(batch * 500 + i), "starting_price": 100, "buyout_price": 120,
                "owner": {"ingame_name": f"Seller{i}", "status": "ingame", "avatar": "a" * 100},
                "item": {"type": "riven", "weapon_url_name": f"family_{(batch * 500 + i) % 418}",
                         "name": "Acri-visican", "mod_rank": 8, "re_rolls": 15,
                         "attributes": [{"url_name": stat, "value": 125.5, "positive": True}
                                        for stat in ("critical_damage", "multishot", "critical_chance")]},
            } for i in range(500)]))
            if compressed:
                pool.add(rows)
            else:
                for row in rows:
                    pool.setdefault(row["item"]["weapon_url_name"], []).append(row)
        del rows
        gc.collect()
        log_memory("30000 auctions retained")
        print(json.dumps({"families": len(pool), "auctions": sum(len(pool[key]) for key in pool)}))
    finally:
        if compressed:
            pool.close()


async def startup():
    from unittest.mock import patch
    with patch("local_env.load_local_env", return_value=0):
        import bot  # imports features and local evidence, NEVER logs into Discord
    bot.state.load_relics()
    log_memory("features and local evidence loaded")
    try:
        await bot.world_feed.client.fetch()
        log_memory("features plus public world feed")
        store = orders(True)
        riven_pool(True)
        log_memory("combined synthetic workload")
        print("Retained order books:", len(store.slugs()))
    finally:
        await bot.world_feed.client.close()


async def riven_live():
    from pathlib import Path
    import shutil
    import tempfile
    from riven_market import RivenMarketService
    with tempfile.TemporaryDirectory(prefix="relicframe-profile-") as temp:
        source = Path(__file__).resolve().parent / "data" / "rivens"
        for name in ("attributes.json", "weapons.json", "weapon_variants.json", "roll_rules.json"):
            shutil.copyfile(source / name, Path(temp) / name)
        service = RivenMarketService(temp)
        log_memory("live riven baseline")
        try:
            deals, families, _ = await asyncio.wait_for(service.refresh_flip_index(force=True), timeout=180)
            log_memory("live riven complete")
            print(json.dumps({"families": families, "deals": len(deals), "failed_searches": service.flip_index_failures}))
        finally:
            await service.close()


async def world():
    from world_state import WorldStateClient
    client = WorldStateClient()
    log_memory("world baseline")
    original = client._json
    async def measured(url):
        result = await original(url)
        log_memory(url.rsplit("/", 1)[-1])
        return result
    client._json = measured
    try:
        await client.fetch()
        log_memory("world retained")
    finally:
        await client.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["orders", "world", "riven-pool", "startup", "riven-live"])
    parser.add_argument("--compressed", action="store_true")
    args = parser.parse_args()
    if args.mode == "orders":
        orders(args.compressed)
    elif args.mode == "world":
        asyncio.run(world())
    elif args.mode == "riven-pool":
        riven_pool(args.compressed)
    elif args.mode == "startup":
        asyncio.run(startup())
    else:
        asyncio.run(riven_live())
