"""Load ignored local settings without adding another runtime dependency."""
from __future__ import annotations

import os
import re
from pathlib import Path


KEY_PATTERN = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")


def load_local_env(path: str | Path | None = None) -> int:
    """Load KEY=VALUE lines into unset environment variables.

    Existing process variables always win. The default file is `.env` next
    to this module, which is excluded by the project's `.gitignore`.
    """
    env_path = Path(path) if path is not None else Path(__file__).resolve().parent / ".env"
    if not env_path.is_file():
        return 0
    loaded = 0
    for raw_line in env_path.read_text(encoding="utf-8-sig").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[7:].lstrip()
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if not KEY_PATTERN.fullmatch(key) or key in os.environ:
            continue
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in {'"', "'"}:
            value = value[1:-1]
        os.environ[key] = value
        loaded += 1
    return loaded
