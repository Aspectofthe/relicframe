# Migration inventory / release gate

Status: **preview, incomplete**. No production cutover, Discord login or channel mutation has been performed by the rewrite work.

## Implemented portions

| Python area | C# replacement | Remaining parity work |
| --- | --- | --- |
| `relic_data`, `analysis`, `relic_row`, `ranking`, `filtering` | `Relics.cs`, `RelicRanking.cs` | Full presentation metadata, remaining analysis helpers, richer comparison UI |
| `order_math`, `order_book`, `rate_limiter`, `http_client`, `reconciler`, `ws_client` | `OrderBook.cs`, `MarketHttp.cs`, `WfmWebSocket.cs` | Production reconciliation metrics and live outage/reconnect soak |
| `live_market`, `wfm_api`, `item_catalog_cache`, `slug_registry`, `seller_blacklist` | `LiveMarket.cs`, `SellerBlacklist.cs` | Full catalog/drop-table refresh, WebSocket reconciliation, production state compatibility |
| `riven_roll_rules` | `RivenRules.cs`, 418-family `roll_rules.json` | Curated per-family alternatives are primary; class-safe market fallback covers future catalog additions. Source-workbook re-import remains offline work. |
| `riven_market`, `auction_pool` | `RivenPricing.cs`, `RivenMarket.cs`, `PublicPayload.cs` | Appraisal, grading, confirmed-sale timing, observed closures, named variants, Endo separation and bounded all-family flips are implemented; live-market soak remains. |
| Parts of `bot`, `diagnostics`, `memory_usage`, `workload` | `RelicFrame.Bot` | Rich component UI parity and workload soak tests; original command group names and autocomplete are ported |
| Major `world_state`, `discord_world_state` paths | `WorldState.cs`, `WorldManager.cs` | Full static browse.wf enrichment, official fallback for non-fissure sections, detailed bounty/Steel Path rendering and live Discord permission/reconnect soak |
| Saved relic result panels | `RelicPanel.cs`, `RelicPanelManager.cs` | Live Discord permission/restart soak; eight interactive ranking channels, navigation, relic selection, drops, seller whispers, and the calculation guide are ported |
| `riven_trade_chat` | `RivenTradeChat.cs`, `EeLogTradeChatCollector.cs`, `TradeChatScreenCollector.cs` | Outgoing EE.log and foreground read-only OCR collectors are implemented; broader real-session OCR validation remains. |

## Not ported — do not remove the Python implementation

- Remaining `world_state`, `discord_world_state` enrichment: broader browse.wf descriptions, Steel Path Incursion scheduling and official-DE repair for additional non-fissure sections. Channels, application emojis, roles, schedule tiers, paginated all-job bounties, Cascade split, stale-fissure fallback and persistent signature deduplication are implemented but still need live-guild soak testing.
- Remaining `discord_automation`, `discord_live_lists`, `bot_guide` parity: richer legacy presentation and live-guild migration/permission/reconnect validation. C# provisions the requested world channels/roles and replacement guide, plus a saved Radiant-default relic panel with persisted filters, five-minute change-only rendering and start/stop controls.
- Remaining `state` compatibility: Python cache migration and richer reconciliation metrics. C# now bootstraps full books, smooths stalest-first REST sweeps and optionally routes documented new-order WebSocket events by catalog item ID.
- `parse_official_drops`, `fetch_relic_data`, `wfinfo_data`, `vault_status`: complete automatic drop-table/catalog/vault refresh. C# currently reads the supplied relic CSV; only the ducat-map portion of WFInfo is used live.
- Companion appraisal/import/image features are intentionally excluded from the C# bot. The obsolete Discord commands are removed during setup.
- `import_riven_roll_rules`: source-workbook importer remains offline work.
- `discord_webhook`, `formatting`, `local_env`: legacy integrations and remaining presentation parity.
- `main`, `profile_memory` and root Python hosting entry point: desktop GUI/diagnostic tools and production launch scripts remain Python. An isolated C# preview Dockerfile/Compose definition exists, but the new executable is not selected by the existing Python host.

## Verification completed

- Release builds of Core, Bot and Tests with warnings treated as errors.
- 2,751 deterministic synthetic Python/C# comparisons: relic calculations, order normalization/purchasing, curated rules, Riven ranges/roll quality/deals, trade-offer parsing/summaries, ranking/filtering/risk/category behavior.
- Concurrent lossless compressed snapshots.
- HTTP pacing/concurrency, caller cancellation and 429/503 retry tests using injected handlers.
- Safe JSON/object-literal parsing, rejecting executable expressions.
- Disk-pool path isolation, deduplication and owned temporary-file cleanup.
- Synthetic end-to-end Riven scan, all catalog families visited, full roll values saved, index reload and cancellation without publishing incomplete results as a complete index.
- Synthetic relic service, catalog resolution, ducat fallback, seller exclusions, cancelled refresh, real snapshot timestamps and failed-fetch reporting.
- Synthetic rendering for every world channel, Arbitration schedule/tier matching, normal/Steel Cascade and all tier signatures, stale-source wording, and official-DE fissure normalization.
- Persistent relic-panel Radiant defaults and saved-filter selection.
- Network-free WebSocket route filtering, tracked item-ID mapping, malformed/unrelated event rejection, and atomic new-order insertion without falsely refreshing the full-book timestamp.
- Discord export parsing, price/trait classification, deduplication and appraiser-compatible evidence generation; the supplied current-market export matched the Python analyzer's 3,900 messages, 11,310 attachments, 2,391 classified rows and 1,413 unique evidence rows.
- Offline memory benchmark. No full Discord/load/container soak test yet.
- Static Linux preview container/Compose packaging with an allowlisted build context and persistent C# state volume. Docker is unavailable on this workstation, so the image itself is not yet built or runtime-validated.

## Required release gates

1. Port and test every required production command, feed and panel above. Keep explicit feature accounting; a compiling preview is not full parity.
2. Validate private evidence import and state migration against copies; preserve originals and keep private content out of Git.
3. Use a separate bot/test guild to verify command registration, permissions, messages, role controls, restart behavior and no repeated pings.
4. Verify packaging on the actual Linux/.NET-capable host and collect peak process/container RAM under simultaneous scans, world refresh and Discord activity.
5. Complete a sustained reconnect/restart/rate-limit/outage test. Ensure failures retain data honestly, cancellation stays responsive and queues remain bounded.
6. Only then switch production startup and retire Python from the runtime. Keep a rollback revision and state backups; do not remove the legacy implementation prematurely.

## Intentional preview differences

The development oracle uses Python only to generate tests. Runtime calculations are C#. Monte Carlo uses .NET's seeded RNG, so individual samples differ from Python's RNG while exact cases are compared directly. Exact distributions have an explicit memory guard. Removed seller exclusions take effect immediately against retained raw records, without needing a fresh fetch. WFM consumers share one 5/sec allowance in this preview instead of allowing separate services to exceed that combined rate.
