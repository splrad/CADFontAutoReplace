from __future__ import annotations

import argparse
import csv
import hashlib
import json
import random
import re
import sys
from collections import Counter
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Iterable

TOOL_ROOT = Path(__file__).resolve().parents[1]
GLYPH_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(TOOL_ROOT))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from afr_glyphcore import FEATURE_SCHEMA_VERSION  # noqa: E402
from afr_glyphcore.candidates import Candidate, build_candidates, make_corrupted_current  # noqa: E402
from afr_glyphcore.features import FEATURE_NAMES, extract_features, has_unsafe_text  # noqa: E402
from build_features import METADATA_COLUMNS, build_rows  # noqa: E402


REVIEWED_SCHEMA = "dbtext-ai-reviewed-label-v1"
CANDIDATE_SCHEMA = "dbtext-ai-candidate-group-v1"
LEGACY_TRAINING_SCHEMA = "dbtext-ai-candidates-v1"
TRAINING_DATASET_SCHEMA = "dbtext-ai-training-dataset-entry-v1"
GENERATION_RULE_VERSION = "hook-cluster-ripple-v4"
DEFAULT_SEED = 20260514
DEFAULT_COUNT = 10000

ENCODING_PATHS = [
    ("big5-carrier-to-gbk", "cp950", "gbk"),
    ("gbk-carrier-to-big5", "gbk", "cp950"),
    ("utf8-carrier-to-gbk", "utf-8", "gbk"),
    ("gbk-carrier-to-utf8", "gbk", "utf-8"),
]

OCR_CONFUSIONS = {
    "见": "赣",
    "赣": "见",
    "管": "筫",
    "阀": "闷",
    "泵": "枣",
    "喷": "暎",
    "淋": "琳",
    "图": "囡",
    "注": "泣",
    "梁": "粱",
    "顶": "项",
    "底": "庶",
    "座": "座美",
    "个": "介",
    "米": "来",
    "水": "氺",
    "火": "灭",
    "风": "凤",
    "门": "闩",
    "高": "商",
    "宽": "寬",
}

DIGIT_CONFUSIONS = {
    "0": "O",
    "O": "0",
    "1": "I",
    "I": "1",
    "5": "S",
    "S": "5",
    "8": "B",
    "B": "8",
    "2": "Z",
    "Z": "2",
}

FULLWIDTH_MAP = str.maketrans(
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-()./",
    "ＡＢＣＤＥＦＧＨＩＪＫＬＭＮＯＰＱＲＳＴＵＶＷＸＹＺ"
    "ａｂｃｄｅｆｇｈｉｊｋｌｍｎｏｐｑｒｓｔｕｖｗｘｙｚ"
    "０１２３４５６７８９＋－（）．／",
)


@dataclass(frozen=True)
class SourceRecord:
    package_id: str
    candidate_record: dict
    training_record: dict

    @property
    def group_id(self) -> str:
        return str(self.training_record.get("groupId") or self.candidate_record.get("groupId") or "")

    @property
    def action(self) -> str:
        return str(self.training_record.get("labelAction") or "")

    @property
    def current_text(self) -> str:
        return str(self.training_record.get("currentText") or self.candidate_record.get("currentText") or "")

    @property
    def label_text(self) -> str:
        return str(self.training_record.get("labelText") or "")

    @property
    def clean_text(self) -> str:
        if self.action in {"repair", "keep"} and self.label_text:
            return self.label_text
        return self.current_text


@dataclass
class SourceCorpus:
    records: list[SourceRecord]
    repair_records: list[SourceRecord]
    keep_records: list[SourceRecord]
    nonrepair_records: list[SourceRecord]
    conflicting_repair_records: list[SourceRecord]
    clean_texts: list[str]


@dataclass
class ClusterSpec:
    category: str
    label_action: str
    current_text: str
    label_text: str
    candidates: list[Candidate]
    selected_text: str
    selected_source_hint: str
    source_record: SourceRecord
    variant_rule: str
    risk_tier: str
    roundtrip_status: str
    human_reason: str
    human_accepted_candidate: bool
    cluster_id: str
    propagation_signature: str
    cluster_size: int


@dataclass
class GenerationResult:
    export_id: str
    package_dir: Path
    reviewed_path: Path
    audit_path: Path
    training_path: Path
    features_path: Path
    report_path: Path
    manifest_path: Path
    records: list[dict]
    training_records: list[dict]
    candidate_records: list[dict]
    audit_rows: list[dict]
    report: dict


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate high-value local DBText training records from real GlyphCore packages."
    )
    parser.add_argument("--dataset-root", default=str(GLYPH_ROOT / "datasets"))
    parser.add_argument("--count", type=int, default=DEFAULT_COUNT)
    parser.add_argument("--seed", type=int, default=DEFAULT_SEED)
    parser.add_argument("--export-id", default="")
    parser.add_argument("--source-package", action="append", default=[])
    parser.add_argument("--reviewer", default="synthetic-high-value-generator")
    parser.add_argument("--base-reviewed-utc", default="2026-05-14T00:00:00Z")
    parser.add_argument("--no-build-features", action="store_true")
    args = parser.parse_args()

    dataset_root = Path(args.dataset_root)
    export_id = args.export_id.strip() or default_export_id()
    corpus = load_source_corpus(dataset_root, args.source_package)
    result = generate_augmented_dataset(
        corpus=corpus,
        dataset_root=dataset_root,
        export_id=export_id,
        count=args.count,
        seed=args.seed,
        reviewer=args.reviewer,
        base_reviewed_utc=parse_utc(args.base_reviewed_utc),
    )
    write_generation_outputs(result, build_features=not args.no_build_features)

    validation = result.report.get("validation") or {}
    if validation.get("errors"):
        print(json.dumps(validation, ensure_ascii=False, indent=2), file=sys.stderr)
        return 2

    print(f"wrote {len(result.training_records)} reviewed DBText records to {result.reviewed_path}")
    print(f"wrote training dataset to {result.training_path}")
    if result.features_path.exists():
        print(f"wrote feature CSV to {result.features_path}")
    print(f"wrote generation report to {result.report_path}")
    return 0


def default_export_id() -> str:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    return f"{stamp}_hook_cluster_ripple_v4"


def parse_utc(value: str) -> datetime:
    normalized = value.strip().replace("Z", "+00:00")
    parsed = datetime.fromisoformat(normalized)
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=timezone.utc)
    return parsed.astimezone(timezone.utc)


