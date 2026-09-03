# RelicFrame

RelicFrame is a Warframe relic profitability analyzer with three interfaces:

- `bot.py` - the complete Discord bot with slash commands, interactive filters, live lists, refinement comparison, item search, odds, and Buy-N analysis.
- `discord_webhook.py` - a simpler one-way Discord publisher that posts or updates a ranked report without a bot token.
- `main.py` - the desktop interface.

All three use the same calculation modules, so expected profit, guaranteed
profit, ROI, risk, quantity handling, refinement odds, and rankings do not
change depending on which interface is used.

## Quick Discord bot setup

1. Install Python 3.11 or newer.
2. Open this folder in PyCharm.
3. Install the dependencies:

   ```text
   pip install -r requirements.txt
   ```

4. Create a Discord application and bot at the Discord Developer Portal.
5. Copy `.env.example` to `.env`, then put `DISCORD_BOT_TOKEN=your-token` in that local file. `.env` is ignored and normal process environment variables still take priority. Never paste a token into source code or PyCharm's shared project files.
6. Companion appraisal works free through manual trait/color fields. Automatic screenshot recognition is optional; if wanted, set `OPENAI_API_KEY=your-key`. The optional `COMPANION_VISION_MODEL` defaults to `gpt-5.4-mini`.
7. Run `bot.py`.
8. In Discord, run `/relics refresh` once, followed by `/relics list`.

If anything looks wrong, double-click `Run Diagnostics.bat`. It performs a
read-only local check of Python, dependencies, relic data, companion evidence,
the Arbitration schedule, and saved Discord configuration without contacting
Discord or Warframe Market. `/relics status` also reports market retries,
persistent-list failures, and the latest auto-refresh error inside Discord.
For a small connection test as well, run `python diagnostics.py --live`.

The included official relic file is loaded automatically from
`data/relics_from_official_data.csv`.

## Discord features

- `/relics list` - interactive ranked list with refinement, channel, vault, minimum ROI, maximum cost, minimum reward value, trace rate, and guaranteed-only filters.
- `/relics detail` - online/offline cost, expected and worst-case profit, ROI, risk, reward odds, trace efficiency, and ducat information.
- `/relics find` - finds every relic containing a requested reward.
- `/relics odds` - chance of obtaining at least one requested reward within N openings.
- `/relics compare` - compares Intact, Exceptional, Flawless, and Radiant.
- `/relics buyn` - sweeps real seller quantities to calculate the cost and outcome distribution for buying N relics.
- `/relics refresh` - starts or refreshes the live market layer, with optional recurring updates and channel selection.
- `/relics stop` - admin kill switch for the shared relic refresh and pricing worker; immediately restart with new settings.
- `/relics setup-list` - creates persistent live-list channels that update existing messages.
- `/relics status` and `/relics help` - data freshness and in-Discord guidance.
- `/world setup` - creates the Warframe live-feed category, its channels, and opt-in notification roles.
- `/world arbitration` - current/upcoming Arbitration lookups; `/world cascade` - normal and Steel Path Cascade fissures only.
- `/companion appraise` - estimates a two-imprint set from free manual species/pattern/build/color fields, or identifies those traits from a screenshot when optional automatic vision is configured.
- `/companion guide` - screenshot instructions, inherited-trait rules, color tiers, and example price ranges.
- `/riven price` - real weekly completed-trade baselines plus current live Riven asks for one weapon.
- `/riven top` - ranks the latest official Riven feed by completed-trade popularity or price.
- `/riven deals` - finds buy-low/sell-high leads for one weapon using supported comparable rolls.
- `/riven flips` - shows every supported current resale lead from the saved all-weapon background scan, with optional price, discount, and online-seller filters.
- `/riven chatlog` - imports copied or OCR-extracted WTS/WTB text into a deduplicated local observation log.
- `/riven chatstats` - summarizes collected trade-chat asks, bids, and possible raw spreads by Riven family.
- `/riven refresh` and `/riven guide` - archive the newest weekly snapshot and explain the system's evidence and limits.

The world feed covers the environment cycles, news/KinePage, alerts/events,
Sortie, Archon Hunt, Steel Path Incursions, weekly missions, Deep and Temporal
Archimedea, Darvo/Baro/weekly vendors, Holdfasts/Cavia/Hex bounties, normal and
Steel Path fissures, Void Storms, invasions, Arbitrations, and a dedicated Void
Cascade watch. It updates existing Discord messages once per minute and does
not consume the Warframe Market price-request allowance.

