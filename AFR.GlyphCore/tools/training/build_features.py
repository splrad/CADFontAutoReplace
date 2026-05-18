from __future__ import annotations

import argparse
import csv
import json
import re
import sys
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from afr_glyphcore.candidates import Candidate  # noqa: E402
from afr_glyphcore.features import FEATURE_NAMES, FEATURE_SCHEMA_VERSION, extract_features  # noqa: E402


METADATA_COLUMNS = [
    "schema",
    "feature_schema",
    "group_id",
    "candidate_index",
    "label_action",
    "target_score",
    "is_positive",
    "is_noop",
    "is_roundtrip",
    "source",
    "reason",
    "current_text",
    "candidate_text",
    "label_text",
    "origin",
    "origin_detail",
    "training_package_id",
    "training_export_id",
    "drawing_file_name",
    "layer",
    "owner_block_name",
    "text_style_name",
    "position_x",
    "position_y",
    "height",
    "font",
    "bigfont",
    "is_from_xref",
    "native_decode_evidence",
    "native_decode_scope",
    "native_decode_source_family",
    "native_decode_applied_family",
    "native_decode_hook_hit",
    "native_decode_object_correlation",
    "native_decode_cluster_correlation",
    "ldfile_font_evidence",
    "hook_raw_evidence",
    "hook_raw_payload_length",
    "hook_raw_confidence",
    "hook_raw_roundtrip",
    "hook_raw_candidate_source",
    "ripple_seed_count",
    "ripple_seed_quality",
    "ripple_distance_ratio",
]


def main() -> int:
    parser = argparse.ArgumentParser(description="Build DBText GlyphCore feature CSV from reviewed labels.")
    parser.add_argument("--input", required=True, help="Reviewed candidate-group JSONL path.")
    parser.add_argument("--output", required=True, help="Output feature CSV path.")
    args = parser.parse_args()

    input_path = Path(args.input)
    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    rows = list(build_rows(input_path))
    with output_path.open("w", encoding="utf-8-sig", newline="") as writer:
        fieldnames = METADATA_COLUMNS + [f"f{i:02d}_{name}" for i, name in enumerate(FEATURE_NAMES)]
        csv_writer = csv.DictWriter(writer, fieldnames=fieldnames)
        csv_writer.writeheader()
        csv_writer.writerows(rows)

    print(f"wrote {len(rows)} feature rows to {output_path}")
    return 0


def build_rows(input_path: Path):
    with input_path.open("r", encoding="utf-8") as reader:
        for line_number, line in enumerate(reader, start=1):
            if not line.strip():
                continue
            record = json.loads(line)
            validate_record(record, line_number)
            context = dict(record.get("context") or {})
            context["currentText"] = record.get("currentText") or context.get("currentText") or ""
            geometry = record.get("geometry") if isinstance(record.get("geometry"), dict) else {}
            position = geometry.get("position") if isinstance(geometry.get("position"), dict) else {}
            evidence = context.get("nativeDecodeEvidence") if isinstance(context.get("nativeDecodeEvidence"), dict) else {}
            target_scores = candidate_target_scores(record)

            for index, candidate_row in enumerate(record["candidates"]):
                candidate = Candidate(
                    text=candidate_row.get("text") or "",
                    source=candidate_row.get("source") or "",
                    reason=candidate_row.get("reason") or "",
                    is_roundtrip=bool(candidate_row.get("isRoundTrip", False)),
                )
                features = extract_features(context, candidate)
                row = {
                    "schema": "dbtext-ai-training-row-v1",
                    "feature_schema": FEATURE_SCHEMA_VERSION,
                    "group_id": record["groupId"],
                    "candidate_index": index,
                    "label_action": record["labelAction"],
                    "target_score": target_scores[index],
                    "is_positive": 1 if target_scores[index] >= 1.0 else 0,
                    "is_noop": 1 if candidate.is_noop else 0,
                    "is_roundtrip": 1 if candidate.is_roundtrip else 0,
                    "source": candidate.source,
                    "reason": candidate.reason,
                    "current_text": record.get("currentText") or "",
                    "candidate_text": candidate.text,
                    "label_text": record.get("labelText") or "",
                    "origin": record.get("origin") or "",
                    "origin_detail": record.get("originDetail") or "",
                    "training_package_id": record.get("trainingPackageId") or "",
                    "training_export_id": record.get("trainingExportId") or "",
                    "drawing_file_name": (record.get("context") or {}).get("drawingFileName") or "",
                    "layer": context.get("layer") or "",
                    "owner_block_name": context.get("ownerBlockName") or "",
                    "text_style_name": context.get("textStyleName") or "",
                    "position_x": float_dict_value(position, "x"),
                    "position_y": float_dict_value(position, "y"),
                    "height": float_dict_value(geometry, "height"),
                    "font": context.get("textStyleFileName") or "",
                    "bigfont": context.get("textStyleBigFontFileName") or "",
                    "is_from_xref": 1 if bool(context.get("isFromExternalReference", False)) else 0,
                    "native_decode_evidence": 1 if bool_value(context, evidence, "hasNativeDecodeEvidence", "hasEvidence") else 0,
                    "native_decode_scope": str_value(context, evidence, "nativeDecodeEvidenceScope", "scope"),
                    "native_decode_source_family": str_value(context, evidence, "nativeDecodeSourceCodePageFamily", "sourceCodePageFamily"),
                    "native_decode_applied_family": str_value(context, evidence, "nativeDecodeAppliedCodePageFamily", "appliedCodePageFamily"),
                    "native_decode_hook_hit": str_value(context, evidence, "nativeDecodeHookHitType", "hookHitType"),
                    "native_decode_object_correlation": float_value(context, evidence, "nativeDecodeObjectCorrelation", "objectCorrelation"),
                    "native_decode_cluster_correlation": float_value(context, evidence, "nativeDecodeClusterCorrelation", "clusterCorrelation"),
                    "ldfile_font_evidence": 1 if bool_value(context, evidence, "hasLdFileFontEvidence", "hasLdFileFontEvidence") else 0,
                    "hook_raw_evidence": 1 if bool_value(context, evidence, "hasHookRawDecodeEvidence", "hasHookRawDecodeEvidence") else 0,
                    "hook_raw_payload_length": int(float_value(context, evidence, "hookRawPayloadLength", "hookRawPayloadLength")),
                    "hook_raw_confidence": float_value(context, evidence, "hookRawConfidence", "hookRawConfidence"),
                    "hook_raw_roundtrip": 1 if bool_value(context, evidence, "hookRawRoundTrip", "hookRawRoundTrip") else 0,
                    "hook_raw_candidate_source": str_value(context, evidence, "hookRawCandidateSource", "hookRawCandidateSource"),
                    "ripple_seed_count": int(float_value(context, evidence, "rippleSeedCount", "rippleSeedCount")),
                    "ripple_seed_quality": float_value(context, evidence, "rippleSeedQuality", "rippleSeedQuality"),
                    "ripple_distance_ratio": float_value(context, evidence, "rippleDistanceRatio", "rippleDistanceRatio"),
                }
                for feature_index, feature_value in enumerate(features):
                    row[f"f{feature_index:02d}_{FEATURE_NAMES[feature_index]}"] = float(feature_value)
                yield row


