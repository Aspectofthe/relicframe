# Migration inventory / release gate

Status: **preview, incomplete**. No production cutover, Discord login or channel mutation has been performed by the rewrite work.

## Implemented portions

| Python area | C# replacement | Remaining parity work |
| --- | --- | --- |
| `relic_data`, `analysis`, `relic_row`, `ranking`, `filtering` | `Relics.cs`, `RelicRanking.cs` | Full presentation metadata, remaining analysis helpers, richer comparison UI |
| `order_math`, `order_book`, `rate_limiter`, `http_client` | `OrderBook.cs`, `MarketHttp.cs` | WebSocket incremental ingestion, production reconciliation metrics |
| `live_market`, `wfm_api`, `item_catalog_cache`, `slug_registry`, `seller_blacklist` | `LiveMarket.cs`, `SellerBlacklist.cs` | Full catalog/drop-table refresh, WebSocket reconciliation, production state compatibility |
| `riven_roll_rules` | `RivenRules.cs` | Workbook re-import utility |
| `riven_market`, `auction_pool` | `RivenPricing.cs`, `RivenMarket.cs`, `PublicPayload.cs` | Variant-family lookup parity, per-weapon auction queries, all original command options, real-feed validation |
| `companion_appraisal`, color rarity from `companion_vision` | `Companions.cs` | Differential appraisal tests against broader evidence, complete guide and screenshot workflow |
| Parts of `bot`, `diagnostics`, `memory_usage`, `workload` | `RelicFrame.Bot` | Production command parity and workload soak tests |

## Not ported — do not remove the Python implementation

- `world_state`, `discord_world_state`: translated/official fallback, browse.wf data, all mission/vendor/news/cycle channels, Arbitration schedules/tiers, normal and Steel Path Cascade fissures, custom emoji resolution, role menus and persistent ping deduplication.
- `discord_automation`, `discord_live_lists`, `bot_guide`: startup/guild-join provisioning, channel renames/migration, full feature guide channel, saved relic panels, one-minute default refresh and restart-with-filters controls.
- `ws_client`, `reconciler`, `state`: gateway-driven order updates, staleness repair and compatible production caches/state.
- `parse_official_drops`, `fetch_relic_data`, `wfinfo_data`, `vault_status`: complete automatic drop-table/catalog/vault refresh. C# currently reads the supplied relic CSV; only the ducat-map portion of WFInfo is used live.
- `riven_trade_chat`: explicit manual imports, deduplication and observation summaries. No passive game-chat interception is planned or claimed.
- `companion_chat_importer`, `companion_export_analyzer`: HTML/JSON export and asset ingestion. Existing generated JSONL is readable; raw export processing is not ported.
- `companion_vision`: screenshot identification. The preview does not infer reliable natural colors, build or pattern from an image. No paid API or made-up local model has been substituted.
- `companion_guide`, `import_riven_roll_rules`: complete reference/help presentation and source-workbook importer.
- `discord_webhook`, `formatting`, `local_env`: legacy integrations, presentation parity and deployment/environment migration.
- `main`, `profile_memory` and root Python hosting entry point: desktop GUI/diagnostic tools and production launch scripts remain Python. The new executable is not selected by them.

## Verification completed

- Release builds of Core, Bot and Tests with warnings treated as errors.
- 2,845 deterministic synthetic Python/C# comparisons: relic calculations, order normalization/purchasing, curated rules, color rarity, Riven ranges/roll quality/deals, ranking/filtering/risk/category behavior.
- Concurrent lossless compressed snapshots.
- HTTP pacing/concurrency, caller cancellation and 429/503 retry tests using injected handlers.
- Safe JSON/object-literal parsing, rejecting executable expressions.
- Disk-pool path isolation, deduplication and owned temporary-file cleanup.
- Synthetic end-to-end Riven scan, all catalog families visited, full roll values saved, index reload and cancellation without publishing incomplete results as a complete index.
- Synthetic relic service, catalog resolution, ducat fallback, seller exclusions, cancelled refresh, real snapshot timestamps and failed-fetch reporting.
- Offline memory benchmark. No full Discord/load/container soak test yet.

## Required release gates

1. Port and test every required production command, feed and panel above. Keep explicit feature accounting; a compiling preview is not full parity.
2. Validate private evidence import and state migration against copies; preserve originals and keep private content out of Git.
3. Use a separate bot/test guild to verify command registration, permissions, messages, role controls, restart behavior and no repeated pings.
4. Verify packaging on the actual Linux/.NET-capable host and collect peak process/container RAM under simultaneous scans, world refresh and Discord activity.
5. Complete a sustained reconnect/restart/rate-limit/outage test. Ensure failures retain data honestly, cancellation stays responsive and queues remain bounded.
6. Only then switch production startup and retire Python from the runtime. Keep a rollback revision and state backups; do not remove the legacy implementation prematurely.

## Intentional preview differences

The development oracle uses Python only to generate tests. Runtime calculations are C#. Monte Carlo uses .NET's seeded RNG, so individual samples differ from Python's RNG while exact cases are compared directly. Exact distributions have an explicit memory guard. Removed seller exclusions take effect immediately against retained raw records, without needing a fresh fetch. WFM consumers share one 5/sec allowance in this preview instead of allowing separate services to exceed that combined rate.