def load_source_corpus(dataset_root: Path, package_ids: list[str] | None = None) -> SourceCorpus:
    package_dirs = resolve_source_packages(dataset_root, package_ids or [])
    records: list[SourceRecord] = []

    for package_dir in package_dirs:
        manifest = read_json(package_dir / "manifest.json")
        export_id = str(manifest.get("exportId") or package_dir.name)
        candidates_by_group = read_jsonl_by_group(package_dir / "candidate_groups.jsonl")
        training_path = dataset_root / "TrainingSets" / f"{export_id}_training_dataset.jsonl"
        if not training_path.exists():
            raise FileNotFoundError(f"missing training dataset for {export_id}: {training_path}")

        for training_record in read_jsonl(training_path):
            group_id = str(training_record.get("groupId") or "")
            if not group_id:
                continue
            candidate_record = candidates_by_group.get(group_id, training_record)
            records.append(SourceRecord(export_id, candidate_record, training_record))

    if not records:
        raise ValueError("no source records were found")

    repair_records = [r for r in records if r.action == "repair" and usable_clean_text(r.clean_text)]
    keep_records = [r for r in records if r.action == "keep" and usable_clean_text(r.clean_text)]
    nonrepair_records = [r for r in records if r.action != "repair"]
    conflicting_repair_records = [r for r in repair_records if has_conflicting_repair_target(r)]
    clean_texts = dedupe_keep_order(
        r.clean_text for r in records if r.action in {"repair", "keep"} and usable_clean_text(r.clean_text)
    )

    if not clean_texts:
        raise ValueError("source packages do not contain usable clean CAD text")
    if not repair_records:
        repair_records = [r for r in records if usable_clean_text(r.clean_text)]
    if not keep_records:
        keep_records = [r for r in records if usable_clean_text(r.clean_text)]
    if not nonrepair_records:
        nonrepair_records = records[:]

    return SourceCorpus(records, repair_records, keep_records, nonrepair_records, conflicting_repair_records, clean_texts)


def resolve_source_packages(dataset_root: Path, requested: list[str]) -> list[Path]:
    extracted_root = dataset_root / "ExtractedCandidates"
    if requested:
        package_dirs = [(extracted_root / package_id) for package_id in requested]
    else:
        package_dirs = []
        for package_dir in sorted(path for path in extracted_root.iterdir() if path.is_dir()):
            if "high_value" in package_dir.name.lower():
                continue
            if not (package_dir / "candidate_groups.jsonl").exists():
                continue
            manifest_path = package_dir / "manifest.json"
            if not manifest_path.exists():
                continue
            export_id = str(read_json(manifest_path).get("exportId") or package_dir.name)
            if (dataset_root / "TrainingSets" / f"{export_id}_training_dataset.jsonl").exists():
                package_dirs.append(package_dir)

    missing = [str(path) for path in package_dirs if not path.exists()]
    if missing:
        raise FileNotFoundError("missing source packages: " + ", ".join(missing))
    if not package_dirs:
        raise FileNotFoundError(f"no source packages found under {extracted_root}")
    return package_dirs


def generate_augmented_dataset(
    corpus: SourceCorpus,
    dataset_root: Path,
    export_id: str,
    count: int,
    seed: int,
    reviewer: str,
    base_reviewed_utc: datetime,
) -> GenerationResult:
    rng = random.Random(seed)
    quotas = allocate_quotas(count)
    package_dir = dataset_root / "ExtractedCandidates" / export_id
    reviewed_path = dataset_root / "ReviewedLabels" / f"{export_id}_reviewed.jsonl"
    audit_path = dataset_root / "ReviewedLabels" / f"{export_id}_review_audit.tsv"
    training_path = dataset_root / "TrainingSets" / f"{export_id}_training_dataset.jsonl"
    features_path = dataset_root / "TrainingSets" / f"{export_id}_features.csv"
    report_path = dataset_root / "Reports" / f"{export_id}_generation_report.json"
    manifest_path = package_dir / "manifest.json"

    reviewed_records: list[dict] = []
    training_records: list[dict] = []
    candidate_records: list[dict] = []
    audit_rows: list[dict] = []
    ordinal = 0
    cluster_number = 0

    for category, target_count in quotas.items():
        remaining = target_count
        while remaining > 0:
            cluster_size = min(remaining, pick_cluster_size(category, rng))
            spec = make_cluster_spec(corpus, rng, export_id, category, cluster_number, cluster_size)
            cluster_number += 1
            for member_index in range(cluster_size):
                ordinal += 1
                context_source = choose_context_source(corpus, spec.source_record, rng, member_index)
                context = make_context(context_source, spec, ordinal, rng)
                geometry = make_geometry(context_source, ordinal, rng)
                candidates = list(spec.candidates)
                selected_index = ensure_selected_candidate(candidates, spec.selected_text, spec.selected_source_hint)
                candidate_rows = build_candidate_rows(candidates, context, selected_index)
                drawing = make_drawing(context_source)
                group_id = make_group_id(export_id, ordinal, spec.current_text, spec.label_text)
                reviewed_utc = (base_reviewed_utc + timedelta(seconds=ordinal)).isoformat().replace("+00:00", "Z")
                candidate_record = {
                    "schema": CANDIDATE_SCHEMA,
                    "featureSchema": FEATURE_SCHEMA_VERSION,
                    "exportId": export_id,
                    "groupId": group_id,
                    "drawing": drawing,
                    "context": context,
                    "geometry": geometry,
                    "currentText": spec.current_text,
                    "problemGate": make_problem_gate(spec),
                    "risk": make_risk(spec, context, candidate_rows),
                    "candidates": strip_training_targets(candidate_rows),
                }
                reviewed_record = {
                    **candidate_record,
                    "schema": REVIEWED_SCHEMA,
                    "legacyTrainingSchema": LEGACY_TRAINING_SCHEMA,
                    "labelAction": spec.label_action,
                    "labelText": spec.label_text,
                    "selectedCandidateIndex": selected_index,
                    "reviewer": reviewer,
                    "reviewedUtc": reviewed_utc,
                    "origin": "high-value-synthetic-from-real",
                    "originDetail": spec.variant_rule,
                    "candidates": candidate_rows,
                    "batchId": spec.cluster_id,
                    "batchRule": GENERATION_RULE_VERSION,
                    "batchReviewedSampleIds": [spec.source_record.group_id],
                    "batchConfidenceLevel": spec.risk_tier,
                    "appliedByCluster": True,
                    "propagationClusterId": spec.cluster_id,
                    "propagationSignature": spec.propagation_signature,
                    "clusterReviewedSampleIds": [spec.source_record.group_id],
                    "clusterRiskSummary": {
                        "riskTier": spec.risk_tier,
                        "roundtripStatus": spec.roundtrip_status,
                        "variantRule": spec.variant_rule,
                    },
                    "clusterContextSummary": {
                        "clusterSize": spec.cluster_size,
                        "sourcePackageId": spec.source_record.package_id,
                        "sourceLayer": source_context(spec.source_record).get("layer") or "",
                        "sourceTextStyleName": source_context(spec.source_record).get("textStyleName") or "",
                    },
                    "sourceRepresentativeHandle": source_context(spec.source_record).get("handle") or "",
                    "sourceRepresentativeGroupId": spec.source_record.group_id,
                    "appliedCount": spec.cluster_size,
                    "skippedConflictCount": 0,
                    "skippedRiskCount": 0,
                    "skippedRoundtripCount": 0,
                    "skippedContextCount": 0,
                    "skippedManualCount": 0,
                    "propagationRule": GENERATION_RULE_VERSION,
                    "propagationScope": "high-value-cluster-expanded",
                    "humanReviewMode": "simulated-production-review",
                    "sourcePackageId": spec.source_record.package_id,
                    "sourceGroupId": spec.source_record.group_id,
                    "sourceHandle": source_context(spec.source_record).get("handle") or "",
                    "variantRule": spec.variant_rule,
                    "humanAcceptedCandidate": spec.human_accepted_candidate,
                    "humanCorrectionReason": spec.human_reason,
                    "riskTier": spec.risk_tier,
                    "roundtripStatus": spec.roundtrip_status,
                    "generationRuleVersion": GENERATION_RULE_VERSION,
                }
                training_record = {
                    **reviewed_record,
                    "trainingDatasetSchema": TRAINING_DATASET_SCHEMA,
                    "enteredTrainingUtc": reviewed_utc,
                    "trainingFeatureBuildId": f"high-value-{seed}",
                    "trainingSource": "high-value-generator",
                    "trainingPackageId": export_id,
                    "trainingExportId": export_id,
                }
                selected_candidate = candidate_rows[selected_index]
                audit_rows.append(
                    make_audit_row(
                        ordinal,
                        reviewed_record,
                        selected_candidate,
                        spec,
                        context,
                    )
                )
                candidate_records.append(candidate_record)
                reviewed_records.append(reviewed_record)
                training_records.append(training_record)
            remaining -= cluster_size

    report = make_generation_report(
        export_id=export_id,
        seed=seed,
        quotas=quotas,
        corpus=corpus,
        training_records=training_records,
        candidate_records=candidate_records,
        audit_rows=audit_rows,
    )
    return GenerationResult(
        export_id=export_id,
        package_dir=package_dir,
        reviewed_path=reviewed_path,
        audit_path=audit_path,
        training_path=training_path,
        features_path=features_path,
        report_path=report_path,
        manifest_path=manifest_path,
        records=reviewed_records,
        training_records=training_records,
        candidate_records=candidate_records,
        audit_rows=audit_rows,
        report=report,
    )


