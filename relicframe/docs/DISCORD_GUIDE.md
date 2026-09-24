# RelicFrame Discord Bot — How It Works

This is the guide for people *using* the bot in a Discord server. If you're
setting the bot up or hosting it, see `README.md` instead. In Discord, you
can also just run **`/relics help`** any time for a quick in-app version of
this.

## Live Warframe world feed

An admin can create the complete feed with one command:

```
/world setup
```

This creates a **WARFRAME LIVE** category with separate channels for cycles,
news/KinePage, alerts/events, Sortie, Archon Hunt, Steel Path Incursions,
weekly missions, Deep and Temporal Archimedea, vendors, bounties, normal
fissures, Steel Path fissures, Void Storms, invasions, Arbitrations, and Void
Cascade. Each channel keeps one clean set of messages and edits it every
minute instead of posting duplicates.

The setup also creates a **world-pings** channel with grouped opt-in role menus.
General roles cover Void Cascade, Arbitration, Alerts, Events, Sortie, Archon
Hunt, Steel Path Incursions, Bounties, normal Fissures, Steel Path Fissures,
Void Storms, Invasions, Baro Ki'Teer, Warframe News, and Archimedea. Separate
menus provide Arbitration S/A/B/C/D/F roles and Lith/Meso/Neo/Axi/Requiem/Omnia
roles for normal Fissures, Steel Path Fissures, and Void Storms.

People add or remove their own roles with the menus in that channel. New roles
are baselined during an upgrade, so restarting does not ping every active
mission. Each feed channel keeps only its newest bot ping: the prior ping is
deleted before a fresh notification is sent, and broad plus tier notifications
are combined into one message.

Normal, Steel Path, and Void Storm mission rows also show the matching
Arbitration S/A/B/C/D/F mission grade when the node exists in the loaded local
Arbitration schedule.

The bot needs **Manage Channels**, **Manage Roles**, **View Channels**, **Send
Messages**, **Embed Links**, and **Read Message History**. Its Discord role
must be above the ping roles it creates.

Useful commands:

| Command | What it does |
|---|---|
| `/world setup` | Creates missing channels/roles and repairs the feed. Safe to run again. |
| `/world refresh` | Pulls current data and refreshes this server immediately. |
| `/world status` | Shows the last successful update and loaded Arbitration count. |
| `/world arbitration` | Shows the active Arbitration and the next six rotations. |
| `/world cascade` | Shows active Void Cascade fissures and upcoming Cascade Arbitrations. |
| `/world help` | Shows the short setup guide inside Discord. |

The general live state comes from WarframeStat.us; the richer bounty data and
public schedule/mapping files come from browse.wf. When WarframeStat.us is more
than ten minutes behind, current missions, cycles, alerts, events, news, and
vendors automatically use Digital Extremes' official world-state feed instead.
`/world status` shows the active mission source. The updated `Untitled.txt` is
loaded as the local hourly Arbitration schedule. These requests are separate
from Warframe Market pricing, so the live feed does not use the market client's
five-requests-per-second allowance.

## The idea in one paragraph

RelicFrame prices every Warframe relic against live Warframe Market
listings and tells you which ones are actually worth opening. It checks
both the **online** market (instant trades, small cut) and **offline**
sellers (better prices, you wait for a reply), works out expected profit
*and* worst-case profit for each, and ranks relics by whichever of seven
modes you care about — from "never lose plat" to "best ducats per plat."

## Step 1 — someone needs to fetch prices first

Nothing has live prices until an admin runs:

```
/relics refresh
```

This is the slow part — pricing hundreds of relics against a
rate-limited API takes a few minutes. You'll see a live progress message
with an ETA. Once it finishes, every other command reads that cached
snapshot instantly — nobody else has to wait for a fetch.

**Keeping it fresh automatically** — instead of someone re-running
`/relics refresh` by hand, turn on a recurring background refresh and
pick which channel gets the update posted:

```
/relics refresh auto:Start channel:#relic-prices interval_minutes:30
```

- `channel` — where the "✅ Refresh complete" summary gets posted each
  cycle. Discord shows you a channel picker — just select one.
- `interval_minutes` — how often, from 5 to 1440 minutes (default 30).

Check on it or turn it off any time:

```
/relics refresh auto:Status
/relics refresh auto:Stop
```

There's a 2-minute cooldown per server on anything that actually triggers
a fetch (a plain refresh, or starting auto-refresh) so the bot can't
accidentally hammer Warframe Market — checking status or stopping
auto-refresh isn't affected by that cooldown.

## Step 2 — browse relics

```
/relics list
```

opens an interactive table with:
- **Rank by** dropdown — Best Overall, Guaranteed Profit, Expected
  Profit, Best ROI, Best Plat/Trace, Cheapest, Best Ducat Farming
- **Channel** dropdown — Online + Offline, Online only, Offline only
- **Vault status** dropdown — Any / Unvaulted / Vaulted / Unknown
- **🟢 Guaranteed only** toggle — hide anything that isn't a sure profit
- **Filters…** button — a popup for min ROI, max relic cost, min reward
  price, and your plat-per-trace value (0 by default, meaning you farm
  your own void traces)
- ◀ ▶ page buttons

The list stays interactive for 10 minutes — everyone in the channel can
use the same dropdowns without re-running the command.

