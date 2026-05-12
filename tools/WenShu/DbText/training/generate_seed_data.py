from __future__ import annotations

import argparse
import json
import random
import sys
from pathlib import Path
from uuid import uuid5, NAMESPACE_URL

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from wenshu_dbtext.candidates import build_candidates, make_corrupted_current  # noqa: E402


CAD_PHRASES = [
    "消防水泵房",
    "生活给水管",
    "喷淋主管",
    "排烟风管",
    "地下车库",
    "屋顶水箱",
    "电气设备间",
    "弱电井",
    "强电井",
    "排污泵",
    "自动喷淋系统",
    "消火栓系统",
    "风机盘管",
    "冷冻水供水",
    "冷冻水回水",
    "压力排水管",
    "雨水立管",
    "污水立管",
    "详见系统图",
    "标高见建筑",
    "材料表",
    "管道支架",
    "阀门井",
    "水流指示器",
    "报警阀组",
    "防火阀",
    "送风口",
    "排风口",
    "风量调节阀",
    "配电箱",
    "应急照明",
    "疏散指示",
    "桥架安装",
    "预留套管",
    "穿墙套管",
    "设备基础",
    "检修口",
    "楼梯间",
    "管井内安装",
    "卫生间排水",
    "空调冷凝水",
    "新风机房",
    "水泵接合器",
    "压力表",
    "止回阀",
    "闸阀",
    "蝶阀",
    "排气阀",
    "水力警铃",
    "末端试水装置",
]

ASCII_AND_MIXED = [
    "DN100",
    "DN150 消防管",
    "P-01",
    "A-3F",
    "EL+3.500",
    "1:100",
    "B1 车库",
    "FM-01 防火门",
    "W1 给水",
    "FJ-2 风机",
]

UNKNOWN_TEXTS = [
    "???",
    "////",
    "@@@@",
    "A/B/C",
    "12345",
    "----",
    "见说明",
]

FONT_NAMES = ["hztxt.shx", "gbcbig.shx", "tssdchn.shx", "txt.shx", "simplex.shx"]
BIGFONT_NAMES = ["hzfs.shx", "gbcbig.shx", "chineset.shx", ""]
STYLE_NAMES = ["HZTXT", "STANDARD", "TSSD", "TEXT", "DIMTXT"]
LAYERS = ["A-ANNO-TEXT", "W-PIPE-TEXT", "E-LIGHT-TEXT", "H-HVAC-TEXT", "F-FIRE-TEXT"]
BLOCKS = ["*Model_Space", "TITLE_BLOCK", "PIPE_DETAIL", "FIRE_PLAN", "EQUIPMENT_BLOCK"]

CORRUPTION_PATHS = [
    ("big5-carrier-to-gbk", "cp950", "gbk"),
    ("gbk-carrier-to-big5", "gbk", "cp950"),
    ("utf8-carrier-to-gbk", "utf-8", "gbk"),
    ("gbk-carrier-to-utf8", "gbk", "utf-8"),
]


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate synthetic AFR WenShu DBText seed labels.")
    parser.add_argument("--count", type=int, default=2000, help="Target candidate group count.")
    parser.add_argument("--output", required=True, help="Output reviewed JSONL path.")
    parser.add_argument("--seed", type=int, default=20260512, help="Deterministic random seed.")
    args = parser.parse_args()

    rng = random.Random(args.seed)
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    records = generate_records(args.count, rng)
    with output.open("w", encoding="utf-8", newline="\n") as writer:
        for record in records:
            writer.write(json.dumps(record, ensure_ascii=False, sort_keys=True) + "\n")

    summary = summarize(records)
    print(f"wrote {len(records)} candidate groups to {output}")
    print(json.dumps(summary, ensure_ascii=False, sort_keys=True))
    return 0


