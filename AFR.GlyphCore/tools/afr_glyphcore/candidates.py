from __future__ import annotations

import unicodedata
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
    actual_context = context or {}
    preferred_source = _evidence_preferred_source(actual_context)
    allow_private_use_prefix_cleanup = _has_native_decode_mismatch(actual_context)
    _add_hook_raw_candidate(candidates, current_text, actual_context, allow_private_use_prefix_cleanup)
    _add_preferred_conversion(candidates, current_text, preferred_source, allow_private_use_prefix_cleanup)

    if preferred_source != "big5-carrier-to-gbk":
        _try_add_conversion(candidates, current_text, "cp950", "gbk", "big5-carrier-to-gbk", allow_private_use_prefix_cleanup)
    if preferred_source != "gbk-carrier-to-big5":
        _try_add_conversion(candidates, current_text, "gbk", "cp950", "gbk-carrier-to-big5", allow_private_use_prefix_cleanup)
    if preferred_source != "utf8-carrier-to-gbk":
        _try_add_conversion(candidates, current_text, "utf-8", "gbk", "utf8-carrier-to-gbk", allow_private_use_prefix_cleanup)
    if preferred_source != "gbk-carrier-to-utf8":
        _try_add_conversion(candidates, current_text, "gbk", "utf-8", "gbk-carrier-to-utf8", allow_private_use_prefix_cleanup)
    if not _has_strong_decoded_repair_candidate(candidates, preferred_source):
        _add_private_use_prefix_cleanup_candidate(
            candidates,
            current_text,
            "private-use-prefix-space-fill",
            "native-evidence-leading-private-use-placeholder",
            allow_private_use_prefix_cleanup,
        )
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
    allow_private_use_prefix_cleanup: bool,
) -> None:
    if not current_text:
        return

    try:
        carrier_bytes = _encode_carrier(current_text, carrier_encoding)
        decoded = carrier_bytes.decode(target_encoding, errors="strict")
        if not decoded or decoded == current_text:
            return

        candidate_bytes = decoded.encode(target_encoding, errors="strict")
        roundtrip = (
            carrier_bytes == candidate_bytes
            and _decode_carrier(candidate_bytes, carrier_encoding) == current_text
        )
        reason = "roundtrip-ok" if roundtrip else "roundtrip-failed"
    except UnicodeError:
        return

    _add_candidate(candidates, decoded, source, reason, roundtrip)
    _add_private_use_punctuation_carryover_candidate(
        candidates,
        current_text,
        decoded,
        source + "+private-use-punctuation-carryover",
        reason + "; private-use-punctuation-carryover",
        allow_private_use_prefix_cleanup and roundtrip,
    )
    _add_private_use_prefix_cleanup_candidate(
        candidates,
        decoded,
        source + "+private-use-prefix-space-fill",
        reason + "; leading-private-use-placeholder",
        allow_private_use_prefix_cleanup and roundtrip,
    )


def _add_preferred_conversion(
    candidates: list[Candidate],
    current_text: str,
    preferred_source: str,
    allow_private_use_prefix_cleanup: bool,
) -> None:
    if preferred_source == "big5-carrier-to-gbk":
        _try_add_conversion(candidates, current_text, "cp950", "gbk", preferred_source, allow_private_use_prefix_cleanup)
    elif preferred_source == "gbk-carrier-to-big5":
        _try_add_conversion(candidates, current_text, "gbk", "cp950", preferred_source, allow_private_use_prefix_cleanup)
    elif preferred_source == "utf8-carrier-to-gbk":
        _try_add_conversion(candidates, current_text, "utf-8", "gbk", preferred_source, allow_private_use_prefix_cleanup)
    elif preferred_source == "gbk-carrier-to-utf8":
        _try_add_conversion(candidates, current_text, "gbk", "utf-8", preferred_source, allow_private_use_prefix_cleanup)


def _evidence_preferred_source(context: dict) -> str:
    evidence = context.get("nativeDecodeEvidence") if isinstance(context.get("nativeDecodeEvidence"), dict) else {}
    source = str(context.get("nativeDecodeSourceCodePageFamily") or evidence.get("sourceCodePageFamily") or "").lower()
    applied = str(context.get("nativeDecodeAppliedCodePageFamily") or evidence.get("appliedCodePageFamily") or "").lower()
    if "big5" in source and "gbk" in applied:
        return "big5-carrier-to-gbk"
    if "gbk" in source and "big5" in applied:
        return "gbk-carrier-to-big5"
    if "utf8" in source and "gbk" in applied:
        return "utf8-carrier-to-gbk"
    if "gbk" in source and "utf8" in applied:
        return "gbk-carrier-to-utf8"
    return ""


def _source_priority(source: str, preferred_source: str) -> int:
    if "hook-raw-stream" in (source or "").lower():
        return 0
    if not preferred_source:
        return 1
    return 0 if preferred_source.lower() in (source or "").lower() else 1


def _has_strong_decoded_repair_candidate(candidates: list[Candidate], preferred_source: str) -> bool:
    for candidate in candidates:
        source = (candidate.source or "").lower()
        if candidate.is_noop or not candidate.is_roundtrip:
            continue
        if "hook-raw-stream" in source:
            return True
        if preferred_source and preferred_source.lower() in source:
            return True
    return False


def _encode_carrier(text: str, encoding: str) -> bytes:
    if encoding.lower() != "cp950":
        return text.encode(encoding, errors="strict")

    payload = bytearray()
    for char in text:
        mapped = _encode_windows_cp950_private_use(char)
        if mapped is not None:
            payload.extend(mapped)
            continue
        payload.extend(char.encode(encoding, errors="strict"))
    return bytes(payload)