def allocate_quotas(count: int) -> dict[str, int]:
    if count <= 0:
        raise ValueError("count must be positive")
    ratios = [
        ("keep", 0.48),
        ("repair", 0.32),
        ("unknown", 0.16),
        ("unsafe", 0.02),
        ("glyph-issue", 0.02),
    ]
    quotas = {name: int(count * ratio) for name, ratio in ratios}
    remainder = count - sum(quotas.values())
    for name, _ in ratios:
        if remainder <= 0:
            break
        quotas[name] += 1
        remainder -= 1
    return quotas


def pick_cluster_size(category: str, rng: random.Random) -> int:
    roll = rng.random()
    if category == "keep":
        if roll < 0.35:
            return rng.randint(40, 160)
        if roll < 0.82:
            return rng.randint(6, 28)
        return rng.randint(1, 4)
    if category == "repair":
        if roll < 0.25:
            return rng.randint(20, 90)
        if roll < 0.78:
            return rng.randint(4, 20)
        return rng.randint(1, 3)
    if category == "unknown":
        if roll < 0.20:
            return rng.randint(15, 70)
        if roll < 0.70:
            return rng.randint(3, 16)
        return rng.randint(1, 3)
    if roll < 0.15:
        return rng.randint(10, 40)
    if roll < 0.75:
        return rng.randint(2, 10)
    return 1


def make_cluster_spec(
    corpus: SourceCorpus,
    rng: random.Random,
    export_id: str,
    category: str,
    cluster_number: int,
    cluster_size: int,
) -> ClusterSpec:
    if category == "keep":
        spec = make_keep_spec(corpus, rng)
    elif category == "repair":
        spec = make_repair_spec(corpus, rng)
    elif category == "unknown":
        spec = make_unknown_spec(corpus, rng)
    elif category == "unsafe":
        spec = make_unsafe_spec(corpus, rng)
    elif category == "glyph-issue":
        spec = make_glyph_issue_spec(corpus, rng)
    else:
        raise ValueError(f"unsupported category: {category}")

    cluster_id = stable_id("cluster", export_id, category, str(cluster_number), spec[1], spec[2])
    signature = stable_id("signature", category, spec[1], spec[2], spec[4], spec[5])
    return ClusterSpec(
        category=category,
        label_action=spec[0],
        current_text=spec[1],
        label_text=spec[2],
        candidates=spec[3],
        selected_text=spec[4],
        selected_source_hint=spec[5],
        source_record=spec[6],
        variant_rule=spec[7],
        risk_tier=spec[8],
        roundtrip_status=spec[9],
        human_reason=spec[10],
        human_accepted_candidate=spec[11],
        cluster_id=cluster_id,
        propagation_signature=signature,
        cluster_size=cluster_size,
    )


def make_keep_spec(corpus: SourceCorpus, rng: random.Random) -> tuple:
    source = rng.choice(corpus.keep_records or corpus.nonrepair_records)
    text = choose_clean_text(corpus, source, rng)
    text = maybe_contextualize_clean_text(corpus, text, rng)
    candidates = build_candidates_with_hard_negatives(text, text, rng, category="keep")
    return (
        "keep",
        text,
        text,
        candidates,
        text,
        "current-noop",
        source,
        rng.choice(["hard-negative-clean", "normal-cad-text-with-conflict-candidates", "safe-short-or-code-text"]),
        "medium" if len(candidates) > 1 else "low",
        "noop-target",
        "人工判断原文符合图纸语义，候选为编码或OCR噪声",
        False,
    )


