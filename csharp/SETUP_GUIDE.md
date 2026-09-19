# RelicFrame bot setup

RelicFrame currently runs directly on the computer that launches it. An Oracle server, shared price service, reverse proxy, or public IP is **not required**. Remote/shared caching may be added later as an optional deployment mode.

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

## 5. Start on Linux

From the repository root:

```bash
chmod +x csharp/run-bot.sh
./csharp/run-bot.sh
```

The launcher prompts for the same token and server ID. It stores them in owner-only mode-`600` files inside the ignored `csharp/runtime` directory. Use `--forget-credentials` to replace them or `--no-build` after a successful Release build.

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

The bot also creates **PRIME ECONOMY / #aya-planner** and **#baro-investments**. These read Digital Extremes' current vendor manifests; neither requires an account token. The Aya board compares listed relic asks with intact solo opening EV. The Baro board shows Ducat/Credit costs and recent rank-zero market activity, but does not promise post-departure profit. Optionally set `RELICFRAME_DUCAT_COST_PLAT` to your own platinum cost per Ducat to see an estimated margin; leave it unset if you do not know that cost.

The member running setup commands needs **Manage Server**. The bot itself needs **Manage Channels** and **Manage Roles**. Initial market and Riven loading can continue in the background; `/rf-status` reports its current stage.

## 7. Optional Personal Market setup

Personal Market is not required for the public relic, world-state, Arcane, Prime-part, or Riven features.

- Windows automatically checks AlecaFrame's local `lastData.dat` inventory when available.
- `RELICFRAME_PRIME_INVENTORY_JSON` can point to a compatible inventory file on either platform.
- Authenticated listing changes require the account owner's own Warframe.market token through `RELICFRAME_WFM_TOKEN` or `RELICFRAME_WFM_TOKEN_FILE`.
- Keep automatic publishing paused until the inventory, price floor, undercut, and owner-only Discord permissions have been checked.

Never share Warframe.market tokens between users. Each installation keeps its private inventory, trade history, and authenticated listing operations local.

## 8. Updating and troubleshooting

Stop the running bot, update the repository, and run the normal launcher again so it rebuilds before connecting. Useful first checks are:

```powershell
dotnet build csharp/RelicFrame.Bot -c Release
.\csharp\run-bot.cmd
```

If commands do not appear, verify the invite included `applications.commands`, the server ID is correct, and the bot is online in that server. If boards cannot be created, recheck **Manage Channels** and **Manage Roles**. If directly posted Riven screenshots are ignored, recheck **Message Content Intent** in the Developer Portal and restart the bot.

Oracle hosting and shared public-price caching are intentionally deferred. The local launchers and every current feature work without them.
