# Platinum features: delivery and evidence plan

This is the implementation tracker for the requested platinum-making features. A checked row means the named behavior is implemented in the C# bot; a partial row means the current feature does **not** yet satisfy the whole request. The bot must never silently substitute a current seller's ask for a confirmed sale or invent an acquisition time.

## What feedback changes in the design

- Player discussions repeatedly favor readily tradable Prime parts, mods and Arcanes, but warn that niche high-price items can sit in inventory for days. Rank opportunities by **expected return and observed sale activity**, not just the highest asking price. See [July 2026 mod discussion](https://www.reddit.com/r/Warframe/comments/1upinw5/mods_to_sell_for_platinum/) and [August 2026 platinum discussion](https://www.reddit.com/r/Warframe/comments/1v56x6c/anyone_got_any_ways_to_farm_plat/).
- Prime sets and unopened relics are separate selling routes. Compare their **opportunity costs** and clearly identify whether the player already owns the relic/parts. See [Prime parts and sets](https://warframe-analytics.com/guides/platinum/prime-parts-and-prime-sets) and [unopened relic sales](https://warframe-analytics.com/guides/platinum).
- Follow [Warframe.market's API-client rules](https://docs.warframe.market/docs/rules/overview/): identifiable client, caching, no needless full-book polling, backoff on restrictions. User-provided authenticated listing writes stay separate from public price reads.
- Farm speed is player/build/squad-dependent. A `p/hour` board needs measured or editable mission duration and sale probability. If either is missing, show EV/run and **unknown p/hour**.
- For every recommendation show: source timestamp, sample/volume, online or recent-visible price basis, fees/resource costs where applicable, and a low-confidence state rather than fake precision.

## Current implementation

| Feature | State | Current behavior / remaining gap |
| --- | --- | --- |
| Relic profit rankings | Implemented | EV, ROI, win chance, risk, plat/Trace and guaranteed-profit views. |
| Best relic farm right now | Not yet | Needs actual relic acquisition source/time and fissure duration; do not infer p/hour from mission tier alone. |
| Prime-set completion profit | Implemented | Owner-only one-missing-component board, quantity-aware; includes cheapest source relic. |
| Part versus set calculator | Implemented in this batch | Existing Prime-set board compares owned-part asks with complete-set ask minus missing-part purchase. Requires fresh component prices; otherwise omitted. |
| Relic opening versus selling | Implemented in this batch | Selected relic's drop view compares reward EV against the same-refinement relic's online sell ask; missing prices suppress advice. |
| Radshare calculator | Implemented in this batch | Selected relic's drop view shows rare-visible probability and the best selectable reward EV for 1–4 players. It is per player, not combined squad revenue. |
| Vault investment tracker | Not yet | Needs dated vault transitions, genuine historical supply, and liquidity. No guaranteed appreciation claims. |
| Prime Resurgence/Aya planner | Implemented | `#aya-planner` reads current official Varzia relic stock and Aya costs, offers separate overall, Prime-part-profit and intact-relic-sale sorts per Aya, and names returning Prime gear. It displays the next rotation only after the official preview reveals it; no unrevealed picks or future relic prices are guessed. Aya farm time remains unknown, so no invented platinum/hour. |
| Prime-junk and Ducat evaluator | Implemented | Existing Ducat efficiency and reward-value views. |
| Baro investment assistant | Partial | New `#baro-investments` reads official current stock, Ducat/Credit costs, rank-zero 30-day closed-order medians, activity and live asks. It records observed visits and uses 1–30-day post-departure medians when a prior observed visit has at least three reporting days. Optional `RELICFRAME_DUCAT_COST_PLAT` produces an estimated margin. Closed orders are not confirmed trades; Credit opportunity cost and any future resale price remain unknown. |
| Syndicate standing converter | Not yet | Needs current vendor inventories/standing costs and ranked trade volume. |
| Arcane profit board | Partial | Arcane collection EV, quick-sale ordering and rank-separated market estimates exist; acquisition-location profit/hour does not. |
| Vosfor calculator | Implemented | Dissolve-versus-sell and pack EV already exist; values remain estimated asks. |
| Mod farm profitability | Not yet | Needs exact drop tables, activity time and volume-aware sell-through. |
| Maxed-mod profit | Not yet | Needs rank-specific prices plus precise Endo/Credit upgrade costs. |
| Riven appraisal and desired rolls | Implemented | Local OCR, comparable asks, roll grading and desired-roll guidance. |
| Riven flip finder | Partial | Existing finder; more confirmed-sale and liquidity validation needed. |
| Veiled Riven economics | Not yet | Needs category-specific reveal probabilities and net sell values. |
| Kuva efficiency calculator | Not yet | Needs empirically supported roll-value distributions; cannot promise improvement per reroll. |
| Market price alerts | Not yet | Needs owner opt-in, thresholds, freshness/volume checks and DM rate controls. |
| Spread finder | Not yet | Needs realistic executable buy/sell books, tax and liquidity screens. |
| Volume-aware opportunities | Partial | Arcane collection ordering uses reported R0 activity; general market ranking does not. |
| Price history and trend alerts | Not yet | Needs durable time series and outlier-resistant alerts. |
| Craft-to-sell calculator | Not yet | Must verify tradability of the crafted result and all inputs before offering a route. |
| Event-item tracker | Not yet | Needs official event dates, tradable catalog and dated resale evidence. |
| Lich/Sister market helper | Not yet | Needs contract-specific attributes and market comparables; ordinary item-book prices are invalid. |
| Open-world sales board | Not yet | Needs a verified tradable catalog and quantity/per-trade market handling. |
| Daily platinum route | Not yet | Depends on measured mission times, player unlocks, inventory and liquidity from the earlier features. |
| Portfolio dashboard | Partial | Owner-only Personal Market now shows fresh-price Prime inventory ask value, coverage and a first-priced-snapshot daily change. Liquid/slow stock requires verified trade activity; this value is not realized proceeds. |
| Personal Market improvements | Partial | Sale-adjusted inventory, managed-order reconciliation, price floor and pause exist. The board now distinguishes completed-trade gross from provisional order changes and refuses to create/reprice listings from stale books. Purchase-cost basis, stale-listing age flags and bounded repricing policy remain. |

## Delivery order

1. **Market quality:** common price/volume/freshness model, durable price history, cautious alerts and spread finder. These underpin nearly every later recommendation.
2. **Inventory and route math:** part/set comparison validation, portfolio, Personal Market realized ledger, Prime farm and daily route with user-editable durations.
3. **Verified acquisition catalogs:** Aya/Resurgence, Baro, Syndicates, mods, Arcanes, open-world and events. Each source requires its own drop/cost/tradability tests.
4. **Speculative markets:** vault investment, maxed mods, veiled/Riven rerolls, and Lich/Sister contracts, with explicit confidence and no guaranteed returns.

An item should move to **Implemented** only after its source is current, its calculations have focused tests, the Discord view works with no added full-book API sweep, and stale/missing inputs produce an honest unavailable state.
