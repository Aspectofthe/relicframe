"""Hosting entry point for the production Python Discord bot."""

import os
from pathlib import Path
import runpy
import sys


def main() -> None:
    app_dir = Path(__file__).resolve().parent / "relicframe"
    os.chdir(app_dir)
    sys.path.insert(0, str(app_dir))
    runpy.run_path(str(app_dir / "bot.py"), run_name="__main__")


if __name__ == "__main__":
    main()
