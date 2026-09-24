# Feature Coverage

The test suite describes production behavior; it is not a collection of
placeholder features.

| Area | Production modules |
| --- | --- |
| Profit, ROI, risk, odds, Buy-N, refinement and ducats | `relic_data.py`, `relic_row.py`, `analysis.py` |
| Filtering and rank modes | `filtering.py`, `ranking.py` |
| Market normalization and order selection | `order_math.py`, `wfm_api.py` |
| Concurrent order retrieval and rate control | `http_client.py`, `rate_limiter.py` |
| Live order books and reconciliation | `order_book.py`, `reconciler.py`, `live_market.py` |
| WebSocket incremental updates | `ws_client.py` |
| Official and WFInfo relic data | `parse_official_drops.py`, `wfinfo_data.py`, `vault_status.py` |
| Desktop interface | `main.py` |
| Full Discord bot and persistent lists | `bot.py`, `discord_live_lists.py`, `formatting.py`, `state.py` |
| Discord webhook publisher | `discord_webhook.py` |

Run `python -m unittest discover -s tests -t .` to verify these behaviors.
