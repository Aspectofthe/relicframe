# RelicFrame bot setup

RelicFrame currently runs directly on the computer that launches it. An Oracle server, shared price service, reverse proxy, or public IP is **not required**. Remote/shared caching may be added later as an optional deployment mode.

## Fresh-install checklist

For the current C# bot, select the **`csharp-rewrite` branch** on GitHub before downloading the ZIP, or clone that branch. Extract the entire repository; do not run a copied `csharp` folder without its sibling `relicframe/data` files. Run the launcher from the repository root.

| Needed for | What to provide |
| --- | --- |
| Every installation | .NET 10 SDK, a Discord bot token, a Discord server ID, the invited bot with permissions and Message Content Intent, and the checked-in `relicframe/data` directory |
| Private Personal Market controls for a non-server-owner | That person's Discord user ID as `RELICFRAME_PERSONAL_USER_ID` or in a locally created settings file |
| Showing owned Prime parts | A compatible, current local inventory file: AlecaFrame `lastData.dat`, WFHelper/helper `inventory.json`, or a manual JSON list |
| Creating/changing Warframe.market listings | The account owner's own Warframe.market token; keep auto-publishing paused until inventory and prices have been verified |
| Immediate AlecaFrame completed-trade reconciliation | An optional AlecaFrame **Trades-only public token**; omit this entirely if AlecaFrame is not used |
| Capturing visible Trade Chat on Linux | Optional desktop capture packages and configuration in **Linux OCR** below; not needed for Discord image attachments |

Public relic, world, Prime, Arcane, mod and Riven boards do **not** require AlecaFrame, WFHelper, a Warframe.market login, OpenAI, or another cloud AI key. `csharp/runtime/` and private token/settings files are intentionally **absent from GitHub**; each installer creates their own local configuration. Never copy a friend's runtime directory or run two instances with the same bot token in the same server.

## 1. Install the prerequisites

- Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Download or clone the RelicFrame repository.
- On Windows, keep the repository in a normal user-writable folder and run commands from its root directory.
- On Linux, also install the optional capture packages described under **Linux OCR** below if Trade Chat OCR is wanted.

Confirm the SDK is available:

```powershell
dotnet --version
```

The result must begin with `10.`. The checked-in Windows launcher will use `.tools\dotnet\dotnet.exe` instead when that local SDK exists.

For a Git checkout, use `git clone --branch csharp-rewrite https://github.com/Aspectofthe/relicframe.git`. For a ZIP, choose the `csharp-rewrite` branch on the repository page before **Code → Download ZIP**, extract it, and open a terminal in the extracted repository root. The repository must contain `csharp/run-bot.cmd` (or `csharp/run-bot.sh`) and `relicframe/data/relics_from_official_data.csv`.

## 2. Create the Discord bot

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and select **New Application**.
2. Open **Bot**, create the bot if Discord has not already done so, and copy/reset its token. Treat the token like a password.
3. Under **Privileged Gateway Intents**, enable **Message Content Intent**. This is required for directly posted Riven screenshots; RelicFrame ignores ordinary text and image posts outside its configured appraisal channel.
4. Open **OAuth2 → URL Generator** and select the `bot` and `applications.commands` scopes.
5. Give the bot these server permissions: **View Channels**, **Send Messages**, **Embed Links**, **Attach Files**, **Read Message History**, **Use Application Commands**, **Manage Channels**, and **Manage Roles**.
6. Open the generated invite link and add the bot to the intended server. Keep the bot's role above the notification roles it manages.

Do not commit the bot token, paste it into Discord, or share the generated runtime credential files.

## 3. Copy the server ID

In Discord, enable **User Settings → Advanced → Developer Mode**. Right-click the server icon, choose **Copy Server ID**, and keep the resulting 17–20 digit number ready for the launcher.

## 4. Start on Windows

From the repository root, run:

```powershell
.\csharp\run-bot.cmd
```

The launcher builds the Release application and prompts once for:

- the Discord bot token, with hidden input;
- the Discord server ID.

It stores both in `csharp/runtime/launch-credentials.xml`, encrypted for the current Windows user with DPAPI. That directory is ignored by Git. To replace the saved credentials:

```powershell
.\csharp\run-bot.cmd -ForgetCredentials
```

After one successful build, `-NoBuild` can be used for a quicker launch:

```powershell
.\csharp\run-bot.cmd -NoBuild
```

Use the normal launch again after downloading new code; `-NoBuild` deliberately skips compiling updates. If the ZIP was extracted to a different computer or Windows account, the old DPAPI credential file cannot be reused—let that computer's launcher prompt for its own token and server ID.