If WarframeStat.us serves an outdated snapshot, live rotations automatically
switch to Digital Extremes' official world-state feed wherever official data is
available. `/world status` shows which source is active and never describes a
delayed feed as if there were simply no missions.

Fissure rows include their matching S/A/B/C/D/F Arbitration mission grade.
The opt-in panel provides general roles plus per-tier roles for Arbitrations,
normal Fissures, Steel Path Fissures, and Void Storms. Ping notifications are
combined and rolled per channel, leaving only the newest bot ping visible.

The bot automatically creates `bot-guide`, `refresh`, WARFRAME LIVE and THE LIST
on startup/server join. It renames existing feed channels in place: world-cycles
stays, world-news becomes warframe-news, world-pings becomes role-pings, and the
remaining feed names lose world-. Normal and Steel Path Cascade have separate
notification roles. The old Cascade role is reused for normal Cascade.

Relic refresh defaults to Radiant, forced catalog refresh on worker startup,
one minute between cycles, with output in each server's refresh channel.
Use the red stop button or `/relics stop` (Manage Server required) to stop the
shared pricing worker, then `/relics refresh auto:Start` to change the settings.
Reconnects will not override a stop, but a process restart restores the defaults.
Read `bot-guide` in Discord or [the hosting guide](../README.md) for setup details.

## Webhook mode

Webhook mode is useful when a channel only needs an automatically updated
report. It cannot receive slash commands or provide Discord buttons.

Set `DISCORD_WEBHOOK_URL` in PyCharm, then run:

```text
python discord_webhook.py --test-webhook
python discord_webhook.py --interval-minutes 10
```

See [docs/WEBHOOK_GUIDE.md](docs/WEBHOOK_GUIDE.md) for complete setup.

## Desktop mode

Run:

```text
python main.py
```

The desktop app exposes the same calculation engine with sortable tables,
filters, item search, refinement comparison, Buy-N analysis, and detailed
reward information.

## Saved companion-sales chat importer

`companion_chat_importer.py` converts a folder of manually saved `.txt`,
`.png`, `.jpg`, `.jpeg`, `.webp`, and `.gif` files into a portable
Discord-style archive. Double-click `Import Companion Sales.bat`, or run it
without arguments, to choose a folder:

```text
python companion_chat_importer.py
```

It creates a sibling folder ending in `_chat_export` with `index.html` for
viewing, `transcript.txt` for searching, `messages.jsonl` for the future
companion appraiser, and a local `assets` folder containing the images. Files
with the same folder and base name, such as `sale.txt` and `sale.png`, are
kept together as one chat entry. It requires no Discord login or token.

Large DiscordChatExporter HTML archives can be normalized with
`companion_export_analyzer.py`. It writes the complete message history to a
compressed JSONL file and creates reviewable JSONL/CSV files containing likely
sales, listings, appraisals, and platinum-price mentions. The labels are
heuristic evidence categories and are not treated as verified sales without
reviewing their surrounding conversation.

The current `kubrow_sales` JSON export is normalized under
`data/companion_current_market`. Its deduplicated evidence is the primary input
for current Kubrow price ranges; the older breeding-chat archive under
`data/companion_sales` is included as lower-weight historical evidence. When
current matches exist, historical evidence is capped at 35% of their total
pricing influence.

The appraisal command has a local manual mode: select species, pattern, build,
breed, and up to four natural colors; the bot calculates the rarity tier and
prices the result without an API key or image. When optional screenshot vision
is configured, the same fields act as manual corrections.
Mixed multi-pet price posts are excluded from appraisal calculations, reposts
are deduplicated, and asking prices receive less weight than likely completed
sales. The bot shows its comparable count and confidence instead of presenting
the estimate as a guaranteed sale. Optional screenshot recognition requires an
OpenAI API key and sends the attached image to the Responses API with response
storage disabled. Manual mode never sends an image anywhere. Kavat estimates
currently depend mainly on lower-weight historical HTML evidence and therefore
normally show lower price confidence.

## Riven pricing and resale leads

