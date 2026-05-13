from __future__ import annotations

import unicodedata
from dataclasses import dataclass

from . import FEATURE_COUNT, FEATURE_SCHEMA_VERSION
from .candidates import Candidate


FEATURE_NAMES = [
    "current_len_norm",
    "candidate_len_norm",
    "abs_len_delta_norm",
    "text_equal",
    "candidate_empty",
    "current_cjk_ratio",
    "candidate_cjk_ratio",
    "current_ascii_ratio",
    "candidate_ascii_ratio",
    "current_control_ratio",
    "candidate_control_ratio",
    "current_replacement_ratio",
    "candidate_replacement_ratio",
    "current_private_use_ratio",
    "candidate_private_use_ratio",
    "current_digit_ratio",
    "candidate_digit_ratio",
    "current_punctuation_ratio",
    "candidate_punctuation_ratio",
    "current_symbol_ratio",
    "candidate_symbol_ratio",
    "current_bopomofo_or_kana_ratio",
    "candidate_bopomofo_or_kana_ratio",
    "normalized_edit_distance",
    "character_overlap",
    "candidate_roundtrip",
    "source_has_big5",
    "source_has_gbk",
    "source_has_utf8",
    "source_has_current",
    "source_has_safe",
    "current_cad_keyword_ratio",
    "candidate_cad_keyword_ratio",
    "candidate_cjk_ratio_improved",
    "candidate_has_control",
    "candidate_has_replacement",
    "candidate_has_private_use",
    "candidate_has_suspicious_unicode",
    "current_has_suspicious_unicode",
    "is_from_xref",
    "layer_hash01",
    "owner_block_hash01",
    "text_style_hash01",
    "font_hash01",
    "bigfont_hash01",
    "typeface_hash01",
    "known_cad_text_style",
    "known_cad_font",
    "known_cad_bigfont",
    "candidate_length_le_1",
    "current_length_le_1",
    "length_risk",
    "candidate_loses_cjk",
    "candidate_becomes_ascii_from_nonascii",
    "candidate_mostly_symbols",
    "current_mostly_symbols",
]


if len(FEATURE_NAMES) != FEATURE_COUNT:
    raise RuntimeError("feature name count does not match runtime feature count")


@dataclass
class TextStats:
    cjk_ratio: float = 0.0
    ascii_ratio: float = 0.0
    control_ratio: float = 0.0
    replacement_ratio: float = 0.0
    private_use_ratio: float = 0.0
    digit_ratio: float = 0.0
    punctuation_ratio: float = 0.0
    symbol_ratio: float = 0.0
    bopomofo_or_kana_ratio: float = 0.0


