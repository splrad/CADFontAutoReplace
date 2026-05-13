from __future__ import annotations

import argparse
import csv
import json
import sys
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
    "layer",
    "owner_block_name",
    "text_style_name",
    "font",
    "bigfont",
    "is_from_xref",
]


def main() -> int:
    parser = argparse.ArgumentParser(description="Build dbtext-ai-features-v1 CSV from reviewed labels.")
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
                    "target_score": float(candidate_row.get("targetScore", 0.0)),
                    "is_positive": 1 if float(candidate_row.get("targetScore", 0.0)) >= 1.0 else 0,
                    "is_noop": 1 if candidate.is_noop else 0,
                    "is_roundtrip": 1 if candidate.is_roundtrip else 0,
                    "source": candidate.source,
                    "reason": candidate.reason,
                    "current_text": record.get("currentText") or "",
                    "candidate_text": candidate.text,
                    "label_text": record.get("labelText") or "",
                    "origin": record.get("origin") or "",
                    "origin_detail": record.get("originDetail") or "",
                    "layer": context.get("layer") or "",
                    "owner_block_name": context.get("ownerBlockName") or "",
                    "text_style_name": context.get("textStyleName") or "",
                    "font": context.get("textStyleFileName") or "",
                    "bigfont": context.get("textStyleBigFontFileName") or "",
                    "is_from_xref": 1 if bool(context.get("isFromExternalReference", False)) else 0,
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


if __name__ == "__main__":
    raise SystemExit(main())
