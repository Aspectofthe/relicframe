"""Comparable-sales appraisal for companion imprints."""
from __future__ import annotations

import json
import hashlib
import math
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


GROUP_WEIGHTS = {"species": 4.0, "breed": 1.5, "pattern": 2.5, "build": 2.0, "rarity": 2.5, "color": 1.0}
CLASS_WEIGHTS = {"confirmed_sale": 1.0, "listing": 0.75, "price_mention": 0.45, "appraisal": 0.35}


@dataclass(frozen=True)
class AppraisalResult:
    estimate: int
    low: int
    high: int
    comparable_count: int
    screenshot_count: int
    confidence: str
    exact_trait_matches: int
    newest_timestamp: str
    current_comparable_count: int
    historical_comparable_count: int


def _weighted_quantile(values: list[tuple[float, float]], quantile: float) -> float:
    ordered = sorted(values)
    total = sum(weight for _, weight in ordered)
    target = total * quantile
    running = 0.0
    for value, weight in ordered:
        running += weight
        if running >= target:
            return value
    return ordered[-1][0]


def _round_plat(value: float) -> int:
    step = 10 if value < 1000 else 50
    return max(step, int(round(value / step) * step))


def _parse_time(value: str) -> datetime | None:
    try:
        parsed = datetime.fromisoformat(value)
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc)
    except (TypeError, ValueError):
        return None


class CompanionAppraiser:
    def __init__(self, evidence_path: str | Path, historical_evidence_path: str | Path | None = None):
        self.evidence_path = Path(evidence_path)
        self.historical_evidence_path = Path(historical_evidence_path) if historical_evidence_path else None
        self.records = self._load(self.evidence_path, "current", 1.0)
        if self.historical_evidence_path:
            self.records.extend(self._load(self.historical_evidence_path, "historical", 0.08))
        dates = [_parse_time(row.get("timestamp", "")) for row in self.records]
        self.newest = max((date for date in dates if date is not None), default=None)

    @staticmethod
    def _load(path: Path, source: str, source_weight: float) -> list[dict]:
        if not path.is_file():
            return []
        records_by_fingerprint: dict[bytes, dict] = {}
        with path.open("r", encoding="utf-8") as stream:
            for line in stream:
                row = json.loads(line)
                amounts = row.get("amounts") or []
                if len(amounts) != 1:
                    continue
                low = float(amounts[0].get("low", 0))
                high = float(amounts[0].get("high", low))
                if 5 <= low <= high <= 10_000:
                    text = " ".join(str(row.get("text", "")).casefold().split())
                    assets = sorted(
                        f"{item.get('filename', '')}:{item.get('url', '')}"
                        for item in row.get("attachments", [])
                    )
                    fingerprint = "\0".join((text, *assets)) or str(row.get("message_id", ""))
                    fingerprint = hashlib.sha256(fingerprint.encode()).digest()
                    # Pricing only uses these fields. Raw messages/assets remain
                    # untouched in the source JSONL, not duplicated in RAM.
                    row = {"timestamp": row.get("timestamp", ""), "traits": row.get("traits") or {},
                           "classification": row.get("classification"), "amounts": [{"low": low, "high": high}],
                           "attachments": bool(row.get("attachments")),
                           "_appraisal_source": source, "_source_weight": source_weight}
                    previous = records_by_fingerprint.get(fingerprint)
                    if previous is None or row.get("timestamp", "") >= previous.get("timestamp", ""):
                        records_by_fingerprint[fingerprint] = row
        return list(records_by_fingerprint.values())

    def appraise(self, **requested: str | None) -> AppraisalResult:
        traits = {key: value.casefold() for key, value in requested.items() if value}
        if not self.records:
            raise RuntimeError("The current companion market dataset is missing or empty.")

        candidates: list[tuple[float, dict, float, int]] = []
        for row in self.records:
            row_traits = row.get("traits") or {}
            score = 0.0
            exact = 0
            for group, value in traits.items():
                found = {str(item).casefold() for item in row_traits.get(group, [])}
                weight = GROUP_WEIGHTS.get(group, 1.0)
                if value in found:
                    score += weight
                    exact += 1
                elif found:
                    score -= weight * 0.75
            if score <= 0:
                continue
            amount = row["amounts"][0]
            midpoint = (float(amount["low"]) + float(amount.get("high", amount["low"]))) / 2
            candidates.append((score, row, midpoint, exact))

        if not candidates:
            raise RuntimeError("No usable current comparables match those traits.")
        best_score = max(item[0] for item in candidates)
        selected = [item for item in candidates if item[0] >= best_score - 1.5]
        weighted_rows: list[tuple[float, float, str]] = []
        screenshots = 0
        exact_matches = 0
        newest_timestamp = ""
        current_count = 0
        historical_count = 0
        for score, row, midpoint, exact in selected:
            class_weight = CLASS_WEIGHTS.get(row.get("classification"), 0.25)
            date = _parse_time(row.get("timestamp", ""))
            age_days = max(0.0, (self.newest - date).total_seconds() / 86400) if self.newest and date else 365
            source = row.get("_appraisal_source", "current")
            source_weight = float(row.get("_source_weight", 1.0))
            weight = max(0.0001, source_weight * class_weight * math.pow(0.5, age_days / 120) * math.pow(1.8, score))
            weighted_rows.append((midpoint, weight, source))
            screenshots += int(bool(row.get("attachments")))
            exact_matches += int(exact == len(traits))
            newest_timestamp = max(newest_timestamp, row.get("timestamp", ""))
            current_count += int(source == "current")
            historical_count += int(source == "historical")

        current_weight = sum(weight for _, weight, source in weighted_rows if source == "current")
        historical_weight = sum(weight for _, weight, source in weighted_rows if source == "historical")
        history_scale = 1.0
        if current_weight and historical_weight > current_weight * 0.35:
            history_scale = current_weight * 0.35 / historical_weight
        weighted = [
            (value, weight * history_scale if source == "historical" else weight)
            for value, weight, source in weighted_rows
        ]

        estimate = _round_plat(_weighted_quantile(weighted, 0.5))
        low = _round_plat(_weighted_quantile(weighted, 0.2))
        high = _round_plat(_weighted_quantile(weighted, 0.8))
        low, high = min(low, estimate), max(high, estimate)
        current_exact_matches = sum(
            int(exact == len(traits)) for _, row, _, exact in selected
            if row.get("_appraisal_source", "current") == "current"
        )
        if current_count >= 20 and current_exact_matches >= 8:
            confidence = "High"
        elif current_count >= 8 and current_exact_matches >= 3:
            confidence = "Medium"
        else:
            confidence = "Low"
        return AppraisalResult(
            estimate=estimate, low=low, high=high, comparable_count=len(selected),
            screenshot_count=screenshots, confidence=confidence,
            exact_trait_matches=exact_matches, newest_timestamp=newest_timestamp,
            current_comparable_count=current_count,
            historical_comparable_count=historical_count,
        )