def extract_features(context: dict, candidate: Candidate) -> list[float]:
    current = str(context.get("currentText") or "")
    candidate_text = candidate.text or ""
    current_stats = analyze(current)
    candidate_stats = analyze(candidate_text)

    features = [0.0] * FEATURE_COUNT
    features[0] = norm(len(current), 64)
    features[1] = norm(len(candidate_text), 64)
    features[2] = norm(abs(len(current) - len(candidate_text)), 32)
    features[3] = boolf(current == candidate_text)
    features[4] = boolf(candidate_text == "")
    features[5] = current_stats.cjk_ratio
    features[6] = candidate_stats.cjk_ratio
    features[7] = current_stats.ascii_ratio
    features[8] = candidate_stats.ascii_ratio
    features[9] = current_stats.control_ratio
    features[10] = candidate_stats.control_ratio
    features[11] = current_stats.replacement_ratio
    features[12] = candidate_stats.replacement_ratio
    features[13] = current_stats.private_use_ratio
    features[14] = candidate_stats.private_use_ratio
    features[15] = current_stats.digit_ratio
    features[16] = candidate_stats.digit_ratio
    features[17] = current_stats.punctuation_ratio
    features[18] = candidate_stats.punctuation_ratio
    features[19] = current_stats.symbol_ratio
    features[20] = candidate_stats.symbol_ratio
    features[21] = current_stats.bopomofo_or_kana_ratio
    features[22] = candidate_stats.bopomofo_or_kana_ratio
    features[23] = normalized_edit_distance(current, candidate_text)
    features[24] = character_overlap(current, candidate_text)
    features[25] = boolf(candidate.is_roundtrip)
    features[26] = contains_source(candidate.source, "big5")
    features[27] = contains_source(candidate.source, "gbk")
    features[28] = contains_source(candidate.source, "utf8")
    features[29] = contains_source(candidate.source, "current")
    features[30] = contains_source(candidate.source, "safe")
    features[31] = cad_keyword_ratio(current)
    features[32] = cad_keyword_ratio(candidate_text)
    features[33] = boolf(candidate_stats.cjk_ratio > current_stats.cjk_ratio)
    features[34] = boolf(candidate_stats.control_ratio > 0)
    features[35] = boolf(candidate_stats.replacement_ratio > 0)
    features[36] = boolf(candidate_stats.private_use_ratio > 0)
    features[37] = boolf(has_suspicious_unicode(candidate_text))
    features[38] = boolf(has_suspicious_unicode(current))
    features[39] = boolf(bool(context.get("isFromExternalReference", False)))
    features[40] = stable_hash01(str(context.get("layer") or ""))
    features[41] = stable_hash01(str(context.get("ownerBlockName") or ""))
    features[42] = stable_hash01(str(context.get("textStyleName") or ""))
    features[43] = stable_hash01(str(context.get("textStyleFileName") or ""))
    features[44] = stable_hash01(str(context.get("textStyleBigFontFileName") or ""))
    features[45] = stable_hash01(str(context.get("textStyleTypeFace") or ""))
    features[46] = boolf(is_known_cad_text_style(str(context.get("textStyleName") or "")))
    features[47] = boolf(is_known_cad_font(str(context.get("textStyleFileName") or "")))
    features[48] = boolf(is_known_cad_font(str(context.get("textStyleBigFontFileName") or "")))
    features[49] = boolf(len(candidate_text) <= 1)
    features[50] = boolf(len(current) <= 1)
    features[51] = boolf(length_risk(current, candidate_text))
    features[52] = boolf(candidate_stats.cjk_ratio < current_stats.cjk_ratio and current_stats.cjk_ratio > 0.2)
    features[53] = boolf(candidate_stats.ascii_ratio > 0.9 and current_stats.ascii_ratio < 0.5)
    features[54] = boolf(is_mostly_symbols(candidate_stats))
    features[55] = boolf(is_mostly_symbols(current_stats))
    return features


def analyze(text: str) -> TextStats:
    if not text:
        return TextStats()

    cjk = 0
    ascii_count = 0
    control = 0
    replacement = 0
    private_use = 0
    digit = 0
    punctuation = 0
    symbol = 0
    bopomofo_or_kana = 0

    for ch in text:
        code = ord(ch)
        if code <= 0x7F:
            ascii_count += 1
        if unicodedata.category(ch) == "Cc":
            control += 1
        if ch == "\uFFFD":
            replacement += 1
        if ch.isdigit():
            digit += 1
        if is_cjk(ch):
            cjk += 1
        if is_private_use(ch):
            private_use += 1
        if is_bopomofo_or_kana(ch):
            bopomofo_or_kana += 1

        category = unicodedata.category(ch)
        if category in {"Po", "Pc", "Pd", "Ps", "Pe", "Pi", "Pf"}:
            punctuation += 1
        if category in {"Sm", "Sc", "Sk", "So"}:
            symbol += 1

    length = max(1, len(text))
    return TextStats(
        cjk_ratio=cjk / length,
        ascii_ratio=ascii_count / length,
        control_ratio=control / length,
        replacement_ratio=replacement / length,
        private_use_ratio=private_use / length,
        digit_ratio=digit / length,
        punctuation_ratio=punctuation / length,
        symbol_ratio=symbol / length,
        bopomofo_or_kana_ratio=bopomofo_or_kana / length,
    )


