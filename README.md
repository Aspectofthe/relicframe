# RelicFrame

RelicFrame is a Warframe relic-profitability, market-analysis, world-state, Personal Market, Arcane, Prime-set, and local Riven-OCR Discord bot. The active implementation is the .NET 10 application under `csharp/`; legacy Python files remain only for migration/reference work.

## Legacy Python bot

Requirements: Python 3.11 or newer and a Discord bot token.

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install -r requirements.txt
Copy-Item relicframe\.env.example relicframe\.env
python -u main.py
```

Edit `relicframe/.env` and set `DISCORD_BOT_TOKEN` before the final command. Never commit that file. The root launcher changes into `relicframe/` so all existing data paths continue to work.

Run the Python tests from the application directory:

```powershell
Set-Location relicframe
python -m unittest discover -s tests -p "test_*.py"
```

See [relicframe/README.md](relicframe/README.md) for commands, permissions, diagnostics, desktop mode, webhook mode, and data details.

## C# bot

Install the .NET 10 SDK, then run from the repository root:

Windows: run `.\csharp\run-bot.cmd`. Linux: run `chmod +x csharp/run-bot.sh && ./csharp/run-bot.sh`. Both launchers build Release, securely prompt for the Discord token and server ID on first use, and start the bot. See [csharp/README.md](csharp/README.md) for environment variables, Linux OCR/Proton integration, Docker, commands, and deployment notes.

The C# test bot also creates an owner-only Personal Market board. With explicit local authorization it can reuse AlecaFrame's local JWT to create/update bounded Prime sell listings while enforcing a price floor and owner-only emergency pause. Credentials are never printed or committed; automatic trading remains a Warframe.market grey area.

Market/world/Discord boards and attached-image OCR are platform-independent. Steam/Proton EE.log discovery and Linux Trade Chat capture are supported with the setup documented in the C# guide.

Riven screenshots use local Tesseract OCR only—no OpenAI or third-party OCR service. `/rf-riven appraise image:` runs four image-processing passes, validates the recognized weapon and attributes, then requires a private editable confirmation before it requests a price.

## Important local data

Runtime state, credentials, private companion exports, generated fixtures, virtual environments, build outputs, and market snapshots are ignored by Git. Back up private evidence and Discord state separately before redeploying.
