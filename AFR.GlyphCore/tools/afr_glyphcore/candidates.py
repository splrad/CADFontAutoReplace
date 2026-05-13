from __future__ import annotations

from dataclasses import dataclass


@dataclass
class Candidate:
    text: str
    source: str
    reason: str
    is_roundtrip: bool

    @property
    def is_noop(self) -> bool:
        return "current-noop" in self.source.lower()

    def add_source(self, source: str, reason: str, is_roundtrip: bool) -> None:
        if source and source.lower() not in self.source.lower():
            self.source += "+" + source
        if reason and reason.lower() not in self.reason.lower():
            self.reason += "; " + reason
        self.is_roundtrip = self.is_roundtrip and is_roundtrip


def build_candidates(current_text: str) -> list[Candidate]:
    candidates: list[Candidate] = []
    _add_candidate(candidates, current_text, "current-noop", "current text", True)
    _try_add_conversion(candidates, current_text, "cp950", "gbk", "big5-carrier-to-gbk")
    _try_add_conversion(candidates, current_text, "gbk", "cp950", "gbk-carrier-to-big5")
    _try_add_conversion(candidates, current_text, "utf-8", "gbk", "utf8-carrier-to-gbk")
    _try_add_conversion(candidates, current_text, "gbk", "utf-8", "gbk-carrier-to-utf8")
    return sorted(candidates, key=lambda c: (1 if c.is_noop else 0, c.text))


def make_corrupted_current(correct_text: str, carrier_encoding: str, target_encoding: str) -> str | None:
    try:
        payload = correct_text.encode(target_encoding, errors="strict")
        current = payload.decode(carrier_encoding, errors="strict")
    except UnicodeError:
        return None

    if current == correct_text:
        return None
    return current


def _try_add_conversion(
    candidates: list[Candidate],
    current_text: str,
    carrier_encoding: str,
    target_encoding: str,
    source: str,
) -> None:
    if not current_text:
        return

    try:
        carrier_bytes = current_text.encode(carrier_encoding, errors="strict")
        decoded = carrier_bytes.decode(target_encoding, errors="strict")
        if not decoded or decoded == current_text:
            return

        candidate_bytes = decoded.encode(target_encoding, errors="strict")
        roundtrip = (
            carrier_bytes == candidate_bytes
            and candidate_bytes.decode(carrier_encoding, errors="strict") == current_text
        )
        reason = "roundtrip-ok" if roundtrip else "roundtrip-failed"
    except UnicodeError:
        return

    _add_candidate(candidates, decoded, source, reason, roundtrip)


def _add_candidate(
    candidates: list[Candidate],
    text: str,
    source: str,
    reason: str,
    is_roundtrip: bool,
) -> None:
    if not text:
        return

    for candidate in candidates:
        if candidate.text == text:
            candidate.add_source(source, reason, is_roundtrip)
            return

    candidates.append(Candidate(text, source, reason, is_roundtrip))
