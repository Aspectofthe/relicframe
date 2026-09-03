"""Hosting entry point: run the Discord bot with its expected working directory."""

import os
from pathlib import Path
import runpy
import sys


def main():
    app_dir = Path(__file__).resolve().parent / "relicframe"
    os.chdir(app_dir)
    sys.path.insert(0, str(app_dir))
    runpy.run_path(str(app_dir / "bot.py"), run_name="__main__")


if __name__ == "__main__":
    main()