def generate_records(count: int, rng: random.Random) -> list[dict]:
    records: list[dict] = []
    phrases = CAD_PHRASES + ASCII_AND_MIXED
    attempts = 0

    while len(records) < count and attempts < count * 50:
        attempts += 1
        clean = rng.choice(phrases)
        variant = rng.random()

        if variant < 0.48:
            path_name, carrier, target = rng.choice(CORRUPTION_PATHS)
            current = make_corrupted_current(clean, carrier, target)
            if not current:
                continue
            candidates = build_candidates(current)
            if not any(c.text == clean and c.is_roundtrip for c in candidates):
                continue
            records.append(make_record("repair", current, clean, candidates, rng, path_name))
        elif variant < 0.74:
            current = clean
            candidates = build_candidates(current)
            records.append(make_record("keep", current, clean, candidates, rng, "clean-text"))
        elif variant < 0.84:
            current = clean + "\u0007"
            candidates = build_candidates(current)
            records.append(make_record("unsafe", current, "", candidates, rng, "control-char"))
        elif variant < 0.92:
            current = clean + "\uFFFD"
            candidates = build_candidates(current)
            records.append(make_record("unsafe", current, "", candidates, rng, "replacement-char"))
        elif variant < 0.97:
            current = rng.choice(UNKNOWN_TEXTS)
            candidates = build_candidates(current)
            records.append(make_record("unknown", current, current, candidates, rng, "unknown-pattern"))
        else:
            current = clean
            candidates = build_candidates(current)
            records.append(make_record("glyph-issue", current, current, candidates, rng, "font-glyph-risk"))

    return records


def make_record(
    label_action: str,
    current: str,
    label_text: str,
    candidates,
    rng: random.Random,
    origin_detail: str,
) -> dict:
    group_key = f"{label_action}|{origin_detail}|{current}|{label_text}|{len(candidates)}"
    group_id = str(uuid5(NAMESPACE_URL, "afr-dbtext-ai/" + group_key + "/" + str(rng.random())))
    context = make_context(current, rng)

    candidate_rows = []
    for candidate in candidates:
        target_score = target_for_candidate(label_action, current, label_text, candidate)
        candidate_rows.append(
            {
                "text": candidate.text,
                "source": candidate.source,
                "reason": candidate.reason,
                "isRoundTrip": candidate.is_roundtrip,
                "targetScore": target_score,
            }
        )

    return {
        "schema": "dbtext-ai-candidates-v1",
        "groupId": group_id,
        "origin": "synthetic-seed-v1",
        "originDetail": origin_detail,
        "labelAction": label_action,
        "labelText": label_text,
        "currentText": current,
        "context": context,
        "candidates": candidate_rows,
    }


def make_context(current: str, rng: random.Random) -> dict:
    return {
        "drawingPath": "",
        "drawingFileName": "synthetic-seed.dwg",
        "drawingLength": 0,
        "drawingLastWriteUtc": "",
        "drawingSha256": "",
        "entityType": "DBText",
        "objectId": "",
        "handle": "",
        "layer": rng.choice(LAYERS),
        "ownerBlockName": rng.choice(BLOCKS),
        "textStyleName": rng.choice(STYLE_NAMES),
        "textStyleFileName": rng.choice(FONT_NAMES),
        "textStyleBigFontFileName": rng.choice(BIGFONT_NAMES),
        "textStyleTypeFace": "",
        "currentText": current,
        "isFromExternalReference": rng.random() < 0.015,
    }


def target_for_candidate(label_action: str, current: str, label_text: str, candidate) -> float:
    if label_action == "repair":
        return 1.0 if candidate.text == label_text and candidate.text != current else 0.0
    if candidate.text == current and "current-noop" in candidate.source.lower():
        return 1.0
    return 0.0


def summarize(records: list[dict]) -> dict:
    by_action: dict[str, int] = {}
    candidate_count = 0
    positive_count = 0
    for record in records:
        by_action[record["labelAction"]] = by_action.get(record["labelAction"], 0) + 1
        for candidate in record["candidates"]:
            candidate_count += 1
            if candidate["targetScore"] >= 1.0:
                positive_count += 1
    return {
        "groups": len(records),
        "candidates": candidate_count,
        "positiveCandidates": positive_count,
        "labelActions": by_action,
    }


if __name__ == "__main__":
    raise SystemExit(main())