def validate_record(record: dict, line_number: int) -> None:
    if record.get("schema") not in {"dbtext-ai-candidates-v1", "dbtext-ai-reviewed-label-v1"}:
        raise ValueError(f"line {line_number}: unsupported schema")
    if record.get("labelAction") not in {"repair", "keep", "unsafe", "unknown", "glyph-issue"}:
        raise ValueError(f"line {line_number}: unsupported labelAction")
    if not isinstance(record.get("candidates"), list) or not record["candidates"]:
        raise ValueError(f"line {line_number}: candidates must be a non-empty array")


def candidate_target_scores(record: dict) -> list[float]:
    candidates = list(record.get("candidates") or [])
    scores = [float(candidate.get("targetScore", 0.0) or 0.0) for candidate in candidates]
    label_action = str(record.get("labelAction") or "")
    label_text = str(record.get("labelText") or "")
    if label_action != "repair" or not label_text:
        return scores

    visible_matches = [
        index
        for index, candidate in enumerate(candidates)
        if not is_manual_review_candidate(candidate)
        and visible_text_equal(candidate.get("text") or "", label_text)
    ]
    if not visible_matches:
        return scores

    return [1.0 if index in visible_matches else 0.0 for index in range(len(candidates))]


def is_manual_review_candidate(candidate: dict) -> bool:
    return str(candidate.get("source") or "").lower() == "manual-review"


def normalize_visible_text(value: str) -> str:
    text = unicodedata.normalize("NFKC", str(value or ""))
    text = normalize_shx_number_sign_aliases(text)
    text = re.sub(r"[\u200b-\u200d\ufeff]", "", text)
    return text


def visible_text_equal(left: str, right: str) -> bool:
    left_text = normalize_visible_text(left)
    right_text = normalize_visible_text(right)
    if left_text == right_text:
        return True
    if starts_with_placeholder_space_run(left_text) or starts_with_placeholder_space_run(right_text):
        return False
    return left_text.strip() == right_text.strip()


def starts_with_placeholder_space_run(text: str) -> bool:
    return len(text) >= 2 and text[0].isspace() and text[1].isspace()


def normalize_shx_number_sign_aliases(text: str) -> str:
    if not text or "\u4E95" not in text:
        return text or ""
    chars = list(text)
    for index, char in enumerate(chars):
        if char == "\u4E95" and should_render_number_sign_alias(text, index):
            chars[index] = "#"
    return "".join(chars)


def should_render_number_sign_alias(text: str, index: int) -> bool:
    if index < 2 or index + 1 >= len(text):
        return False
    if text[index - 1] not in {"-", "\uFF0D"}:
        return False
    if not is_ascii_alnum(text[index + 1]):
        return False
    start = index - 2
    while start >= 0 and is_ascii_alnum(text[start]):
        start -= 1
    prefix = text[start + 1 : index - 1]
    return 1 <= len(prefix) <= 8 and any(is_ascii_alpha(char) for char in prefix)


def is_ascii_alnum(char: str) -> bool:
    return ("0" <= char <= "9") or is_ascii_alpha(char)


def is_ascii_alpha(char: str) -> bool:
    return ("A" <= char <= "Z") or ("a" <= char <= "z")


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


def float_dict_value(source: dict, key: str) -> float:
    try:
        return float(source.get(key) or 0.0)
    except (TypeError, ValueError):
        return 0.0


if __name__ == "__main__":
    raise SystemExit(main())
