# Migration inventory / release gate

Status: **preview, incomplete**. No production cutover, Discord login or channel mutation has been performed by the rewrite work.

## Implemented portions

| Python area | C# replacement | Remaining parity work |
| --- | --- | --- |
| `relic_data`, `analysis`, `relic_row`, `ranking`, `filtering` | `Relics.cs`, `RelicRanking.cs` | Full presentation metadata, remaining analysis helpers, richer comparison UI |
| `order_math`, `order_book`, `rate_limiter`, `http_client`, `reconciler`, `ws_client` | `OrderBook.cs`, `MarketHttp.cs`, `WfmWebSocket.cs` | Production reconciliation metrics and live outage/reconnect soak |
| `live_market`, `wfm_api`, `item_catalog_cache`, `slug_registry`, `seller_blacklist` | `LiveMarket.cs`, `SellerBlacklist.cs` | Full catalog/drop-table refresh, WebSocket reconciliation, production state compatibility |
| `riven_roll_rules` | `RivenRules.cs` | Workbook re-import utility |
| `riven_market`, `auction_pool` | `RivenPricing.cs`, `RivenMarket.cs`, `PublicPayload.cs` | Variant-family lookup parity, per-weapon auction queries, all original command options, real-feed validation |
| `companion_appraisal`, `companion_guide`, color rarity from `companion_vision` | `Companions.cs`, `/rf-companion appraise|guide` | Differential appraisal tests against broader evidence and a reliable free screenshot-classification model/workflow |
| `companion_export_analyzer` | `CompanionExports.cs`, `RelicFrame.Tools` | Streaming/peak-memory validation on the two very large historical HTML archives, CSV/summary presentation parity |
| Parts of `bot`, `diagnostics`, `memory_usage`, `workload` | `RelicFrame.Bot` | Production command parity and workload soak tests |
| Major `world_state`, `discord_world_state` paths | `WorldState.cs`, `WorldManager.cs` | Full static browse.wf enrichment, official fallback for non-fissure sections, detailed bounty/Steel Path rendering and live Discord permission/reconnect soak |
| Saved relic result panels | `RelicPanel.cs`, `RelicPanelManager.cs` | Live Discord permission/restart soak and richer presentation metadata |
| `riven_trade_chat` | `RivenTradeChat.cs` | Broader real-export validation; deliberately no passive game-chat interception |

## Not ported — do not remove the Python implementation

- Remaining `world_state`, `discord_world_state` enrichment: browse.wf regions/challenges/Steel Path Incursion schedule, official-DE repair for non-fissure sections, detailed bounty rows and custom guild emoji lookup. Channels, roles, schedule tiers, Cascade split, stale-fissure fallback and persistent signature deduplication now have C# implementations, but have not been live-guild tested.
- Remaining `discord_automation`, `discord_live_lists`, `bot_guide` parity: richer legacy presentation and live-guild migration/permission/reconnect validation. C# now provisions the requested world channels and roles plus a guide, and provides a saved Radiant-default relic panel with persisted filters, one-minute rendering, and start/stop controls.
- Remaining `state` compatibility: Python cache migration and richer reconciliation metrics. C# now bootstraps full books, smooths stalest-first REST sweeps and optionally routes documented new-order WebSocket events by catalog item ID.
- `parse_official_drops`, `fetch_relic_data`, `wfinfo_data`, `vault_status`: complete automatic drop-table/catalog/vault refresh. C# currently reads the supplied relic CSV; only the ducat-map portion of WFInfo is used live.
- DiscordChatExporter HTML/JSON evidence ingestion and `companion_chat_importer` TXT/image portable archives are now ported as offline C# tools. The supplied 3,900-message JSON export matched all Python aggregate counts; giant historical HTML still needs streaming/peak-memory validation.
- `companion_vision`: screenshot identification. The preview does not infer reliable natural colors, build or pattern from an image. No paid API or made-up local model has been substituted.
- `import_riven_roll_rules`: source-workbook importer. Companion usage, price factors, historical examples and natural-color tiers now have an in-bot guide, though its richer multi-embed presentation is not duplicated yet.
- `discord_webhook`, `formatting`, `local_env`: legacy integrations and remaining presentation parity.
- `main`, `profile_memory` and root Python hosting entry point: desktop GUI/diagnostic tools and production launch scripts remain Python. An isolated C# preview Dockerfile/Compose definition exists, but the new executable is not selected by the existing Python host.

## Verification completed

- Release builds of Core, Bot and Tests with warnings treated as errors.
- 2,851 deterministic synthetic Python/C# comparisons: relic calculations, order normalization/purchasing, curated rules, color rarity, Riven ranges/roll quality/deals, trade-offer parsing/summaries, ranking/filtering/risk/category behavior.
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
