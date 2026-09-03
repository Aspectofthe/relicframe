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

## Checks

From `relicframe/`, run:

```sh
python -m unittest discover -s tests -p 'test_*.py'
```
