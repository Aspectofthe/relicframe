"""Local, explicit trade-chat offer importer.

Warframe does not expose a public trade-chat feed.  This module only parses
text the user deliberately supplies (for example, text copied from Windows
Snipping Tool).  WTS/WTB messages are observations, never recorded as sales.
"""

from __future__ import annotations

import hashlib
import json
import re
import statistics
import threading
from dataclasses import asdict, dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Iterable


ACTION_RE = re.compile(r"\b(WTS|WTB|WTT)\b", re.IGNORECASE)
PRICE_RE = re.compile(r"(?<!\d)(\d{1,6})\s*(?:p|pl|plat|platinum)\b", re.IGNORECASE)
TIME_PREFIX_RE = re.compile(r"^\s*(?:\[\s*\d{1,2}:\d{2}(?::\d{2})?\s*\]\s*)+")
BRACKET_RE = re.compile(r"\[([^\[\]]{2,160})\]")


def _key(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", value.casefold()).strip()


@dataclass(frozen=True)
class TradeChatOffer:
    offer_id: str
    observed_at: str
    action: str
    seller: str
    weapon: str
    price: int | None
    raw_text: str
    source: str = "manual"


@dataclass(frozen=True)
class TradeChatImportResult:
    added: tuple[TradeChatOffer, ...]
    duplicate_count: int
    unparsed_line_count: int


TRADE_CHAT_BUDGET_BYTES = 16 * 1024 * 1024


class RivenTradeChatLog:
    def __init__(self, path: str | Path, weapon_names: Iterable[str]):
        self.path = Path(path)
        self.weapon_names = tuple(sorted(
            {name.strip() for name in weapon_names if name and name.strip()},
            key=lambda value: len(_key(value)),
            reverse=True,
        ))
        self._lock = threading.Lock()

    def _weapon_in(self, value: str) -> str | None:
        normalized = f" {_key(value)} "
        for weapon in self.weapon_names:
            marker = f" {_key(weapon)} "
            if marker in normalized or normalized.strip().startswith(marker.strip() + " "):
                return weapon
        return None

    @staticmethod
    def _seller(line: str, action_start: int) -> str:
        prefix = TIME_PREFIX_RE.sub("", line[:action_start]).strip(" :-|>")
        if ":" in prefix:
            prefix = prefix.rsplit(":", 1)[-1].strip()
        prefix = re.sub(r"^[^A-Za-z0-9_.-]+", "", prefix)
        return prefix[-32:] if prefix else "Unknown"

    def parse(self, text: str, *, observed_at: datetime | None = None, source: str = "manual") -> tuple[list[TradeChatOffer], int]:
        stamp = (observed_at or datetime.now(UTC)).astimezone(UTC)
        offers: list[TradeChatOffer] = []
        unparsed = 0
        for raw in text.splitlines():
            line = " ".join(raw.strip().split())
            if not line:
                continue
            action_match = ACTION_RE.search(line)
            if not action_match:
                unparsed += 1
                continue
            action = action_match.group(1).upper()
            seller = self._seller(line, action_match.start())
            brackets = list(BRACKET_RE.finditer(line))
            found: list[tuple[str, int | None]] = []
            for index, bracket in enumerate(brackets):
                weapon = self._weapon_in(bracket.group(1))
                if not weapon:
                    continue
                end = brackets[index + 1].start() if index + 1 < len(brackets) else len(line)
                nearby = PRICE_RE.search(line[bracket.end():end])
                found.append((weapon, int(nearby.group(1)) if nearby else None))
            if not found:
                weapon = self._weapon_in(line[action_match.end():])
                if weapon:
                    price_match = PRICE_RE.search(line[action_match.end():])
                    found.append((weapon, int(price_match.group(1)) if price_match else None))
            if not found:
                unparsed += 1
                continue
            global_price = PRICE_RE.search(line[action_match.end():])
            for weapon, price in found:
                resolved_price = price if price is not None else (int(global_price.group(1)) if global_price else None)
                identity = "\0".join((stamp.date().isoformat(), action, _key(seller), _key(weapon), str(resolved_price), _key(line)))
                offer_id = hashlib.sha256(identity.encode("utf-8")).hexdigest()[:20]
                offers.append(TradeChatOffer(
                    offer_id=offer_id,
                    observed_at=stamp.isoformat(),
                    action=action,
                    seller=seller,
                    weapon=weapon,
                    price=resolved_price,
                    raw_text=line,
                    source=source,
                ))
        return offers, unparsed

    def load(self) -> list[TradeChatOffer]:
        return list(self._iter_offers())

    def _iter_offers(self):
        if not self.path.exists():
            return
        try:
            with self.path.open(encoding="utf-8") as stream:
                for line in stream:
                    try:
                        yield TradeChatOffer(**json.loads(line))
                    except (json.JSONDecodeError, TypeError, ValueError):
                        continue
        except OSError:
            return

    def import_text(self, text: str, *, observed_at: datetime | None = None, source: str = "manual") -> TradeChatImportResult:
        parsed, unparsed = self.parse(text, observed_at=observed_at, source=source)
        with self._lock:
            existing_ids = {offer.offer_id for offer in self._iter_offers()}
            added = [offer for offer in parsed if offer.offer_id not in existing_ids]
            duplicates = len(parsed) - len(added)
            if added:
                size = self.path.stat().st_size if self.path.exists() else 0
                payload_size = sum(len((json.dumps(asdict(offer), ensure_ascii=False) + "\n").encode("utf-8")) for offer in added)
                if size + payload_size > TRADE_CHAT_BUDGET_BYTES:
                    raise RuntimeError("Trade-chat log reached its 16 MiB budget. Export/archive it before importing more; existing data is unchanged.")
                self.path.parent.mkdir(parents=True, exist_ok=True)
                with self.path.open("a", encoding="utf-8", newline="\n") as target:
                    for offer in added:
                        target.write(json.dumps(asdict(offer), ensure_ascii=False) + "\n")
        return TradeChatImportResult(tuple(added), duplicates, unparsed)

    def recent(self, *, weapon: str | None = None, days: int = 30) -> list[TradeChatOffer]:
        cutoff = datetime.now(UTC) - timedelta(days=max(1, days))
        marker = _key(weapon or "")
        result = []
        for offer in self._iter_offers():
            try:
                stamp = datetime.fromisoformat(offer.observed_at)
            except ValueError:
                continue
            if stamp.tzinfo is None:
                stamp = stamp.replace(tzinfo=UTC)
            if stamp < cutoff or (marker and _key(offer.weapon) != marker):
                continue
            result.append(offer)
        return sorted(result, key=lambda offer: offer.observed_at, reverse=True)


def price_summary(offers: Iterable[TradeChatOffer]) -> dict[str, float | int | None]:
    rows = list(offers)
    wts = [offer.price for offer in rows if offer.action == "WTS" and offer.price is not None]
    wtb = [offer.price for offer in rows if offer.action == "WTB" and offer.price is not None]
    return {
        "observations": len(rows),
        "wts_count": len(wts),
        "wts_min": min(wts) if wts else None,
        "wts_median": statistics.median(wts) if wts else None,
        "wtb_count": len(wtb),
        "wtb_max": max(wtb) if wtb else None,
        "wtb_median": statistics.median(wtb) if wtb else None,
        "possible_spread": (max(wtb) - min(wts)) if wts and wtb else None,
    }
