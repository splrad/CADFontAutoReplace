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
    "source_from_big5",
    "source_from_gbk",
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
    # v2 新增特征
    "candidate_len_ratio",
    "current_cjk_count_norm",
    "candidate_cjk_count_norm",
    "font_is_gbk_family",
    "font_is_big5_family",
    "candidate_source_convergence",
    # v3 Hook evidence features
    "native_decode_evidence_present",
    "native_decode_family_mismatch",
    "native_decode_scope_object",
    "native_decode_scope_cluster",
    "native_decode_scope_ripple",
    "native_decode_source_big5",
    "native_decode_source_gbk",
    "native_decode_applied_big5",
    "native_decode_applied_gbk",
    "native_decode_object_correlation",
    "native_decode_cluster_correlation",
    "native_decode_hook_hit_dbtext",
    "ldfile_font_evidence",
    "ripple_seed_count_norm",
    "ripple_context_cjk_ratio",
    "evidence_aligned_candidate",
    # v4 engineering semantics, raw Hook candidate evidence, and ripple quality
    "current_simplified_engineering_chinese_ratio",
    "candidate_simplified_engineering_chinese_ratio",
    "candidate_simplified_engineering_chinese_improved",
    "current_traditional_or_rare_cjk_ratio",
    "candidate_traditional_or_rare_cjk_ratio",
    "candidate_reduces_traditional_or_rare_cjk",
    "layer_engineering_keyword_ratio",
    "layer_candidate_keyword_overlap",
    "candidate_preserves_ascii_tokens",
    "candidate_preserves_engineering_symbols",
    "current_ascii_token_count_norm",
    "candidate_ascii_token_count_norm",
    "current_engineering_symbol_count_norm",
    "candidate_engineering_symbol_count_norm",
    "hook_raw_payload_present",
    "hook_raw_preferred_candidate",
    "hook_raw_roundtrip_ok",
    "hook_raw_confidence",
    "ripple_seed_quality",
    "ripple_distance_ratio",
    # v6 supervised lexical identity features for human-confirmed exceptions.
    "current_text_hash01",
    "candidate_text_hash01",
    "current_candidate_source_hash01",
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
    features[26] = source_from(candidate.source, "big5")
    features[27] = source_from(candidate.source, "gbk")
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
    # v2 新增特征
    features[56] = candidate_len_ratio(len(current), len(candidate_text))
    features[57] = norm(cjk_count(current), 8)
    features[58] = norm(cjk_count(candidate_text), 8)
    features[59] = boolf(is_font_gbk_family(str(context.get("textStyleFileName") or ""), str(context.get("textStyleBigFontFileName") or "")))
    features[60] = boolf(is_font_big5_family(str(context.get("textStyleFileName") or ""), str(context.get("textStyleBigFontFileName") or "")))
    features[61] = candidate_source_convergence(candidate.source)
    evidence = native_decode_evidence(context)
    ripple_context = str(context.get("rippleContextText") or evidence.get("rippleContextText") or "")
    ripple_stats = analyze(ripple_context)
    features[62] = boolf(bool_value(context, evidence, "hasNativeDecodeEvidence", "hasEvidence"))
    features[63] = boolf(bool_value(context, evidence, "nativeDecodeFamilyMismatch", "familyMismatch"))
    features[64] = boolf(has_token(str_value(context, evidence, "nativeDecodeEvidenceScope", "scope"), "object"))
    features[65] = boolf(has_token(str_value(context, evidence, "nativeDecodeEvidenceScope", "scope"), "cluster"))
    features[66] = boolf(has_token(str_value(context, evidence, "nativeDecodeEvidenceScope", "scope"), "ripple"))
    features[67] = boolf(has_token(str_value(context, evidence, "nativeDecodeSourceCodePageFamily", "sourceCodePageFamily"), "big5"))
    features[68] = boolf(has_token(str_value(context, evidence, "nativeDecodeSourceCodePageFamily", "sourceCodePageFamily"), "gbk"))
    features[69] = boolf(has_token(str_value(context, evidence, "nativeDecodeAppliedCodePageFamily", "appliedCodePageFamily"), "big5"))
    features[70] = boolf(has_token(str_value(context, evidence, "nativeDecodeAppliedCodePageFamily", "appliedCodePageFamily"), "gbk"))
    features[71] = clamp01(float_value(context, evidence, "nativeDecodeObjectCorrelation", "objectCorrelation"))
    features[72] = clamp01(float_value(context, evidence, "nativeDecodeClusterCorrelation", "clusterCorrelation"))
    features[73] = boolf(has_token(str_value(context, evidence, "nativeDecodeHookHitType", "hookHitType"), "dbtext"))
    features[74] = boolf(bool_value(context, evidence, "hasLdFileFontEvidence", "hasLdFileFontEvidence"))
    features[75] = norm(int(float_value(context, evidence, "rippleSeedCount", "rippleSeedCount")), 8)
    features[76] = ripple_stats.cjk_ratio
    features[77] = boolf(is_evidence_aligned_candidate(context, evidence, candidate.source))
    features[78] = simplified_engineering_chinese_ratio(current)
    features[79] = simplified_engineering_chinese_ratio(candidate_text)
    features[80] = boolf(simplified_engineering_chinese_ratio(candidate_text) > simplified_engineering_chinese_ratio(current))
    features[81] = traditional_or_rare_cjk_ratio(current)
    features[82] = traditional_or_rare_cjk_ratio(candidate_text)
    features[83] = boolf(traditional_or_rare_cjk_ratio(candidate_text) < traditional_or_rare_cjk_ratio(current))
    features[84] = engineering_keyword_ratio(str(context.get("layer") or ""))
    features[85] = layer_candidate_keyword_overlap(str(context.get("layer") or ""), candidate_text)
    features[86] = boolf(preserves_ascii_tokens(current, candidate_text))
    features[87] = engineering_symbol_preservation(current, candidate_text)
    features[88] = norm(ascii_token_count(current), 8)
    features[89] = norm(ascii_token_count(candidate_text), 8)
    features[90] = norm(engineering_symbol_count(current), 8)
    features[91] = norm(engineering_symbol_count(candidate_text), 8)
    features[92] = boolf(bool_value(context, evidence, "hasHookRawDecodeEvidence", "hasHookRawDecodeEvidence") and int(float_value(context, evidence, "hookRawPayloadLength", "hookRawPayloadLength")) > 0)
    features[93] = boolf(is_hook_raw_preferred_candidate(context, evidence, candidate_text, candidate.source))
    features[94] = boolf(bool_value(context, evidence, "hookRawRoundTrip", "hookRawRoundTrip"))
    features[95] = clamp01(float_value(context, evidence, "hookRawConfidence", "hookRawConfidence"))
    features[96] = clamp01(float_value(context, evidence, "rippleSeedQuality", "rippleSeedQuality"))
    features[97] = clamp01(float_value(context, evidence, "rippleDistanceRatio", "rippleDistanceRatio"))
    features[98] = stable_hash01(current)
    features[99] = stable_hash01(candidate_text)
    features[100] = stable_hash01(f"{current}\0{candidate_text}\0{candidate.source}")
    return features