def _decode_carrier(payload: bytes, encoding: str) -> str:
    if encoding.lower() != "cp950":
        return payload.decode(encoding, errors="strict")

    parts: list[str] = []
    segment = bytearray()
    index = 0

    def flush_segment() -> None:
        if not segment:
            return
        parts.append(bytes(segment).decode(encoding, errors="strict"))
        segment.clear()

    while index < len(payload):
        if index + 1 < len(payload):
            mapped = _decode_windows_cp950_private_use_pair(payload[index], payload[index + 1])
            if mapped:
                flush_segment()
                parts.append(mapped)
                index += 2
                continue

        segment.append(payload[index])
        index += 1

    flush_segment()
    return "".join(parts)


def _encode_windows_cp950_private_use(char: str) -> bytes | None:
    code = ord(char)
    # Windows/.NET CP950 maps the ETEN private-use range U+F6B1..U+F7CA
    # onto Big5 bytes C6A1..C8FE. Python's cp950 codec does not expose
    # that mapping, but CAD/.NET uses it during runtime candidate generation.
    if code < 0xF6B1 or code > 0xF7CA:
        return None

    offset = code - 0xF6B1
    lead = 0xC6 + offset // 94
    trail = 0xA1 + offset % 94
    return bytes((lead, trail))


def _decode_windows_cp950_private_use_pair(lead: int, trail: int) -> str:
    if lead < 0xC6 or lead > 0xC8 or trail < 0xA1 or trail > 0xFE:
        return ""

    return chr(0xF6B1 + ((lead - 0xC6) * 94) + (trail - 0xA1))


def _add_hook_raw_candidate(
    candidates: list[Candidate],
    current_text: str,
    context: dict,
    allow_private_use_prefix_cleanup: bool,
) -> None:
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
    _add_private_use_punctuation_carryover_candidate(
        candidates,
        current_text,
        text,
        source + "+private-use-punctuation-carryover",
        reason + "; private-use-punctuation-carryover",
        allow_private_use_prefix_cleanup and roundtrip,
    )
    _add_private_use_prefix_cleanup_candidate(
        candidates,
        text,
        source + "+private-use-prefix-space-fill",
        reason + "; leading-private-use-placeholder",
        allow_private_use_prefix_cleanup and roundtrip,
    )


def _add_private_use_prefix_cleanup_candidate(
    candidates: list[Candidate],
    text: str,
    source: str,
    reason: str,
    allow_private_use_prefix_cleanup: bool,
) -> None:
    if not allow_private_use_prefix_cleanup:
        return
    candidate = _replace_leading_private_use_placeholders_with_spaces(text)
    if not candidate:
        return
    _add_candidate(candidates, candidate, source, reason, True)


def _add_private_use_punctuation_carryover_candidate(
    candidates: list[Candidate],
    current_text: str,
    text: str,
    source: str,
    reason: str,
    allow_private_use_punctuation_carryover: bool,
) -> None:
    if not allow_private_use_punctuation_carryover:
        return
    candidate = _replace_interior_private_use_with_current_punctuation(current_text, text)
    if not candidate:
        return
    _add_candidate(candidates, candidate, source, reason, True)


def _replace_leading_private_use_placeholders_with_spaces(text: str) -> str:
    if not text:
        return ""

    prefix_length = 0
    while prefix_length < len(text) and prefix_length < 4 and _is_private_use(text[prefix_length]):
        prefix_length += 1

    if prefix_length == 0 or prefix_length >= len(text):
        return ""
    if prefix_length < len(text) and _is_private_use(text[prefix_length]):
        return ""

    visible_text = text[prefix_length:]
    if not visible_text.strip() or not _has_meaningful_visible_text(visible_text):
        return ""
    return (" " * prefix_length) + visible_text


def _replace_interior_private_use_with_current_punctuation(current_text: str, text: str) -> str:
    if not current_text or not text or len(current_text) != len(text) or _is_private_use(text[0]):
        return ""

    chars = list(text)
    replacements = 0
    for index, char in enumerate(chars):
        if not _is_private_use(char):
            continue

        carryover = current_text[index]
        if not _is_carryover_punctuation(carryover):
            return ""

        chars[index] = carryover
        replacements += 1

    if replacements == 0:
        return ""

    candidate = "".join(chars)
    if not _has_meaningful_visible_text(candidate):
        return ""
    return candidate


def _is_carryover_punctuation(char: str) -> bool:
    if (
        not char
        or char.isspace()
        or unicodedata.category(char) == "Cc"
        or _is_private_use(char)
        or char.isalnum()
    ):
        return False

    return unicodedata.category(char) in {
        "Pc",
        "Pd",
        "Ps",
        "Pe",
        "Pi",
        "Pf",
        "Po",
        "Sm",
        "Sk",
        "So",
    }


def _has_meaningful_visible_text(text: str) -> bool:
    saw_visible = False
    for char in text:
        if char.isspace() or unicodedata.category(char) == "Cc":
            continue
        category = unicodedata.category(char)
        if category in {"Co", "Cs", "Cn"}:
            return False
        saw_visible = True
        if char.isalnum() or category in {"Lo", "Nl", "Nd"}:
            return True
    return saw_visible and len(text) <= 2


def _has_native_decode_mismatch(context: dict) -> bool:
    evidence = context.get("nativeDecodeEvidence") if isinstance(context.get("nativeDecodeEvidence"), dict) else {}
    has_evidence = bool(context.get("hasNativeDecodeEvidence") or evidence.get("hasEvidence"))
    mismatch = bool(context.get("nativeDecodeFamilyMismatch") or evidence.get("familyMismatch"))
    return has_evidence and mismatch


def _is_private_use(char: str) -> bool:
    return unicodedata.category(char) == "Co" or "\ue000" <= char <= "\uf8ff"


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