def has_unsafe_text(text: str) -> bool:
    stats = analyze(text)
    return stats.control_ratio > 0 or stats.replacement_ratio > 0 or has_suspicious_unicode(text)


def cad_keyword_ratio(text: str) -> float:
    if not text:
        return 0.0
    keywords = "水管井泵阀风压流排污喷淋消防电气设备材料表房库层标高详见安装系统屋顶支架压力自动"
    return sum(1 for ch in text if ch in keywords) / max(1, len(text))


def normalized_edit_distance(left: str, right: str) -> float:
    maximum = max(len(left or ""), len(right or ""))
    if maximum == 0:
        return 0.0
    return min(1.0, levenshtein(left or "", right or "") / maximum)


def levenshtein(left: str, right: str) -> int:
    previous = list(range(len(right) + 1))
    current = [0] * (len(right) + 1)

    for i, left_ch in enumerate(left, start=1):
        current[0] = i
        for j, right_ch in enumerate(right, start=1):
            cost = 0 if left_ch == right_ch else 1
            current[j] = min(current[j - 1] + 1, previous[j] + 1, previous[j - 1] + cost)
        previous, current = current, previous

    return previous[len(right)]


def character_overlap(left: str, right: str) -> float:
    if not left or not right:
        return 0.0
    intersection = sum(1 for ch in left if ch in right)
    return intersection / max(1, max(len(left), len(right)))


def stable_hash01(text: str) -> float:
    if not text:
        return 0.0
    value = 2166136261
    for byte in text.encode("utf-8"):
        value ^= byte
        value = (value * 16777619) & 0xFFFFFFFF
    return (value & 0xFFFF) / 65535.0


def has_suspicious_unicode(text: str) -> bool:
    for ch in text or "":
        if unicodedata.category(ch) in {"Cs", "Cn"}:
            return True
    return False


def length_risk(current: str, candidate: str) -> bool:
    current_length = len(current or "")
    candidate_length = len(candidate or "")
    if current_length == 0 or candidate_length == 0:
        return True
    return candidate_length > current_length * 4 or current_length > candidate_length * 4


def is_mostly_symbols(stats: TextStats) -> bool:
    return stats.symbol_ratio + stats.punctuation_ratio > 0.75


def is_known_cad_text_style(text: str) -> bool:
    if not text:
        return False
    upper = text.upper()
    return "HZTXT" in upper or "TXT" in upper or "TEXT" in upper


def is_known_cad_font(text: str) -> bool:
    if not text:
        return False
    lower = text.lower()
    upper = text.upper()
    return lower.endswith(".shx") or "GB" in upper or "TSSD" in upper or "TXT" in upper


def contains_source(source: str, token: str) -> float:
    return boolf(bool(source) and token.lower() in source.lower())


def boolf(value: bool) -> float:
    return 1.0 if value else 0.0


def norm(value: int, scale: int) -> float:
    return min(1.0, value / max(1, scale))


def is_cjk(ch: str) -> bool:
    code = ord(ch)
    return (
        0x3400 <= code <= 0x4DBF
        or 0x4E00 <= code <= 0x9FFF
        or 0xF900 <= code <= 0xFAFF
    )


def is_private_use(ch: str) -> bool:
    return 0xE000 <= ord(ch) <= 0xF8FF


def is_bopomofo_or_kana(ch: str) -> bool:
    code = ord(ch)
    return 0x3040 <= code <= 0x30FF or 0x3100 <= code <= 0x312F


__all__ = [
    "FEATURE_COUNT",
    "FEATURE_NAMES",
    "FEATURE_SCHEMA_VERSION",
    "extract_features",
    "has_unsafe_text",
]
