"""Ephemeral, bounded SQLite spool for a complete sampled Riven auction pool.

Only the family currently being evaluated is decoded. Never used as a stale
pricing fallback; the spool is destroyed when the scan completes/fails.
"""
from collections.abc import Mapping
import json
import sqlite3
import tempfile
from pathlib import Path
import zlib


class AuctionPool(Mapping):
    def __init__(self):
        self._temp = tempfile.TemporaryDirectory(prefix="relicframe-auctions-")
        self.db = sqlite3.connect(str(Path(self._temp.name) / "pool.sqlite"))
        self.db.execute("PRAGMA page_size=4096")
        self.db.execute("PRAGMA cache_size=-512")
        self.db.execute("PRAGMA mmap_size=0")
        self.db.execute("PRAGMA journal_mode=OFF")
        self.db.execute("PRAGMA max_page_count=16384")  # 64 MiB at default 4 KiB/page
        self.db.execute("CREATE TABLE auctions (seq INTEGER PRIMARY KEY, id TEXT UNIQUE, weapon TEXT, payload BLOB)")
        self.db.execute("CREATE INDEX by_weapon ON auctions(weapon)")

    def add(self, rows):
        for row in rows:
            auction_id = str(row.get("id") or "")
            if not auction_id:
                continue
            item = row.get("item") or {}
            weapon = str(item.get("weapon_url_name") or "").casefold().strip()
            if not weapon or str(item.get("type") or "riven").casefold() != "riven":
                self.db.execute("DELETE FROM auctions WHERE id=?", (auction_id,))
                continue
            payload = zlib.compress(json.dumps(row, separators=(",", ":"), ensure_ascii=False).encode(), 1)
            self.db.execute("INSERT INTO auctions(id,weapon,payload) VALUES (?,?,?) "
                            "ON CONFLICT(id) DO UPDATE SET weapon=excluded.weapon,payload=excluded.payload",
                            (auction_id, weapon, payload))
        self.db.commit()

    def __getitem__(self, weapon):
        rows = [json.loads(zlib.decompress(payload)) for (payload,) in
                self.db.execute("SELECT payload FROM auctions WHERE weapon=? ORDER BY seq", (weapon,))]
        if not rows:
            raise KeyError(weapon)
        return rows

    def __iter__(self):
        return iter(row[0] for row in self.db.execute("SELECT weapon FROM auctions GROUP BY weapon ORDER BY MIN(seq)"))

    def __len__(self):
        return self.db.execute("SELECT COUNT(DISTINCT weapon) FROM auctions").fetchone()[0]

    def close(self):
        db = getattr(self, "db", None)
        if db is not None:
            db.close()
            self.db = None
        temp = getattr(self, "_temp", None)
        if temp is not None:
            temp.cleanup()

    def __del__(self):
        self.close()