Riven commands do not require OpenAI or a Warframe.market account. The bot
combines Digital Extremes' public weekly Riven feed (aggregate completed trades)
with Warframe.market's public weapon catalog and live direct-sale auctions.
Completed trades establish the weapon baseline; exact-roll auctions provide
current comparables. The scanner never describes an auction ask as a verified
sale.

The supplied root-level `ALL weapons.txt` is parsed as the full named-weapon
catalog. Its prose and embedded browser scripts are ignored; only validated gun
and melee CSV rows are imported. The current import contains 763 named variants:
581 route to one of 418 tradable Riven families, while 182 non-Riven weapons
(such as Amp parts, Railjack armaments, or Exalted weapons) remain searchable
and return a clear unavailable result. Shared families preserve each selected
variant's own disposition—for example, Braton Prime searches Braton Riven
auctions but displays Braton Prime's disposition.

Autocomplete combines those named variants with the live Riven-family catalog,
so newer and modular Riven weapons are not hidden when the local weapon export
lags behind. The supplied good-roll workbook is compiled into
`data/rivens/roll_rules.json`: 417 profiles currently cover every live Riven
family except Dex Nikana, including separate Primary and Melee profiles for
Vinquibus. A curated lead must contain at least two desired positives, satisfy
the profile's required stats, contain no dead third positive, and carry one of
the weapon's listed harmless negatives.

The flip scanner also uses the supplied Riven formula—class base value ×
variant disposition × roll-layout weight × the 90%–110% random quality factor—to
compare the numerical quality of otherwise similar listings. That is a resale
ranking input, not a Riven appraisal feature.

`/riven refresh` saves the latest official feed under `data/rivens/history`.
The public DE URL exposes the newest week, not all past weeks, so the bot reports
the exact number of snapshots it truly has and builds history over time. Existing
dated snapshots can be added later without changing the appraisal interface.
Auction searches are cached for ten minutes and happen only for the requested
weapon, keeping the single-weapon command separate from the relic order-book code.

`/riven deals` and `/riven flips` are lead finders, not automatic traders. They
compare an auction with similar rolls, require multiple supporting peers, and
use 90% of the peer median as a conservative resale target. Curated three-positive
matches rank ahead of two-positive matches. Placeholder and implausible asking
prices are excluded using DE's completed-trade range, so clusters of fake prices
cannot manufacture a deal. A background market index checks
the entire live Riven-family catalog about every 15 minutes and saves the last
complete snapshot to `data/rivens/flip_index.json`; DE completed-trade
activity still boosts liquid opportunities so high theoretical margin on a dead
weapon does not automatically outrank sellability. `/riven flips` reads the
completed snapshot and paginates every matching current deal instead of starting
a limited scan. The broad auction search samples both ends of every positive-stat
market search, deduplicates the listings, and evaluates all 418 catalog families;
the first background pass after a clean install can take a few minutes. Every result shows the live listing's numerical
roll values. Its contact selector opens a private card containing a copyable
in-game `/w` whisper, the auction page, and the seller's Warframe.market profile.
The bot prepares these links and text but does not message sellers, buy anything,
or access Warframe trade chat. Warframe does not provide a public trade-chat
feed; `/riven chatlog` processes copied or OCR text explicitly supplied
by the user. These WTS/WTB records remain labeled as offers rather than
completed sales, and exact Riven rolls still need manual inspection.

## Market-data design

- Prices are normalized per item using `platinum / perTrade`.
- Listings with zero quantity are excluded; missing quantity is not treated as zero.
- Online and offline calculations use the same engine and remain visibly separate.
- Unusual listings are flagged rather than silently deleted.
- The asynchronous client uses bounded concurrency and a shared five-request-per-second start rate.
- Full order books are bootstrapped once and reconciled in the background.
- The WebSocket speed layer uses `wss://ws.warframe.market/socket`, the required `wfm` protocol, and routes `itemId` through the catalog's ID-to-slug map.
- REST reconciliation remains the source of truth because the public new-order stream cannot report every edit, sale, or deletion.

## Tests

Every active test module maps to production behavior. Run the suite from this
folder with:

```text
python -m unittest discover -s tests -t .
```

The tests are network-free and use controlled fakes for Discord and market
requests. The numbered files outside this folder are historical save
iterations and are intentionally not part of this clean project.