def native_decode_evidence(context: dict) -> dict:
    value = context.get("nativeDecodeEvidence")
    return value if isinstance(value, dict) else {}


def bool_value(context: dict, evidence: dict, flat_key: str, nested_key: str) -> bool:
    if flat_key in context:
        return bool(context.get(flat_key))
    return bool(evidence.get(nested_key))


def str_value(context: dict, evidence: dict, flat_key: str, nested_key: str) -> str:
    return str(context.get(flat_key) or evidence.get(nested_key) or "")


def float_value(context: dict, evidence: dict, flat_key: str, nested_key: str) -> float:
    value = context.get(flat_key)
    if value is None:
        value = evidence.get(nested_key)
    try:
        return float(value or 0.0)
    except (TypeError, ValueError):
        return 0.0


def is_evidence_aligned_candidate(context: dict, evidence: dict, source: str) -> bool:
    source_family = str_value(context, evidence, "nativeDecodeSourceCodePageFamily", "sourceCodePageFamily")
    applied_family = str_value(context, evidence, "nativeDecodeAppliedCodePageFamily", "appliedCodePageFamily")
    lower_source = (source or "").lower()
    if has_token(source_family, "big5") and has_token(applied_family, "gbk"):
        return "gbk-carrier-to-big5" in lower_source
    if has_token(source_family, "gbk") and has_token(applied_family, "big5"):
        return "big5-carrier-to-gbk" in lower_source
    if has_token(source_family, "utf8") and has_token(applied_family, "gbk"):
        return "gbk-carrier-to-utf8" in lower_source
    if has_token(source_family, "gbk") and has_token(applied_family, "utf8"):
        return "utf8-carrier-to-gbk" in lower_source
    return False


def candidate_len_ratio(current_len: int, candidate_len: int) -> float:
    if current_len == 0:
        return 1.0
    return min(1.0, (candidate_len / current_len) / 4.0)


def cjk_count(text: str) -> int:
    return sum(1 for ch in (text or "") if is_cjk(ch))


def is_font_gbk_family(font: str, bigfont: str) -> bool:
    tokens = ("gb", "gbcbig", "hztxt", "tssd")
    lower_font = font.lower()
    lower_big = bigfont.lower()
    return any(t in lower_font or t in lower_big for t in tokens)


def is_font_big5_family(font: str, bigfont: str) -> bool:
    tokens = ("big5", "hzbig5", "cns")
    lower_font = font.lower()
    lower_big = bigfont.lower()
    return any(t in lower_font or t in lower_big for t in tokens)


