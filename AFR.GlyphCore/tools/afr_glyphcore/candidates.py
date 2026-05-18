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


def build_candidates(current_text: str, context: dict | None = None) -> list[Candidate]:
    candidates: list[Candidate] = []
    _add_candidate(candidates, current_text, "current-noop", "current text", True)
    preferred_source = _evidence_preferred_source(context or {})
    _add_hook_raw_candidate(candidates, current_text, context or {})
    _add_preferred_conversion(candidates, current_text, preferred_source)

    if preferred_source != "big5-carrier-to-gbk":
        _try_add_conversion(candidates, current_text, "cp950", "gbk", "big5-carrier-to-gbk")
    if preferred_source != "gbk-carrier-to-big5":
        _try_add_conversion(candidates, current_text, "gbk", "cp950", "gbk-carrier-to-big5")
    if preferred_source != "utf8-carrier-to-gbk":
        _try_add_conversion(candidates, current_text, "utf-8", "gbk", "utf8-carrier-to-gbk")
    if preferred_source != "gbk-carrier-to-utf8":
        _try_add_conversion(candidates, current_text, "gbk", "utf-8", "gbk-carrier-to-utf8")
    return sorted(candidates, key=lambda c: (1 if c.is_noop else 0, _source_priority(c.source, preferred_source), c.text))


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


def _add_preferred_conversion(candidates: list[Candidate], current_text: str, preferred_source: str) -> None:
    if preferred_source == "big5-carrier-to-gbk":
        _try_add_conversion(candidates, current_text, "cp950", "gbk", preferred_source)
    elif preferred_source == "gbk-carrier-to-big5":
        _try_add_conversion(candidates, current_text, "gbk", "cp950", preferred_source)
    elif preferred_source == "utf8-carrier-to-gbk":
        _try_add_conversion(candidates, current_text, "utf-8", "gbk", preferred_source)
    elif preferred_source == "gbk-carrier-to-utf8":
        _try_add_conversion(candidates, current_text, "gbk", "utf-8", preferred_source)


def _evidence_preferred_source(context: dict) -> str:
    evidence = context.get("nativeDecodeEvidence") if isinstance(context.get("nativeDecodeEvidence"), dict) else {}
    source = str(context.get("nativeDecodeSourceCodePageFamily") or evidence.get("sourceCodePageFamily") or "").lower()
    applied = str(context.get("nativeDecodeAppliedCodePageFamily") or evidence.get("appliedCodePageFamily") or "").lower()
    if "big5" in source and "gbk" in applied:
        return "gbk-carrier-to-big5"
    if "gbk" in source and "big5" in applied:
        return "big5-carrier-to-gbk"
    if "utf8" in source and "gbk" in applied:
        return "gbk-carrier-to-utf8"
    if "gbk" in source and "utf8" in applied:
        return "utf8-carrier-to-gbk"
    return ""


def _source_priority(source: str, preferred_source: str) -> int:
    if "hook-raw-stream" in (source or "").lower():
        return 0
    if not preferred_source:
        return 1
    return 0 if preferred_source.lower() in (source or "").lower() else 1


def _add_hook_raw_candidate(candidates: list[Candidate], current_text: str, context: dict) -> None:
    evidence = context.get("nativeDecodeEvidence") if isinstance(context.get("nativeDecodeEvidence"), dict) else {}
    has_raw = bool(context.get("hasHookRawDecodeEvidence") or evidence.get("hasHookRawDecodeEvidence"))
    text = str(context.get("hookPreferredDecodedText") or evidence.get("hookPreferredDecodedText") or "")
    if not has_raw or not text or text == current_text:
        return

    source = str(context.get("hookRawCandidateSource") or evidence.get("hookRawCandidateSource") or "hook-raw-stream")
    if "hook-raw-stream" not in source.lower():
        source = "hook-raw-stream+" + source
    roundtrip = bool(context.get("hookRawRoundTrip") or evidence.get("hookRawRoundTrip"))
    reason = "hook-raw-roundtrip-ok" if roundtrip else "hook-raw-derived"
    length = context.get("hookRawPayloadLength") or evidence.get("hookRawPayloadLength")
    if length:
        reason += f"; raw-len={length}"
    _add_candidate(candidates, text, source, reason, roundtrip)


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