## Companion appraisal

Use `/companion appraise` in either mode:

- **Free manual mode:** no screenshot or API key is required. Select species,
  pattern, and build for a Kubrow (Kavats do not use build), then add breed and
  up to four natural colors when known. The bot calculates the color-rarity tier.
- **Optional screenshot mode:** attach one clear Kubrow or Kavat screenshot when
  automatic vision is configured. Manual fields override uncertain detections.

Both modes estimate the two-imprint set from the same sales evidence.

Run `/companion guide` for the complete screenshot setup, inherited-trait
rules, canonical common/uncommon/rare colors, and example values for each
pattern/build tier. Natural colors, neutral lighting, no armor/skins, and a
full-body view produce the best result.

The primary appraisal data covers Kubrows from the exported `kubrow_sales`
channel (April–August 2026). The two breeding-channel HTML exports provide
lower-weight historical support and cannot outweigh matching current sales.
Automatic recognition is optional and requires `OPENAI_API_KEY` on the machine
hosting the bot. Screenshots are sent to the configured OpenAI Responses API
with storage disabled. Manual mode is local and free. Kavat pricing currently
relies mainly on historical HTML evidence, so it should be treated as lower
confidence than the current Kubrow channel.

## Riven tools

The Riven system is free and uses manual stat selection—no OpenAI key is needed.
Every named variant imported from `ALL weapons.txt` appears in weapon search.
Prime, Vandal, Wraith, Prisma, Kuva, Tenet, Coda, and other versions are routed
to their correct shared Riven family while keeping the selected version's own
disposition. A weapon that cannot use a tradable Riven is identified clearly
instead of receiving a made-up appraisal.

| Command | What it does |
|---|---|
| `/riven price weapon:<name>` | Shows rolled/unrolled median, average, range, popularity, and current live asks. |
| `/riven appraise` | Estimates one exact roll from 2–3 positives, optional negative, and cycle count. |
| `/riven top` | Ranks the newest completed-trade feed by popularity or price. |
| `/riven deals` | Finds live asks priced below supported similar-roll peers for one weapon. |
| `/riven refresh` | Admin command that downloads and archives the latest weekly snapshot. |
| `/riven guide` | Explains grades, evidence, risks, and data limitations in Discord. |

The weekly numbers come from Digital Extremes and represent aggregate completed
trades, but they do not include the stats of each sold roll. Exact stats come
from current Warframe.market auctions, whose prices are asks rather than proof
of a sale. The appraisal combines both and shows its comparable count and
confidence. The grade means *relative market neighborhood for this weapon*; it
is not a guaranteed sale price or a complete in-game build grade.

The resale finder never messages sellers or performs a trade. Always inspect
the numerical stat values and the weapon's actual build first. Warframe trade
chat has no public feed, so the bot cannot silently monitor it; chat data must be
explicitly exported, copied, or supplied as screenshots for a future importer.

## Other commands

| Command | What it does |
|---|---|
| `/relics detail relic:<name>` | Full online/offline breakdown for one relic — cost, expected value, worst case, risk score, best reward odds, ducat efficiency. |
| `/relics find item:<name>` | Which relics drop a specific item, cheapest expected cost per copy first. Works even before a refresh, since drop odds don't need prices. |
| `/relics odds relic:<name> reward:<name>` | Chance of pulling a specific reward within 1/3/6/10/20/50 opens. |
| `/relics compare relic:<name>` | Intact → Exceptional → Flawless → Radiant side by side, plus which tier is actually worth refining to for your chosen goal. |
| `/relics buyn relic:<name> n:<count>` | Real cost of buying N copies by sweeping the actual sell-order book (not naive price × N), with your odds of ending up in profit. |
| `/relics status` | How old the cached snapshot is, and whether auto-refresh is on. |
| `/relics help` | This guide, as an in-Discord embed. |

## Reading the colors and emoji

**Profit category** (embed color + leading emoji on every relic row):

| | Meaning |
|---|---|
| 🟢 green | Guaranteed profit — even the worst case is in the black |
| 🟡 yellow | Expected profit only — could lose plat on a bad roll |
| 🔴 red | Guaranteed loss at current prices |
| ⚪ white | No price data to judge yet |

**Vault status dot** next to the relic name: 🟠 vaulted · 🟢 unvaulted ·
⚪ unknown.

**Risk score** (0–100, lower is safer): 🟢 ≤25 · 🟡 26–60 · 🔴 61+.
Factors in price volatility, whether the listing matched your exact
subtype, and whether it looked like an outlier.

**Price annotations:** a `~` after a price means it's a fallback match
rather than an exact subtype match; a `⚠` means that price looked like an
outlier and was excluded from the normal average.

## A few things worth knowing

- **`/relics list` and `/relics detail` never touch the network** — they
  only read the cached snapshot, so they're instant even mid-refresh.
- If you change refinement tier, the cached snapshot's *prices* (fetched
  at one specific tier) won't line up — the bot will tell you to run
  `/relics refresh refinement:<tier>` rather than silently mixing tiers.
- The bot's own Warframe Market calls share one rate limiter across every
  command, so a `/relics buyn` lookup and a background auto-refresh
  running at the same time won't exceed the API's request limit.