def candidate_source_convergence(source: str) -> float:
    if not source:
        return 0.0
    return norm(1 + source.count("+"), 3)


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


def simplified_engineering_chinese_ratio(text: str) -> float:
    if not text:
        return 0.0
    cjk_chars = [ch for ch in text if is_cjk(ch)]
    if not cjk_chars:
        return 0.0
    return sum(1 for ch in cjk_chars if is_simplified_engineering_char(ch)) / max(1, len(cjk_chars))


def traditional_or_rare_cjk_ratio(text: str) -> float:
    if not text:
        return 0.0
    return sum(1 for ch in text if is_traditional_or_rare_cjk(ch)) / max(1, len(text))


def engineering_keyword_ratio(text: str) -> float:
    if not text:
        return 0.0
    upper = text.upper()
    tokens = [
        "WATER",
        "DRAIN",
        "PIPE",
        "FIRE",
        "HVAC",
        "ELEC",
        "TEXT",
        "DIM",
        "给水",
        "排水",
        "消防",
        "喷淋",
        "电气",
        "风管",
        "暖通",
        "结构",
        "建筑",
        "标注",
        "水",
        "管",
        "阀",
        "泵",
        "风",
        "电",
        "层",
        "井",
        "标高",
    ]
    return norm(sum(1 for token in tokens if token.upper() in upper), 6)


def layer_candidate_keyword_overlap(layer: str, candidate: str) -> float:
    layer_chars = engineering_chars(layer)
    if not layer_chars or not candidate:
        return 0.0
    return sum(1 for ch in candidate if ch in layer_chars) / max(1, len(layer_chars))


def preserves_ascii_tokens(current: str, candidate: str) -> bool:
    tokens = extract_ascii_tokens(current)
    if not tokens:
        return True
    preserved = sum(1 for token in tokens if token.lower() in (candidate or "").lower())
    return preserved >= max(1, len(tokens) - 1)


def engineering_symbol_preservation(current: str, candidate: str) -> float:
    symbols = [ch for ch in current or "" if is_engineering_symbol(ch)]
    if not symbols:
        return 1.0
    return sum(1 for ch in symbols if ch in (candidate or "")) / max(1, len(symbols))


def ascii_token_count(text: str) -> int:
    return len(extract_ascii_tokens(text))


def engineering_symbol_count(text: str) -> int:
    return sum(1 for ch in text or "" if is_engineering_symbol(ch))


def is_hook_raw_preferred_candidate(context: dict, evidence: dict, candidate_text: str, candidate_source: str) -> bool:
    if not bool_value(context, evidence, "hasHookRawDecodeEvidence", "hasHookRawDecodeEvidence"):
        return False
    if "hook-raw-stream" in (candidate_source or "").lower():
        return True
    preferred = str_value(context, evidence, "hookPreferredDecodedText", "hookPreferredDecodedText")
    return bool(preferred) and preferred == candidate_text


def engineering_chars(text: str) -> set[str]:
    keywords = "水管井泵阀风压流排污喷淋消防电气设备材料表房库层标高详见安装系统屋顶支架压力自动给排暖通建筑结构标注"
    return {ch for ch in text or "" if ch in keywords}


def extract_ascii_tokens(text: str) -> list[str]:
    tokens: list[str] = []
    current: list[str] = []
    for ch in text or "":
        if is_ascii_token_char(ch):
            current.append(ch)
        else:
            if len(current) >= 2:
                tokens.append("".join(current))
            current = []
    if len(current) >= 2:
        tokens.append("".join(current))
    return tokens


def is_ascii_token_char(ch: str) -> bool:
    return ch.isascii() and (ch.isalnum() or ch in "+-()./")


def is_engineering_symbol(ch: str) -> bool:
    return ch in "+-()./×xXΦφ%#@=<>≤≥±°"


def is_simplified_engineering_char(ch: str) -> bool:
    simplified = "检宽顶图层风阀喷淋电气设备材料库给压流排污消防标高详见安装系统屋顶支架自动泵管水井房"
    return ch in simplified or cad_keyword_ratio(ch) > 0


def is_traditional_or_rare_cjk(ch: str) -> bool:
    traditional = "檢寬頂圖層風閥噴電氣設備給壓詳見築標號號體體臺臺"
    return ch in traditional


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


def source_from(source: str, family: str) -> float:
    lower = (source or "").lower()
    return boolf(lower.startswith(f"{family.lower()}-carrier-to-") or f"+{family.lower()}-carrier-to-" in lower)


def has_token(value: str, token: str) -> bool:
    return bool(value) and token.lower() in value.lower()


def boolf(value: bool) -> float:
    return 1.0 if value else 0.0


def clamp01(value: float) -> float:
    return max(0.0, min(1.0, value))


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
