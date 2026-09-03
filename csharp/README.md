# RelicFrame C# rewrite — migration preview

**This is not yet a feature-complete replacement for the Python bot. Do not change the production startup file to this preview.** The Python deployment and its private state remain unchanged. See [MIGRATION.md](MIGRATION.md) for the remaining work and release gates.

The executable is C#/.NET 10, using Discord.Net 3.20.1. It does not run Python, OpenAI or a paid recognition service. Python is used only by the development comparison generator.

## Implemented and tested offline

- Relic CSV loading, four refinement tiers, expected/worst-case returns, odds, trace efficiency, ducat efficiency, quantity-aware purchases, exact distributions and labelled Monte Carlo estimates.
- Seven ranking modes, seller availability/vault/ROI/cost/reward filters and persisted seller exclusions applied before calculation.
- Compressed full order records, atomic snapshots, shared 5-request/second HTTP pacing, bounded concurrency, deadlines, retry/backoff and cancellation. No arbitrary top-N truncation of an order book.
- Background relic-market refresh with catalog caching, ducat fallback, retained old data and honest timestamps after failed fetches.
- Riven stat ranges, numerical roll quality, supplied curated roll rules, comparable-ask deal scoring and official weekly trade ceilings.
- Background Riven scans that evaluate every catalog family against stat-search results, disk-backed deduplication, saved candidate indexes, bounded weekly history, stop/restart and seller/listing links. Search-result caps mean **not every listing is available**; the bot must not claim otherwise.
- Manual-trait companion appraisal from existing private JSONL evidence, distinguishing current versus historical evidence and listings versus confirmed sales. Natural-color rarity classification from supplied names.
- A test-guild Discord preview with immediate deferred acknowledgement, two calculation workers, a bounded queue, and a separate fast status command.
- Test-guild world feeds: automatic `WARFRAME LIVE` setup, the requested visible channel names, persistent role buttons, all base/Arbitration/fissure-tier roles, one-minute background updates, per-channel replacement pings, Cascade split by normal/Steel Path, `Lvl <grade> tier` fissure labels, and fresh official-DE fissure fallback when the translated source is stale.

The tests contain 2,845 synthetic cross-language cases, plus injected HTTP/lifecycle/storage tests. Passing these tests does not certify the unported features or real Discord operation.

## Build and verify

Install the .NET 10 SDK. From the repository root:

```powershell
dotnet build csharp/RelicFrame.Bot -c Release
python csharp/generate_parity.py
dotnet run --project csharp/RelicFrame.Tests -c Release -- csharp/fixtures.generated.json
dotnet csharp/RelicFrame.Bot/bin/Release/net10.0/RelicFrame.Bot.dll
```

The fixture generator requires the existing Python development dependencies in `relicframe/requirements.txt`. It uses synthetic inputs only, not Discord exports, tokens or private sales records. Generated fixtures, binaries, SDK files and runtime state are not committed.

On this workspace the local SDK is `.tools/dotnet/dotnet.exe`, and the existing Python interpreter is `relicframe/.venv/Scripts/python.exe`. These are local tools, not part of the C# deployment.

No arguments prints the preview notice and exits without connecting to anything.

## Optional test-guild run

Use a **separate Discord test application** and a disposable test server. Do not run two deployments using the production token. Set these variables through the host's secret/environment settings; never commit their values or paste them into chat:

| Variable | Purpose |
| --- | --- |
| `RELICFRAME_CSHARP_TOKEN` | Separate test-bot token; required only with `--test-bot` |
| `RELICFRAME_TEST_GUILD_ID` | Selected test server; required only with `--test-bot` |
| `RELICFRAME_DATA_DIR` | Public data and optionally private evidence directory; default `relicframe/data` |
| `RELICFRAME_RUNTIME_DIR` | Writable C#-only cache/state directory; default `csharp/runtime` |

Do not point the runtime directory at Python's working directory or private export directories. The C# preview does not load `.env`, register global commands, delete Python commands, migrate production role/channel IDs or automatically start scans.

```powershell
dotnet csharp/RelicFrame.Bot/bin/Release/net10.0/RelicFrame.Bot.dll --test-bot
```

Available test commands:

- `/rf-status`: responsiveness, gateway latency, memory and worker status.
- `/rf-relics refresh` / `stop`: start/cancel the shared background price worker; requires Manage Server.
- `/rf-relics list`, `detail`, `find`, `odds`, `compare`, `buyn`, `help`.
- `/rf-relics blacklist-add`, `blacklist-remove`, `blacklist-list`; changes require Manage Server.
- `/rf-riven refresh` / `stop`: start/cancel the recurring scanner; requires Manage Server.
- `/rf-riven flips`: page all cached candidates with weapon/budget/ROI/online filters; includes rolls, links and copyable whispers.
- `/rf-riven price`, `top`, `guide`: weekly aggregates and usage instructions.
- `/rf-companion`: manually supply natural traits; requires private evidence copied separately.
- `/rf-world setup`, `refresh`, `start`, `stop`, `status`, `help`: repair and control the C# world boards. Setup/start/stop/refresh require Manage Server; setup also requires the bot to have Manage Channels and Manage Roles.

Refinement defaults to Radiant. A relic refresh sweeps its required books, then waits five minutes; saved relic result panels and their requested one-minute filter scheduler are not ported yet. World boards poll each minute. Riven scans wait 15 minutes between passes. `/rf-status` remains outside the calculation queue. No command automatically sends messages to sellers or purchases anything. World role pings are opt-in, baseline on first observation, and emitted only for newly observed signatures.

## Memory measurements and limits

Run in a fresh process, without including a build:

```powershell
dotnet csharp/RelicFrame.Bot/bin/Release/net10.0/RelicFrame.Bot.dll --profile-orders
dotnet csharp/RelicFrame.Bot/bin/Release/net10.0/RelicFrame.Bot.dll --profile-host
```

Initial Windows measurements:

- 500 books / 100,000 synthetic orders: 22.7 MiB baseline, 40.2 MiB retained working set, 1.090 s build and 0.560 s full-book query.
- Disconnected preview host: 768 relics, four Discord command groups, Discord client objects and Riven rules at 42.3 MiB working set.

These are **not full-bot or Linux-container measurements**, and must not be represented as proof of lower RAM than Python or suitability for a 256 MiB server. The disconnected profile performs no Discord login, private-evidence load or live API request.

Workstation GC is enabled. API bodies are capped at 8 MiB each; concurrency defaults to four. Exact probability distributions stop at 250,000 states rather than exhausting RAM; retry Buy-N with `approximate: True`. Riven temporary data has a 128 MiB disk budget, with an 8 MiB unique-record budget per decoded family. A budget failure is reported, and the last completed index is retained—not silently replaced by truncated results. The budgets do not by themselves bound the entire process working set.

## Before production cutover

Finish the migration checklist, test permissions and interactions in a test guild, publish for the target host, then run a full-feed soak test under that host's actual CPU/RAM limits. Confirm persistence, graceful shutdown, restart, reconnect and ping deduplication before replacing Python. A Python-only startup/container cannot run this executable merely by renaming it.