def make_repair_spec(corpus: SourceCorpus, rng: random.Random) -> tuple:
    source = rng.choice(corpus.repair_records or corpus.records)
    clean = choose_clean_text(corpus, source, rng)
    variant = rng.choices(
        [
            "hook-raw-roundtrip",
            "ripple-context-repair",
            "encoding-roundtrip",
            "half-corrupt-manual",
            "ocr-single-char-manual",
            "chain-pollution-manual",
            "mixed-engineering-manual",
            "long-note-manual",
            "source-real-repair",
        ],
        weights=[16, 12, 42, 10, 7, 5, 4, 3, 5],
        k=1,
    )[0]
    if corpus.conflicting_repair_records and rng.random() < 0.12:
        variant = "source-conflict-correction"

    selected_source = ""
    roundtrip_status = "manual-nonroundtrip"
    accepted = False

    if variant == "hook-raw-roundtrip":
        current, path_name = make_roundtrip_corruption(clean, rng)
        label = clean
        candidates = build_candidates(current)
        selected_source = "hook-raw-stream+" + path_name
        add_or_merge_candidate(candidates, Candidate(label, selected_source, "hook-raw-roundtrip-ok", True))
        accepted = True
        roundtrip_status = "hook-raw-roundtrip-ok"
    elif variant == "ripple-context-repair":
        current, path_name = make_roundtrip_corruption(clean, rng)
        label = clean
        candidates = build_candidates(current)
        selected_source = path_name
        accepted = candidate_contains_text(candidates, label)
        roundtrip_status = "ripple-seed-context"
    elif variant == "source-real-repair" and source.action == "repair":
        current = source.current_text
        label = source.label_text or clean
        candidates = candidates_from_source_or_build(source, current)
        selected_source = "manual-review"
        if candidate_contains_text(candidates, label):
            selected_source = ""
            accepted = True
            roundtrip_status = "source-roundtrip-or-ai-candidate"
    elif variant == "source-conflict-correction":
        source = rng.choice(corpus.conflicting_repair_records)
        current = source.current_text
        label = source.label_text
        candidates = candidates_from_source_or_build(source, current)
        for candidate in candidates:
            if candidate.text != label and not candidate.is_noop:
                candidate.reason = add_reason_token(candidate.reason, "source-target-conflict-negative")
        selected_source = "manual-review"
        roundtrip_status = "source-target-conflict-corrected"
    elif variant == "encoding-roundtrip":
        current, path_name = make_roundtrip_corruption(clean, rng)
        label = clean
        candidates = build_candidates(current)
        selected_source = path_name
        accepted = candidate_contains_text(candidates, label)
        roundtrip_status = "roundtrip-ok" if accepted else "roundtrip-missing"
    elif variant == "half-corrupt-manual":
        current = make_half_corrupt(clean, rng)
        label = clean
        candidates = build_candidates_with_hard_negatives(current, label, rng, category="repair")
        selected_source = "manual-review"
        roundtrip_status = "partial-roundtrip-manual"
    elif variant == "ocr-single-char-manual":
        current = mutate_ocr(clean, rng, force_single=True)
        label = clean
        candidates = build_candidates_with_hard_negatives(current, label, rng, category="repair")
        selected_source = "manual-review"
        roundtrip_status = "ocr-manual"
    elif variant == "chain-pollution-manual":
        current = make_chain_pollution(clean, rng)
        label = clean
        candidates = build_candidates_with_hard_negatives(current, label, rng, category="repair")
        selected_source = "manual-review"
        roundtrip_status = "chain-polluted"
    elif variant == "long-note-manual":
        label = make_long_note(corpus, clean, rng)
        current = make_half_corrupt(label, rng)
        candidates = build_candidates_with_hard_negatives(current, label, rng, category="repair")
        selected_source = "manual-review"
        roundtrip_status = "long-text-manual"
    else:
        label = make_mixed_engineering_text(corpus, clean, rng)
        current = mutate_ocr(make_half_corrupt(label, rng), rng, force_single=False)
        candidates = build_candidates_with_hard_negatives(current, label, rng, category="repair")
        selected_source = "manual-review"
        roundtrip_status = "mixed-cad-manual"

    if not candidate_contains_text(candidates, label):
        add_or_merge_candidate(candidates, Candidate(label, "manual-review", "human-reviewed-correction", True))
        selected_source = "manual-review"
        accepted = False
    elif selected_source == "manual-review":
        add_or_merge_candidate(candidates, Candidate(label, "manual-review", "human-reviewed-correction", True))
        accepted = False

    if rng.random() < 0.45:
        add_conflict_candidate(candidates, label, rng)

    reason = "人工按工程语义修正候选，避免编码候选只形似不达意"
    risk = "high" if selected_source == "manual-review" or len(candidates) >= 3 else "medium"
    return (
        "repair",
        current,
        label,
        candidates,
        label,
        selected_source,
        source,
        variant,
        risk,
        roundtrip_status,
        reason,
        accepted,
    )


def make_unknown_spec(corpus: SourceCorpus, rng: random.Random) -> tuple:
    source = rng.choice(corpus.nonrepair_records or corpus.records)
    base = choose_clean_text(corpus, source, rng)
    current = make_irreversible_noise(base, rng)
    candidates = build_candidates_with_hard_negatives(current, current, rng, category="unknown")
    return (
        "unknown",
        current,
        current,
        candidates,
        current,
        "current-noop",
        source,
        "irreversible-encoding-or-symbol-noise",
        "high",
        "irreversible",
        "人工无法从上下文确认可靠修复，标记为未知避免误修",
        False,
    )


def make_unsafe_spec(corpus: SourceCorpus, rng: random.Random) -> tuple:
    source = rng.choice(corpus.nonrepair_records or corpus.records)
    base = choose_clean_text(corpus, source, rng)
    current = inject_unsafe_text(base, rng)
    candidates = build_candidates_with_hard_negatives(current, current, rng, category="unsafe")
    return (
        "unsafe",
        current,
        current,
        candidates,
        current,
        "current-noop",
        source,
        "unsafe-control-or-replacement-char",
        "high",
        "unsafe-text",
        "包含控制符或替换符，人工保守跳过自动修复",
        False,
    )


def make_glyph_issue_spec(corpus: SourceCorpus, rng: random.Random) -> tuple:
    source = rng.choice(corpus.keep_records or corpus.records)
    base = choose_clean_text(corpus, source, rng)
    current = base if rng.random() < 0.70 else make_pseudo_shx_text(base, rng)
    candidates = build_candidates_with_hard_negatives(current, current, rng, category="glyph-issue")
    return (
        "glyph-issue",
        current,
        current,
        candidates,
        current,
        "current-noop",
        source,
        "shx-glyph-risk-with-no-text-repair",
        "medium",
        "font-glyph-risk",
        "判断为字体显示风险，不把文本内容改写为候选",
        False,
    )


def choose_clean_text(corpus: SourceCorpus, source: SourceRecord, rng: random.Random) -> str:
    text = source.clean_text
    if usable_clean_text(text) and rng.random() < 0.82:
        return text
    return rng.choice(corpus.clean_texts)


def maybe_contextualize_clean_text(corpus: SourceCorpus, text: str, rng: random.Random) -> str:
    if len(text) <= 1 or rng.random() >= 0.22:
        return text
    variant = rng.choice(["fullwidth", "punctuation", "device-code", "multi-fragment"])
    if variant == "fullwidth":
        return partial_fullwidth(text, rng)
    if variant == "punctuation":
        return drop_or_swap_punctuation(text, rng)
    if variant == "device-code":
        token = extract_ascii_token(rng.choice(corpus.clean_texts)) or f"DN{rng.choice([25, 50, 80, 100, 150])}"
        return f"{token} {text}" if len(text) < 20 else text
    other = rng.choice(corpus.clean_texts)
    if other != text and len(text) + len(other) <= 48:
        return f"{text}；{other}"
    return text


def make_roundtrip_corruption(text: str, rng: random.Random) -> tuple[str, str]:
    paths = ENCODING_PATHS[:]
    rng.shuffle(paths)
    for path_name, carrier, target in paths:
        current = make_corrupted_current(text, carrier, target)
        if current:
            return current, path_name
    return mutate_ocr(text, rng, force_single=False), "manual-review"


def make_half_corrupt(text: str, rng: random.Random) -> str:
    if len(text) < 3:
        return mutate_ocr(text, rng, force_single=True)
    spans = possible_spans(text, rng)
    for start, end in spans:
        segment = text[start:end]
        corrupted, _ = make_roundtrip_corruption(segment, rng)
        if corrupted and corrupted != segment:
            return text[:start] + corrupted + text[end:]
    return mutate_ocr(text, rng, force_single=False)


def possible_spans(text: str, rng: random.Random) -> list[tuple[int, int]]:
    length = len(text)
    spans: list[tuple[int, int]] = []
    for _ in range(8):
        size = rng.randint(1, max(1, min(8, length)))
        start = rng.randint(0, max(0, length - size))
        spans.append((start, start + size))
    spans.append((0, length))
    return spans


def make_chain_pollution(text: str, rng: random.Random) -> str:
    current, _ = make_roundtrip_corruption(text, rng)
    if rng.random() < 0.55:
        current = mutate_ocr(current, rng, force_single=False)
    if rng.random() < 0.35:
        current = drop_or_swap_punctuation(current, rng)
    return current


