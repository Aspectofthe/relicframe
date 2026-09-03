"""Persistent, token-free guide shown in each server's bot-guide channel."""
import discord


def guide_embeds():
    sections = [
        ("RelicFrame • Start here", (
            "I track live Warframe missions, compare relic profitability, find Riven flip leads, "
            "and estimate companion imprint values.\n\n"
            "On startup/join I create or repair **WARFRAME LIVE** and **THE LIST**, including "
            "**#refresh** and this guide. Existing messages are updated in place. "
            "Grant **Manage Channels**, **Manage Roles**, View Channel, Send Messages, Embed Links "
            "and Read Message History; keep my role above the notification roles.\n\n"
            "Default relic refresh: **Radiant • force catalog refresh: True • every 1 minute • #refresh**. "
            "The first market bootstrap can take several minutes. Later cycles rebuild from live order books. "
            "The catalog option applies when starting the pricing worker, not a full catalog download every minute.\n\n"
            "**Kill switch (Manage Server): `/relics stop`**, or `/relics refresh auto:Stop`. "
            "Stops the scheduled refresh and relic pricing worker; leaves existing lists visible. "
            "Restart with `/relics refresh auto:Start refinement:Radiant force_catalog_refresh:True "
            "channel:#refresh interval_minutes:1`. Choose different options if desired. "
            "Pricing is shared by this bot across servers; stopping it affects all servers. "
            "It stays stopped until restarted by an admin or the bot process restarts."
        )),
        ("Relics • Prices, filters and buying", (
            "**`/relics list`** — interactive filters, refinement, online status, vault status and ranking.\n"
            "**`/relics detail`** — one relic's cost, rewards and profit breakdown.\n"
            "**`/relics find`** — relics containing a reward.\n"
            "**`/relics odds`** — reward probability after N openings.\n"
            "**`/relics compare`** — compare Intact through Radiant.\n"
            "**`/relics buyn`** — order-book cost and risk for buying a quantity.\n"
            "**`/relics refresh`**, **`/relics status`**, **`/relics help`** — refresh controls and diagnostics.\n"
            "**`/relics setup-list`** — repair the seven persistent lists.\n"
            "**`/relics blacklist-add`**, **`blacklist-remove`**, **`blacklist-list`** — exclude sellers.\n\n"
            "THE LIST ranks best overall, guaranteed profit, expected profit, ROI, plat/trace, cheapest "
            "and ducat farming. Each list has its own filters; these are separate from refresh settings. "
            "Changing filters does not require re-downloading every price.\n\n"
            "All prices are **Platinum**. Expected profit is probability-weighted, not promised earnings. "
            "Listings can disappear; check quantities and seller availability before trading."
        )),
        ("Live missions • Opt-in notifications", (
            "**#world-cycles**, **#warframe-news**, alerts, sortie, archon, steel-path, weekly, "
            "archimedea, vendors, bounties, fissures, steel-fissures, void-storms, invasions, "
            "arbitration and cascade update automatically.\n\n"
            "Use the menus in **#role-pings** to toggle notification roles, including relic tiers, "
            "Arbitration tiers, **Normal Cascade Fissure** and **Steel Path Cascade Fissure**. "
            "Cascade shows fissures only. Selecting a role again removes it. "
            "New events trigger pings; unchanged feeds do not.\n\n"
            "**`/world setup`** repairs channels and roles; **`/world refresh`** updates the boards. "
            "**`/world status`**, **`/world help`**, **`/world arbitration`**, **`/world cascade`** "
            "show status, help or a quick mission lookup."
        )),
        ("Rivens & companion breeding", (
            "**`/riven flips`** — paginated current leads from the background market-wide scan.\n"
            "**`/riven deals`** — one weapon's buy-low/sell-high leads with rolls and seller contact links.\n"
            "**`/riven price`**, **`/riven top`** — live asks and official weekly completed-trade data.\n"
            "**`/riven refresh`**, **`/riven guide`** — reload reference data and read the methodology.\n"
            "**`/riven chatlog`**, **`/riven chatstats`** — import copied/OCR trade chat and summarize it. "
            "This does not capture in-game chat automatically.\n\n"
            "Riven leads use sampled market listings, roll rules and available comparisons, not every auction. "
            "Asking prices and WTB bids are not completed sales; ROI is a lead, not guaranteed resale.\n\n"
            "**`/companion appraise`** — provide known breed, pattern, build, natural colors and rarity "
            "for an evidence-based imprint estimate. **`/companion guide`** explains identification, "
            "inheritance and price factors. Manual traits need no OpenAI key. Automatic screenshot "
            "recognition is optional and requires configured vision access.\n\n"
            "Companion pricing needs the separately transferred private evidence files. Cosmetic colors "
            "are not inherited; height and gender are not carried on imprints. Prices depend on traits "
            "and available sales evidence; estimates are not guaranteed offers."
        )),
    ]
    return [discord.Embed(title=title, description=body, color=0x5865F2)
            for title, body in sections]