## 5. Start on Linux

From the repository root:

```bash
chmod +x csharp/run-bot.sh
./csharp/run-bot.sh
```

The launcher prompts for the same token and server ID. It stores them in owner-only mode-`600` files inside the ignored `csharp/runtime` directory. Use `--forget-credentials` to replace them or `--no-build` after a successful Release build.

Set optional environment variables in the same terminal **before** starting the launcher. They are not stored in the downloaded ZIP and a variable set in one terminal is not automatically available in a new terminal.

### Syndicate standing converter

The bot creates **SYNDICATE ECONOMY / #standing-profit**. Choose one of the six main Syndicates, then use **Set standing budget** (0–132,000). Each row shows a separate purchase option: required vendor rank, standing cost, expected gross platinum, whole-item quantity and leftover standing. The board covers marketplace-mapped mods, weapons and components; it does not read your in-game standing or spend it. The filter, budget and sort are shared board settings.

Sort by activity-weighted return, platinum per 25,000 standing, or reported sales/day. Prices use the lower of the online R0 ask and the 30-day R0 median, with at least one reported sale/day and three reporting days. Unused Syndicate weapons must meet the game's trade eligibility conditions. Vendor costs come from [WFCD's wiki-derived dataset](https://github.com/WFCD/warframe-drop-data#datasyndicatesjson), refreshed daily with a dated cache allowed for up to seven days. Market statistics are cached for six hours and use the existing shared API limiter. Gross estimates exclude trade tax and do not promise that every unit sells immediately.

### Maxed-mod profit

The bot creates **MOD ECONOMY / #maxed-mod-profit** for tradeable rank-10 mods. It compares R0 purchase asks with max-rank resale estimates, shows Endo/Credit upgrade costs and reported R10 sales/day, and offers best-return, per-1k-Endo and sales/day sorts. Prices must be fresh and R10 activity must average at least one reported sale/day over 30 days with at least three reporting days. Its default ranking is gross gain multiplied by `min(1, sales/day / 5)`.

For estimated net profit, set both `RELICFRAME_ENDO_COST_PLAT_PER_1000` and `RELICFRAME_CREDIT_COST_PLAT_PER_100K` to your personal resource opportunity costs, then restart. Without those values, it shows gross resale gain and exact resource costs separately. Trade taxes are excluded. Statistics are cached for six hours, and additional requests use the existing shared limiter after market bootstrap.

### Windows OCR

On Windows, Riven screenshots also use the Windows OCR engine used by PowerToys Text Extractor. PowerToys installation is unnecessary. This runs locally through the built-in Windows PowerShell 5.1 bridge, passes images in memory, and needs an installed English Windows OCR language pack. If unavailable or a pass fails, the other local OCR engines continue. Set `RELICFRAME_WINDOWS_OCR=0` to disable this extra pass. See [Microsoft's OCR language-pack instructions](https://learn.microsoft.com/en-us/windows/powertoys/text-extractor#supported-languages) if the bot reports a missing pack. Restart the bot after installing a pack.

### Linux OCR

- X11 foreground-window capture: install `xdotool` and ImageMagick (`magick` or `import`).
- Wayland explicit-region capture: install `grim`, set `RELICFRAME_TRADE_OCR_LINUX_GEOMETRY=x,y,width,height`, and set `RELICFRAME_TRADE_OCR_ASSUME_WARFRAME=true`.
- A headless server cannot see Warframe running on another computer. It needs a securely synchronized capture file, `EE.log`, and any selected private inventory source.

OCR and Discord boards work without OpenAI or any other cloud AI service.

## 6. Complete the first Discord setup

Keep the launcher terminal open. When the bot connects, it registers commands in the selected server and creates or repairs its categories and boards.

Run these checks in Discord:

1. `/rf-status` — confirm the gateway is connected and watch initial data loading.
2. `/rf-world setup` — repair **WARFRAME LIVE**, its guide, and notification roles if any are missing.
3. `/rf-panel setup` — repair **THE LIST** and its ranking channels.
4. `/rf-relics refresh` — start or restart the shared market-price refresh when required.

The bot also creates **PRIME ECONOMY / #aya-planner** and **#baro-investments**. These read Digital Extremes' current vendor manifests; neither requires an account token. The Aya board has buttons to sort by overall return, Prime-part opening EV from `#prime-part-prices`, or intact relic sale ask, each per Aya. It also displays the next Resurgence only when the official feed has revealed its featured Prime sets and dates; future relic stock is not priced before publication. The Baro board shows Ducat/Credit costs, 30-day reported sales/day, and separate rank-zero/max-rank mod values. Its buttons sort by sales/day, value per 100 Ducats, or value per 100,000 Credits. Those resource-efficiency numbers are gross value, not profit, unless you set both `RELICFRAME_DUCAT_COST_PLAT` (p/Ducat) and `RELICFRAME_CREDIT_COST_PLAT_PER_100K` (p/100,000 Credits) to your own opportunity costs. Max-rank profit is not estimated without Endo/ranking costs.

The member running setup commands needs **Manage Server**. The bot itself needs **Manage Channels** and **Manage Roles**. Initial market and Riven loading can continue in the background; `/rf-status` reports its current stage.

## 7. Optional Personal Market setup

Personal Market is not required for the public relic, world-state, Arcane, Prime-part, or Riven features.

- Windows automatically checks AlecaFrame's local `lastData.dat` inventory when available.
- `RELICFRAME_PRIME_INVENTORY_JSON` can point to a compatible inventory file on either platform.
- Authenticated listing changes require the account owner's own Warframe.market token through `RELICFRAME_WFM_TOKEN` or `RELICFRAME_WFM_TOKEN_FILE`.
- Keep automatic publishing paused until the inventory, price floor, undercut, and owner-only Discord permissions have been checked.

Never share Warframe.market tokens between users. Each installation keeps its private inventory, trade history, and authenticated listing operations local.

### Set the private-board owner on each installation

The button message **“This private board belongs to its configured owner”** means the clicking Discord user ID does not match the bot's Personal Market owner. Use Discord **User Settings > Advanced > Developer Mode**, then right-click your own profile and **Copy User ID**. Set `RELICFRAME_PERSONAL_USER_ID` to that numeric ID before starting the bot. For example, in the PowerShell window used to launch the bot: `$env:RELICFRAME_PERSONAL_USER_ID = '123456789012345678'`. Alternatively, create `<repo>/csharp/runtime/personal_market_settings.json` yourself with this complete content, replacing the example ID:

```json
{ "DiscordUserId": 123456789012345678 }
```

The `csharp/runtime/` directory is Git-ignored and may not exist in a fresh GitHub download until the launcher runs; neither that settings file nor any AlecaFrame token file is included in the repository. The environment variable wins if both are set; if neither is set, the **Discord server owner** is used. Prime Set Completion uses the same owner. Restart the bot after changing the ID; startup configures the boards again. Do not copy another person's `csharp/runtime/` directory when setting up a separate installation: it contains their owner setting, board IDs, inventory and listing state. If the old owner already had access to an existing private channel, review its Discord permission overwrites manually; changing the configured ID does not remove that old overwrite.

An AlecaFrame public token is **optional** and only enables its completed-trade feed; it is not needed to open the private board or read a WFHelper/manual inventory file. Do not create `aleca-public-token.txt` if you do not use that integration. A separate, owner-specific Warframe.market token is required only for authenticated listing changes; keep auto-publishing paused until it is configured and tested.

For a friend running a separate bot on their PC, the simplest safe start is: launch with **their own** Discord bot token/server ID, set their own owner ID if they are not the server owner, leave auto-publishing off, and confirm `/rf-status` works. Then add their own inventory source. Add a Warframe.market token only when they explicitly want the bot to manage their listings. The `#personal-market` board can exist with an empty inventory; that does not mean the bot found their items.

### Reuse another app's inventory snapshot

RelicFrame reads a **local snapshot file**, not another app's account session or a live inventory API. [WFHelper's inventory setup](https://github.com/WFHelper/wfhelper/blob/main/docs/features/getting-started.md#choose-an-inventory-source) can use its helper-generated `inventory.json`, a manually imported `inventory.json`, or AlecaFrame's `lastData.dat`. Point `RELICFRAME_PRIME_INVENTORY_JSON` at the actual file that your chosen source refreshes, not WFHelper's settings/state file or a Warframe.market export.

| File | Usual location |
| --- | --- |
| RelicFrame owner/settings | `<repo>/csharp/runtime/personal_market_settings.json` (or `RELICFRAME_RUNTIME_DIR/personal_market_settings.json` if the runtime was moved) |
| WFHelper helper snapshot, Windows | `%APPDATA%\WFHelper\api-helper\inventory.json` |
| WFHelper helper snapshot, Linux | `${XDG_CONFIG_HOME:-$HOME/.config}/WFHelper/api-helper/inventory.json` |
| AlecaFrame cache, Windows | `%LOCALAPPDATA%\AlecaFrame\lastData.dat` |
| Manual WFHelper JSON import | Wherever you selected or saved that file; it is not necessarily in WFHelper's app-data directory |

These WFHelper paths are for its normal app-data directory; a custom `WFHELPER_USER_DATA` moves its helper snapshot under that directory instead. Check that the file exists and has a recent modification time. On Windows, RelicFrame already discovers AlecaFrame's cache automatically when no inventory override is set.

Set the override **before starting** the bot (use an absolute path; quote paths with spaces):

```powershell
$env:RELICFRAME_PRIME_INVENTORY_JSON = 'C:\path\to\inventory.json'
.\csharp\run-bot.cmd
```

```bash
export RELICFRAME_PRIME_INVENTORY_JSON='/path/to/inventory.json'
./csharp/run-bot.sh
```

The JSON must contain `Recipes` and/or `MiscItems` arrays with `ItemType` and `ItemCount` rows, or be an `InventoryJson` wrapper containing that data. A flat list of item names and quantities is also accepted, for example `[ { "itemName": "Braton Prime Receiver", "quantity": 2 } ]`. WFHelper's displayed inventory, prices, and internal cache state are **not** interchangeable with that file. If another app exports a different schema, convert it to one of these formats first; RelicFrame does not import arbitrary app databases.

Verify the file before enabling automatic listings:

```powershell
dotnet csharp/RelicFrame.Bot/bin/Release/net10.0/RelicFrame.Bot.dll --profile-inventory 'C:\path\to\inventory.json'
```

This prints row/unit counts without item names. A zero count can mean the file is empty, stale, or incompatible; check its modification time and compare a few owned quantities in the Personal Market board. On Linux, use the same diagnostic with the Linux file path. A copied snapshot stays stale until you copy it again; a mounted or synchronized file updates only when its source app writes a new snapshot. WFHelper's own [automatic inventory refresh](https://github.com/WFHelper/wfhelper/blob/main/docs/features/getting-started.md#choose-an-inventory-source) has a cooldown, while manually imported files require a fresh import. After a trade, wait for the source snapshot to update (or use the optional AlecaFrame trade-history integration described in the README), then sync listings. Keep automatic publishing paused while testing an unfamiliar source, and let only one app manage the same Warframe.market listings to avoid conflicting edits.

## 8. Updating and troubleshooting

Stop the running bot, update the repository, and run the normal launcher again so it rebuilds before connecting. Useful first checks are:

```powershell
dotnet build csharp/RelicFrame.Bot -c Release
.\csharp\run-bot.cmd
```

If commands do not appear, verify the invite included `applications.commands`, the server ID is correct, and the bot is online in that server. If boards cannot be created, recheck **Manage Channels** and **Manage Roles**. If directly posted Riven screenshots are ignored, recheck **Message Content Intent** in the Developer Portal and restart the bot.

| Symptom | Check first |
| --- | --- |
| “This private board belongs to its configured owner” | The clicking user's Discord ID versus `RELICFRAME_PERSONAL_USER_ID` or `DiscordUserId`; the fallback is the server owner. Restart after changing it. |
| No `personal_market_settings.json` or AlecaFrame token file in a GitHub download | Expected: both are optional, local, Git-ignored files. Create only the settings file you need; never use another person's tokens. |
| Personal Market has no owned items | Check the selected inventory file exists, contains compatible rows, has refreshed since the last trade, and reports nonzero rows with `--profile-inventory`. A new installation has no private inventory snapshot. |
| Listing action says token missing | Viewing the board needs no Warframe.market token; authenticated listing writes do. Keep auto-publishing paused until the owner's token is supplied. |
| `dotnet` not found or wrong version | Install the .NET **10 SDK**, reopen the terminal, and run `dotnet --version`; the Windows launcher can also use a checked-in-path `.tools/dotnet` installation if present locally. |
| Startup says public data is missing | Download/extract the full repository and launch from its root; check `relicframe/data/relics_from_official_data.csv`. |
| A friend sees the same board or changes interfere | Confirm each install uses its intended Discord application/token and server. Two processes using one bot token against one server will conflict. |

If a check still fails, send the **exact error text**, the relevant startup or `/rf-status` line, operating system, and whether the source is AlecaFrame, WFHelper, or manual JSON. Redact Discord and market tokens, inventory contents, and private account data before sharing logs.

Oracle hosting and shared public-price caching are intentionally deferred. The local launchers and every current feature work without them.