def mutate_ocr(text: str, rng: random.Random, force_single: bool) -> str:
    if not text:
        return text
    chars = list(text)
    positions = [i for i, ch in enumerate(chars) if ch in OCR_CONFUSIONS or ch in DIGIT_CONFUSIONS]
    if positions:
        count = 1 if force_single else rng.randint(1, min(3, len(positions)))
        for index in rng.sample(positions, count):
            ch = chars[index]
            chars[index] = OCR_CONFUSIONS.get(ch) or DIGIT_CONFUSIONS.get(ch) or ch
        return "".join(chars)

    if len(chars) == 1:
        return chars[0] + rng.choice(["", "1", "A"])
    operation = "replace" if force_single else rng.choice(["drop", "duplicate", "swap", "replace"])
    index = rng.randrange(len(chars))
    if operation == "drop":
        del chars[index]
    elif operation == "duplicate":
        chars.insert(index, chars[index])
    elif operation == "swap" and len(chars) > 1:
        j = min(len(chars) - 1, index + 1)
        chars[index], chars[j] = chars[j], chars[index]
    else:
        chars[index] = rng.choice(["赣", "囡", "泣", "O", "0", "Ⅰ"])
    return "".join(chars)


def make_long_note(corpus: SourceCorpus, seed_text: str, rng: random.Random) -> str:
    parts = [seed_text]
    while len("；".join(parts)) < 42 and len(parts) < 4:
        candidate = rng.choice(corpus.clean_texts)
        if candidate not in parts and 1 <= len(candidate) <= 24:
            parts.append(candidate)
    separator = "\n" if rng.random() < 0.35 else "；"
    return separator.join(parts)[:96]


def make_mixed_engineering_text(corpus: SourceCorpus, seed_text: str, rng: random.Random) -> str:
    token = extract_ascii_token(seed_text) or extract_ascii_token(rng.choice(corpus.clean_texts))
    if not token:
        token = rng.choice(["DN100", "GB50016-2014", "EL+3.500", "97S202", "P-01"])
    if len(seed_text) <= 24:
        return rng.choice([f"{token} {seed_text}", f"{seed_text} {token}", f"{seed_text}({token})"])
    return seed_text


