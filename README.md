# RelicFrame

Private source repository for the RelicFrame Discord bot.

The application lives in `relicframe/`. See
[the application guide](relicframe/README.md) for commands and features.

## Hosting

Install dependencies from the repository root:

```sh
python -m pip install -r requirements.txt
```

Set `DISCORD_BOT_TOKEN` in the hosting provider's secret/environment settings.
Do not place tokens in this repository or in the start command.

Start the bot from the repository root:

```sh
python -u main.py
```

On Bot-Hosting's Python template, set `STARTUP_FILE` to `main.py` and leave
the working directory at the repository root. Its default startup command
will install the root `requirements.txt` automatically. The root launcher
switches into `relicframe/` before starting the bot, preserving data paths.
Use the repository-root `main.py`, not `relicframe/main.py` (the desktop UI).
After pulling a code update, restart the deployment. A restart alone may
not fetch new GitHub commits; use the host's pull/redeploy action first.

Keep only one production copy running to avoid duplicate Discord messages.
The root reference files `ALL weapons.txt` and `Untitled.txt` retain their
expected paths relative to the application.

## Automatic Discord setup

On startup and server join, the bot creates/repairs `WARFRAME LIVE`, the seven
`THE LIST` channels, a `bot-guide` explaining every feature, and `refresh`.
Grant Manage Channels, Manage Roles, View Channel, Send Messages, Embed Links,
and Read Message History; place the bot's role above its opt-in notification roles.
Failures are logged per server; reconnects do not duplicate successful setup.

Existing world-feed channels are renamed in place: `world-cycles` stays unchanged,
`world-news` becomes `warframe-news`, `world-pings` becomes `role-pings`, and other
feed names lose `world-`. Persisted channel/message IDs remain valid.
Cascade now shows only normal and Steel Path fissures, with separate opt-in roles.
The old Void Cascade Ping role is reused for normal Cascade (members are preserved).

Default relic refresh is Radiant, force catalog refresh enabled, every one minute
in each server's `refresh` channel. All servers share a single pricing worker;
initial bootstrap may take minutes. Refresh messages are reused rather than piled up.
Catalog forcing applies at worker initialization, not every snapshot cycle.

An administrator with Manage Server can use `/relics stop`, the red **Stop relic
refresh** button, or `/relics refresh auto:Stop` to cancel bootstrap, queued requests,
and the relic pricing worker. Existing lists stay visible; world/Riven feeds continue.
This is process-wide, affecting every server using this bot. Reconnects and joins
do not undo a stop; a process restart restores automatic defaults.

Restart immediately with `/relics refresh auto:Start` and your chosen refinement,
catalog option, channel and interval. Per-list Filters controls are independent
of the refresh settings. Manual refresh controls require Manage Server permission.

## Data and updates

Git tracks source code, tests, the relic CSV, and public Riven reference data.
It deliberately excludes local credentials, virtual environments, private
chat/sales exports, live price snapshots, and Discord channel/message state.

For an existing-bot migration, separately transfer these files through the
hosting provider's private file manager or SFTP, preserving their paths:

- `relicframe/discord_world_state.json`
- `relicframe/discord_list_state.json`
- `relicframe/data/companion_current_market/price_evidence_deduplicated.jsonl`
- `relicframe/data/companion_sales/price_evidence.jsonl`
- Any Riven history or trade-chat observations you want to retain.

The bot can start without the companion evidence, but companion appraisals
will not work until that data is transferred. Live market snapshots can be
rebuilt; private sales history cannot.

Before deploying updates, back up the host's runtime data and confirm the
provider preserves it when replacing code. Do not overwrite current host
state with stale local files. A private repository does not make it safe to
commit tokens or passwords.

## Low-memory hosting (256 MB RAM / 25% CPU / 512 MB disk)

The low-memory path is enabled by default; no extra environment variables are required.
All raw order fields/listings are retained losslessly in compressed per-item books,
decoded only when needed. No top-N truncation or stale on-disk price fallback is used.
Price calculations are unchanged, and decoding runs off the Discord event loop when
refreshing the snapshot. The queue uses four HTTP workers, retaining the existing
five-request-per-second relic ceiling.

Initial relic bootstrap, world-static downloads and full-market Riven index refreshes
take turns rather than overlapping. The first scan may wait while another heavy phase
finishes; ordinary Discord interactions remain available. World exports decode serially.
The Riven scan processes the same stat-search pool, two searches per batch, with a
temporary lossless SQLite spool (64 MiB maximum) and one decoded weapon family at a time.
The spool is removed on completion/cancellation; exceeding its budget fails the scan
explicitly instead of silently dropping listings. On-demand auction cache: four weapons.

Companion evidence keeps only fields used in appraisal in RAM; original private JSONL
files are unchanged. Trade-chat reads stream from disk. Weekly Riven archives stop
accepting new snapshots at 32 MiB and trade-chat imports refuse additions beyond 16 MiB;
existing data is never deleted. Latest Riven prices still refresh at the archive limit.
Export/archive those files privately if either budget is reached. Imported old files,
hosting logs, dependency installations and other externally managed files are not
automatically pruned, so continue watching the host's disk usage.

Console `[memory]` lines report process RSS/peak and, on supported Linux cgroups,
container usage/limit every minute and around heavy phases. A warning appears above
80% of the detected budget; it does not automatically disable features.

Local Windows/Python 3.14 probes (not a guarantee for the Linux container):

| Isolated workload | Before process RAM | After process RAM |
| --- | ---: | ---: |
| 100,000 synthetic orders / 500 books | 152.5 MB | 30.1 MB |
| 30,000 synthetic auctions / 418 families | 87.4 MB | 31.0 MB |
| 8,770 local companion evidence records | 74.0 MB | 31.1 MB |

A combined probe including imports, local evidence, live public world data and both
synthetic workloads peaked at 108.9 MB process RAM. It did not open a Discord gateway
or run a real market bootstrap; Linux page cache/cgroup usage and real payload sizes
can differ. Synthetic book query time increased from 0.187s to 0.659s: compression
trades CPU for memory, particularly relevant to the host's 25% CPU quota.

From `relicframe/`, run `python profile_memory.py orders` and add `--compressed`
to compare the two stores. Modes `riven-pool`, `world`, and `startup` provide additional
probes; `world` and `startup` make read-only public world-state requests. None logs
into Discord. `startup` loads any local companion evidence but prints only counts/RAM.
Mode `riven-live` runs an isolated read-only public market scan with a three-minute
test timeout; it does not overwrite the bot's existing history. The local live probe
reached that timeout during auction collection, peaking at 71.2 MB process RAM, so
a completed real Riven scan has not yet been memory-verified.

After redeployment, capture `[memory]` readings through the first bootstrap and a full
Riven scan. Peak container usage below 256 MB must still be verified on the host.

## Tests

From `relicframe/`, run:

```sh
python -m unittest discover -s tests -p 'test_*.py'
```