def make_irreversible_noise(base: str, rng: random.Random) -> str:
    current = make_chain_pollution(base, rng)
    operations = [
        lambda s: s[: max(1, len(s) // 2)] + "\uFFFD",
        lambda s: s + rng.choice(["??", "////", "@@@", "□"]),
        lambda s: "".join(ch if i % 3 else "?" for i, ch in enumerate(s)),
        lambda s: s.encode("utf-8", errors="ignore").decode("gbk", errors="replace"),
    ]
    mutated = rng.choice(operations)(current)
    return mutated if mutated and mutated != base else base + "\uFFFD"


def inject_unsafe_text(base: str, rng: random.Random) -> str:
    marker = rng.choice(["\u0007", "\u001b", "\uFFFD", "\ue000", "\uf8ff"])
    if not base:
        return marker
    index = rng.randint(0, len(base))
    return base[:index] + marker + base[index:]


def make_pseudo_shx_text(base: str, rng: random.Random) -> str:
    if len(base) <= 1:
        return base
    chars = list(base)
    count = rng.randint(1, min(2, len(chars)))
    for index in rng.sample(range(len(chars)), count):
        chars[index] = rng.choice(["囗", "□", "〇", "—", "∅"])
    return "".join(chars)


def partial_fullwidth(text: str, rng: random.Random) -> str:
    chars = list(text)
    ascii_positions = [i for i, ch in enumerate(chars) if ch in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-()./"]
    if not ascii_positions:
        return text
    for index in rng.sample(ascii_positions, rng.randint(1, min(3, len(ascii_positions)))):
        chars[index] = chars[index].translate(FULLWIDTH_MAP)
    return "".join(chars)


def drop_or_swap_punctuation(text: str, rng: random.Random) -> str:
    chars = list(text)
    punctuation = [i for i, ch in enumerate(chars) if ch in "，。、；：:,.()/（）-—+"]
    if punctuation and rng.random() < 0.65:
        index = rng.choice(punctuation)
        if rng.random() < 0.5:
            del chars[index]
        else:
            chars[index] = rng.choice([" ", "-", "/", ""])
        return "".join(chars)
    return mutate_ocr(text, rng, force_single=True)


def extract_ascii_token(text: str) -> str:
    match = re.search(r"[A-Z]{1,4}[-+]?[A-Z0-9./]*(?:-\d+)?|(?:DN|GB)?\d{2,5}(?:-\d{2,4})?", text or "", re.I)
    return match.group(0) if match else ""


def build_candidates_with_hard_negatives(
    current: str,
    label: str,
    rng: random.Random,
    category: str,
) -> list[Candidate]:
    candidates = list(build_candidates(current))
    if category in {"keep", "repair", "glyph-issue"} and rng.random() < 0.70:
        add_conflict_candidate(candidates, label or current, rng)
    if category in {"unknown", "unsafe"} and rng.random() < 0.45:
        add_or_merge_candidate(
            candidates,
            Candidate(make_pseudo_shx_text(current, rng), "shx-pseudo-cjk", "font-glyph-noise", False),
        )
    return candidates


def candidates_from_source_or_build(source: SourceRecord, current: str) -> list[Candidate]:
    rows = source.training_record.get("candidates") or source.candidate_record.get("candidates") or []
    candidates: list[Candidate] = []
    for row in rows:
        text = str(row.get("text") or "")
        if not text:
            continue
        add_or_merge_candidate(
            candidates,
            Candidate(
                text=text,
                source=str(row.get("source") or ""),
                reason=str(row.get("reason") or ""),
                is_roundtrip=bool(row.get("isRoundTrip", False)),
            ),
        )
    if not candidates:
        candidates = list(build_candidates(current))
    return candidates


def add_conflict_candidate(candidates: list[Candidate], label: str, rng: random.Random) -> None:
    if not label:
        return
    conflict = rng.choice(
        [
            mutate_ocr(label, rng, force_single=True),
            drop_or_swap_punctuation(label, rng),
            partial_fullwidth(label, rng),
        ]
    )
    if conflict and conflict != label:
        add_or_merge_candidate(candidates, Candidate(conflict, "ocr-confusion", "similar-text-hard-negative", False))


def add_or_merge_candidate(candidates: list[Candidate], candidate: Candidate) -> None:
    if not candidate.text:
        return
    for existing in candidates:
        if existing.text == candidate.text:
            existing.add_source(candidate.source, candidate.reason, candidate.is_roundtrip)
            return
    candidates.append(candidate)


def candidate_contains_text(candidates: list[Candidate], text: str) -> bool:
    return any(candidate.text == text for candidate in candidates)


def has_conflicting_repair_target(source: SourceRecord) -> bool:
    if source.action != "repair" or not source.label_text:
        return False
    candidates = source.training_record.get("candidates") or []
    for candidate in candidates:
        if float(candidate.get("targetScore", 0.0) or 0.0) >= 1.0:
            return str(candidate.get("text") or "") != source.label_text
    return False


def add_reason_token(reason: str, token: str) -> str:
    if token in (reason or ""):
        return reason
    return f"{reason}; {token}" if reason else token


def ensure_selected_candidate(candidates: list[Candidate], selected_text: str, source_hint: str) -> int:
    if source_hint:
        for index, candidate in enumerate(candidates):
            if candidate.text == selected_text and source_hint.lower() in candidate.source.lower():
                return index
    for index, candidate in enumerate(candidates):
        if candidate.text == selected_text:
            return index
    add_or_merge_candidate(
        candidates,
        Candidate(
            selected_text,
            source_hint or "manual-review",
            "human-reviewed-correction" if source_hint == "manual-review" else "selected-target",
            True,
        ),
    )
    return len(candidates) - 1


def build_candidate_rows(candidates: list[Candidate], context: dict, selected_index: int) -> list[dict]:
    rows = []
    for index, candidate in enumerate(candidates):
        features = extract_features(context, candidate)
        rows.append(
            {
                "index": index,
                "text": candidate.text,
                "source": candidate.source,
                "reason": candidate.reason,
                "isRoundTrip": bool(candidate.is_roundtrip),
                "isNoOp": candidate.is_noop,
                "hasAiScore": False,
                "unsafeText": has_unsafe_text(candidate.text),
                "features": features,
                "targetScore": 1.0 if index == selected_index else 0.0,
            }
        )
    return rows


def strip_training_targets(candidate_rows: list[dict]) -> list[dict]:
    stripped = []
    for row in candidate_rows:
        item = dict(row)
        item.pop("targetScore", None)
        stripped.append(item)
    return stripped


def make_context(source: SourceRecord, spec: ClusterSpec, ordinal: int, rng: random.Random) -> dict:
    context = dict(source_context(source))
    context["currentText"] = spec.current_text
    context["handle"] = f"{0xA00000 + ordinal:X}"
    context["objectId"] = f"({9000000000000 + ordinal})"
    if "entityType" not in context:
        context["entityType"] = "DBText"
    if "isFromExternalReference" not in context:
        context["isFromExternalReference"] = rng.random() < 0.01
    if rng.random() < 0.10:
        context["textStyleName"] = choose_neighbor_context_value(source, "textStyleName", context.get("textStyleName") or "")
    if rng.random() < 0.08:
        context["layer"] = choose_neighbor_context_value(source, "layer", context.get("layer") or "")
    apply_native_decode_training_context(context, spec, ordinal, rng)
    return context


def apply_native_decode_training_context(context: dict, spec: ClusterSpec, ordinal: int, rng: random.Random) -> None:
    if spec.label_action != "repair":
        return

    source_hint = spec.selected_source_hint or first_repair_candidate_source(spec.candidates)
    source_family, applied_family = codepage_families_from_candidate_source(source_hint)
    if not source_family or not applied_family:
        source_family, applied_family = "gbk", "big5"

    is_ripple = spec.variant_rule == "ripple-context-repair"
    is_hook_raw = "hook-raw-stream" in source_hint.lower() or spec.variant_rule == "hook-raw-roundtrip"
    scope = "ripple" if is_ripple else ("object" if rng.random() < 0.70 else "cluster")
    evidence = {
        "hasEvidence": True,
        "familyMismatch": True,
        "scope": scope,
        "clusterKey": stable_id(
            "native-evidence",
            context.get("layer") or "",
            context.get("ownerBlockName") or "",
            context.get("textStyleName") or "",
            source_family,
            applied_family,
        ),
        "sourceCodePageFamily": source_family,
        "appliedCodePageFamily": applied_family,
        "hookHitType": "dbtext-raw-stream",
        "objectCorrelation": 0.0 if is_ripple else (1.0 if scope == "object" else 0.0),
        "clusterCorrelation": 0.45 if is_ripple else (1.0 if scope == "cluster" else 0.0),
    }
    context["nativeDecodeEvidence"] = evidence
    context["hasNativeDecodeEvidence"] = True
    context["nativeDecodeFamilyMismatch"] = True
    context["nativeDecodeEvidenceScope"] = scope
    context["nativeDecodeSourceCodePageFamily"] = source_family
    context["nativeDecodeAppliedCodePageFamily"] = applied_family
    context["nativeDecodeHookHitType"] = "dbtext-raw-stream"
    context["nativeDecodeObjectCorrelation"] = evidence["objectCorrelation"]
    context["nativeDecodeClusterCorrelation"] = evidence["clusterCorrelation"]

    if is_hook_raw:
        payload_key = stable_id("raw", spec.current_text, spec.label_text, str(ordinal))
        context["hasHookRawDecodeEvidence"] = True
        context["hookRawPayloadSha256"] = payload_key + payload_key
        context["hookRawPayloadLength"] = max(1, len(spec.current_text.encode("utf-8", errors="ignore")))
        context["hookPreferredDecodedText"] = spec.label_text
        context["hookRawCandidateSource"] = source_hint
        context["hookRawRoundTrip"] = spec.roundtrip_status != "manual-nonroundtrip"
        context["hookRawConfidence"] = 0.96 if context["hookRawRoundTrip"] else 0.72
        evidence.update(
            {
                "hasHookRawDecodeEvidence": context["hasHookRawDecodeEvidence"],
                "hookRawPayloadSha256": context["hookRawPayloadSha256"],
                "hookRawPayloadLength": context["hookRawPayloadLength"],
                "hookPreferredDecodedText": context["hookPreferredDecodedText"],
                "hookRawCandidateSource": context["hookRawCandidateSource"],
                "hookRawRoundTrip": context["hookRawRoundTrip"],
                "hookRawConfidence": context["hookRawConfidence"],
            }
        )

    if is_ripple:
        context["rippleContextText"] = spec.label_text
        context["rippleSeedCount"] = rng.randint(1, 5)
        context["rippleSeedQuality"] = round(rng.uniform(0.70, 0.98), 6)
        context["rippleDistanceRatio"] = round(rng.uniform(0.05, 0.85), 6)
        evidence.update(
            {
                "rippleContextText": context["rippleContextText"],
                "rippleSeedCount": context["rippleSeedCount"],
                "rippleSeedQuality": context["rippleSeedQuality"],
                "rippleDistanceRatio": context["rippleDistanceRatio"],
            }
        )


def first_repair_candidate_source(candidates: list[Candidate]) -> str:
    for candidate in candidates:
        if not candidate.is_noop:
            return candidate.source
    return ""


def codepage_families_from_candidate_source(source: str) -> tuple[str, str]:
    lower = (source or "").lower()
    if "big5-carrier-to-gbk" in lower:
        return "gbk", "big5"
    if "gbk-carrier-to-big5" in lower:
        return "big5", "gbk"
    if "utf8-carrier-to-gbk" in lower:
        return "gbk", "utf8"
    if "gbk-carrier-to-utf8" in lower:
        return "utf8", "gbk"
    return "", ""


def choose_context_source(
    corpus: SourceCorpus,
    representative: SourceRecord,
    rng: random.Random,
    member_index: int,
) -> SourceRecord:
    if member_index == 0 or rng.random() < 0.25:
        return representative
    if rng.random() < 0.70:
        same_action = [record for record in corpus.records if record.action == representative.action]
        if same_action:
            return rng.choice(same_action)
    return rng.choice(corpus.records)


def choose_neighbor_context_value(source: SourceRecord, field: str, fallback: str) -> str:
    value = source_context(source).get(field) or fallback
    return str(value)


def make_geometry(source: SourceRecord, ordinal: int, rng: random.Random) -> dict:
    geometry = json.loads(json.dumps(source.candidate_record.get("geometry") or {}))
    position = geometry.get("position")
    if isinstance(position, dict):
        position["x"] = round(float(position.get("x") or 0) + rng.uniform(-1500, 1500) + ordinal * 0.01, 6)
        position["y"] = round(float(position.get("y") or 0) + rng.uniform(-1500, 1500) - ordinal * 0.01, 6)
    if not geometry:
        geometry = {
            "position": {"x": float(ordinal), "y": 0.0, "z": 0.0},
            "height": 750.0,
            "rotation": 0.0,
            "widthFactor": 1.0,
        }
    return geometry


def make_drawing(source: SourceRecord) -> dict:
    drawing = dict(source.candidate_record.get("drawing") or {})
    if not drawing:
        context = source_context(source)
        drawing = {
            "path": context.get("drawingPath") or "",
            "fileName": context.get("drawingFileName") or "",
            "length": context.get("drawingLength") or 0,
            "lastWriteUtc": context.get("drawingLastWriteUtc") or "",
            "sha256": context.get("drawingSha256") or "",
        }
    return drawing


def make_problem_gate(spec: ClusterSpec) -> dict:
    if spec.label_action == "repair":
        return {"hasProblem": True, "reason": spec.variant_rule}
    if spec.label_action in {"unsafe", "unknown", "glyph-issue"}:
        return {"hasProblem": True, "reason": spec.variant_rule}
    return {"hasProblem": False, "reason": "no-suspicious-dbtext"}


def make_risk(spec: ClusterSpec, context: dict, candidate_rows: list[dict]) -> dict:
    return {
        "isFromXref": bool(context.get("isFromExternalReference", False)),
        "currentUnsafe": has_unsafe_text(spec.current_text),
        "candidateUnsafe": any(bool(row.get("unsafeText")) for row in candidate_rows),
        "hasNonRoundTrip": any(not bool(row.get("isRoundTrip")) for row in candidate_rows),
        "candidateConflict": len(candidate_rows) > 2,
        "highRisk": spec.risk_tier == "high",
        "riskTier": spec.risk_tier,
        "roundtripStatus": spec.roundtrip_status,
    }


def make_audit_row(
    ordinal: int,
    reviewed_record: dict,
    selected_candidate: dict,
    spec: ClusterSpec,
    context: dict,
) -> dict:
    return {
        "recordIndex": ordinal,
        "groupId": reviewed_record["groupId"],
        "sourcePackageId": spec.source_record.package_id,
        "sourceGroupId": spec.source_record.group_id,
        "sourceHandle": source_context(spec.source_record).get("handle") or "",
        "labelAction": spec.label_action,
        "currentText": spec.current_text,
        "candidateText": selected_candidate.get("text") or "",
        "labelText": spec.label_text,
        "selectedCandidateSource": selected_candidate.get("source") or "",
        "humanAcceptedCandidate": str(spec.human_accepted_candidate).lower(),
        "humanCorrectionReason": spec.human_reason,
        "riskTier": spec.risk_tier,
        "roundtripStatus": spec.roundtrip_status,
        "variantRule": spec.variant_rule,
        "clusterId": spec.cluster_id,
        "propagationSignature": spec.propagation_signature,
        "layer": context.get("layer") or "",
        "ownerBlockName": context.get("ownerBlockName") or "",
        "textStyleName": context.get("textStyleName") or "",
        "font": context.get("textStyleFileName") or "",
        "bigFont": context.get("textStyleBigFontFileName") or "",
    }


def make_generation_report(
    export_id: str,
    seed: int,
    quotas: dict[str, int],
    corpus: SourceCorpus,
    training_records: list[dict],
    candidate_records: list[dict],
    audit_rows: list[dict],
) -> dict:
    validation = validate_generated_records(training_records, audit_rows)
    return {
        "schema": "afr-glyphcore-high-value-generation-report-v1",
        "exportId": export_id,
        "ruleVersion": GENERATION_RULE_VERSION,
        "seed": seed,
        "requestedQuotas": quotas,
        "source": {
            "records": len(corpus.records),
            "packages": sorted({record.package_id for record in corpus.records}),
            "labelActions": counter_dict(record.action for record in corpus.records),
            "distribution": summarize_records([record.training_record for record in corpus.records]),
        },
        "generated": {
            "records": len(training_records),
            "candidateGroups": len(candidate_records),
            "auditRows": len(audit_rows),
            "distribution": summarize_records(training_records),
            "candidateSources": summarize_candidate_sources(training_records),
            "variantRules": counter_dict(record.get("variantRule") or "" for record in training_records),
            "roundtripStatus": counter_dict(record.get("roundtripStatus") or "" for record in training_records),
        },
        "validation": validation,
    }


def validate_generated_records(training_records: list[dict], audit_rows: list[dict]) -> dict:
    errors: list[str] = []
    group_ids = [str(record.get("groupId") or "") for record in training_records]
    handles = [str((record.get("context") or {}).get("handle") or "") for record in training_records]
    if len(training_records) != len(audit_rows):
        errors.append("audit row count does not match reviewed record count")
    if len(group_ids) != len(set(group_ids)):
        errors.append("duplicate groupId detected")
    if len(handles) != len(set(handles)):
        errors.append("duplicate handle detected")
    for index, record in enumerate(training_records, start=1):
        candidates = record.get("candidates") or []
        if not candidates:
            errors.append(f"record {index} has no candidates")
            continue
        positives = [candidate for candidate in candidates if float(candidate.get("targetScore", 0.0)) >= 1.0]
        if len(positives) != 1:
            errors.append(f"record {index} has {len(positives)} positive candidates")
        if record.get("schema") != REVIEWED_SCHEMA:
            errors.append(f"record {index} has unsupported schema")
        if record.get("featureSchema") != FEATURE_SCHEMA_VERSION:
            errors.append(f"record {index} has unsupported feature schema")
    return {
        "ok": not errors,
        "errors": errors[:50],
        "errorCount": len(errors),
        "recordCount": len(training_records),
        "uniqueGroupIds": len(set(group_ids)),
        "uniqueHandles": len(set(handles)),
    }


def summarize_records(records: list[dict]) -> dict:
    lengths = sorted(len(str(record.get("currentText") or "")) for record in records)
    return {
        "labelActions": counter_dict(record.get("labelAction") or "" for record in records),
        "textLength": quantiles(lengths),
        "layers": top_context(records, "layer"),
        "ownerBlocks": top_context(records, "ownerBlockName"),
        "textStyles": top_context(records, "textStyleName"),
        "fonts": top_context(records, "textStyleFileName"),
        "bigFonts": top_context(records, "textStyleBigFontFileName"),
    }


def summarize_candidate_sources(records: list[dict]) -> dict:
    counter = Counter()
    for record in records:
        for candidate in record.get("candidates") or []:
            counter[str(candidate.get("source") or "")] += 1
    return dict(counter.most_common(20))


def counter_dict(values: Iterable[str]) -> dict:
    return dict(Counter(str(value) for value in values).most_common())


def quantiles(values: list[int]) -> dict:
    if not values:
        return {"min": 0, "p50": 0, "p90": 0, "p99": 0, "max": 0}
    return {
        "min": values[0],
        "p50": values[int((len(values) - 1) * 0.50)],
        "p90": values[int((len(values) - 1) * 0.90)],
        "p99": values[int((len(values) - 1) * 0.99)],
        "max": values[-1],
    }


def top_context(records: list[dict], key: str) -> dict:
    counter = Counter(str((record.get("context") or {}).get(key) or "") for record in records)
    return dict(counter.most_common(12))


def write_generation_outputs(result: GenerationResult, build_features: bool = True) -> None:
    result.package_dir.mkdir(parents=True, exist_ok=True)
    result.reviewed_path.parent.mkdir(parents=True, exist_ok=True)
    result.training_path.parent.mkdir(parents=True, exist_ok=True)
    result.report_path.parent.mkdir(parents=True, exist_ok=True)

    write_jsonl(result.package_dir / "candidate_groups.jsonl", result.candidate_records)
    write_json(result.package_dir / "preview.json", {"records": result.candidate_records[:200]})
    write_json(result.manifest_path, make_manifest(result))
    write_audit_tsv(result.package_dir / "audit.tsv", result.audit_rows)
    write_jsonl(result.reviewed_path, result.records)
    write_jsonl(result.training_path, result.training_records)
    write_audit_tsv(result.audit_path, result.audit_rows)
    if build_features:
        write_feature_csv(result.training_path, result.features_path)
        result.report.setdefault("generated", {})["featureRows"] = count_csv_rows(result.features_path)
        result.report.setdefault("generated", {})["featurePath"] = str(result.features_path)
    write_json(result.report_path, result.report)


def make_manifest(result: GenerationResult) -> dict:
    return {
        "schema": "dbtext-ai-export-package-v1",
        "candidateGroupSchema": CANDIDATE_SCHEMA,
        "reviewedLabelSchema": REVIEWED_SCHEMA,
        "featureSchema": FEATURE_SCHEMA_VERSION,
        "exportId": result.export_id,
        "createdUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "commandName": "AFRGLYPHCOREEXPORTSELECT",
        "origin": "high-value-synthetic-from-real",
        "generationRuleVersion": GENERATION_RULE_VERSION,
        "drawing": {
            "fileName": "hook_cluster_ripple_v4_augmented_from_real_packages.dwg",
            "sourcePackages": sorted({row["sourcePackageId"] for row in result.audit_rows}),
        },
        "files": {
            "candidateGroups": "candidate_groups.jsonl",
            "audit": "audit.tsv",
            "reviewedLabels": str(result.reviewed_path),
            "trainingDataset": str(result.training_path),
            "features": str(result.features_path),
            "report": str(result.report_path),
        },
        "counts": {
            "scanned": len(result.candidate_records),
            "exported": len(result.candidate_records),
            "suspectedProblems": sum(1 for record in result.records if record.get("labelAction") != "keep"),
            "emptySkipped": 0,
            "xref": sum(1 for record in result.records if (record.get("context") or {}).get("isFromExternalReference")),
            "unsafeText": sum(1 for record in result.records if (record.get("risk") or {}).get("currentUnsafe")),
            "nonRoundTrip": sum(1 for record in result.records if (record.get("risk") or {}).get("hasNonRoundTrip")),
            "errors": 0,
        },
        "safety": {
            "readOnlyExport": True,
            "dwgWriteBack": False,
            "localOnly": True,
            "requiresHumanReviewBeforeTraining": False,
        },
    }


def write_feature_csv(input_path: Path, output_path: Path) -> None:
    rows = list(build_rows(input_path))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8-sig", newline="") as writer:
        fieldnames = METADATA_COLUMNS + [f"f{i:02d}_{name}" for i, name in enumerate(FEATURE_NAMES)]
        csv_writer = csv.DictWriter(writer, fieldnames=fieldnames)
        csv_writer.writeheader()
        csv_writer.writerows(rows)


def count_csv_rows(path: Path) -> int:
    with path.open("r", encoding="utf-8-sig", newline="") as reader:
        next(csv.reader(reader), None)
        return sum(1 for _ in reader)


def write_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2, sort_keys=True), encoding="utf-8")


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def read_jsonl(path: Path) -> list[dict]:
    rows = []
    with path.open("r", encoding="utf-8") as reader:
        for line in reader:
            if line.strip():
                rows.append(json.loads(line))
    return rows


def read_jsonl_by_group(path: Path) -> dict[str, dict]:
    return {str(row.get("groupId") or ""): row for row in read_jsonl(path)}


def write_jsonl(path: Path, records: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as writer:
        for record in records:
            writer.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
            writer.write("\n")


def write_audit_tsv(path: Path, rows: list[dict]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fieldnames = [
        "recordIndex",
        "groupId",
        "sourcePackageId",
        "sourceGroupId",
        "sourceHandle",
        "labelAction",
        "currentText",
        "candidateText",
        "labelText",
        "selectedCandidateSource",
        "humanAcceptedCandidate",
        "humanCorrectionReason",
        "riskTier",
        "roundtripStatus",
        "variantRule",
        "clusterId",
        "propagationSignature",
        "layer",
        "ownerBlockName",
        "textStyleName",
        "font",
        "bigFont",
    ]
    with path.open("w", encoding="utf-8-sig", newline="") as writer:
        tsv = csv.DictWriter(writer, fieldnames=fieldnames, delimiter="\t", extrasaction="ignore")
        tsv.writeheader()
        for row in rows:
            tsv.writerow({key: sanitize_tsv_value(row.get(key, "")) for key in fieldnames})


def sanitize_tsv_value(value: object) -> str:
    text = "" if value is None else str(value)
    return text.replace("\r\n", "\\n").replace("\r", "\\n").replace("\n", "\\n")


def source_context(source: SourceRecord) -> dict:
    return dict(source.training_record.get("context") or source.candidate_record.get("context") or {})


def usable_clean_text(text: str) -> bool:
    if not text:
        return False
    if len(text) > 120:
        return False
    if has_unsafe_text(text):
        return False
    visible = [ch for ch in text if not ch.isspace()]
    if not visible:
        return False
    symbols = sum(1 for ch in visible if not ch.isalnum() and not is_cjk(ch))
    return symbols / max(1, len(visible)) < 0.65


def is_cjk(ch: str) -> bool:
    code = ord(ch)
    return 0x3400 <= code <= 0x4DBF or 0x4E00 <= code <= 0x9FFF or 0xF900 <= code <= 0xFAFF


def dedupe_keep_order(values: Iterable[str]) -> list[str]:
    seen: set[str] = set()
    result: list[str] = []
    for value in values:
        if value and value not in seen:
            seen.add(value)
            result.append(value)
    return result


def stable_id(*parts: str) -> str:
    digest = hashlib.sha256("\0".join(parts).encode("utf-8")).hexdigest()
    return digest[:32]


def make_group_id(export_id: str, ordinal: int, current: str, label: str) -> str:
    return stable_id("group", export_id, f"{ordinal:08d}", current, label)


if __name__ == "__main__":
    raise SystemExit(main())
