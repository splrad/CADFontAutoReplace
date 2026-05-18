from __future__ import annotations

import argparse
import csv
import io
import json
import mimetypes
import os
import shutil
import subprocess
import sys
import threading
import time
import unicodedata
import uuid
import webbrowser
from copy import deepcopy
from datetime import datetime, timezone
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, quote, urlparse


LABEL_ACTIONS = {"repair", "keep", "unsafe", "unknown", "glyph-issue"}
REVIEWED_SCHEMA = "dbtext-ai-reviewed-label-v1"
LEGACY_TRAINING_SCHEMA = "dbtext-ai-candidates-v1"
TRAINING_DATASET_SCHEMA = "dbtext-ai-training-dataset-entry-v1"
REVIEW_CLUSTER_RULE_VERSION = "review-cluster-v3"
REVIEW_GROUP_RULE_VERSION = REVIEW_CLUSTER_RULE_VERSION
UI_DIST_DIR = Path(__file__).resolve().parent / "frontend" / "dist"
CAD_SEMANTIC_CHARS = set("水管井泵阀风压流排污喷淋消防电气设备材料表房库层标高详见安装系统屋顶支架压力自动宽高洞梁底施工图纸检修布置内排沟坡")
MOJIBAKE_MARKER_CHARS = set("囀窒齬阨僱砆獗囥馱芞祧潰韌詢階褽菁滅喀闄潯齬甋漹鎛蠻檥")
DEFAULT_TRAINING_OPTIONS = {
    "maxRounds": 650,
    "earlyStoppingRounds": 60,
    "seed": 20260512,
}


def display_text_for_record(record: dict) -> str:
    context = record.get("context") or {}
    explicit = str(record.get("displayText") or context.get("displayText") or "")
    if explicit:
        return explicit
    return raw_current_text(record)


def raw_current_text(record: dict) -> str:
    return str(record.get("currentText") or (record.get("context") or {}).get("currentText") or "")


def display_candidate_text(record: dict, candidate: dict) -> str:
    text = str(candidate.get("text") or candidate.get("candidateText") or "")
    if DatasetStore._is_noop_candidate(candidate) or text == raw_current_text(record):
        return display_text_for_record(record)
    return text


def candidate_encoding_path(record: dict, candidate: dict) -> str:
    source = str(candidate.get("source") or candidate.get("candidateSource") or "").strip()
    if source and source != "--":
        return source

    if DatasetStore._is_noop_candidate(candidate):
        return "原文"

    reason = str(candidate.get("reason") or candidate.get("candidateReason") or "").strip()
    if reason and reason != "--":
        return reason

    problem_reason = str((record.get("problemGate") or {}).get("reason") or "").strip()
    if problem_reason and problem_reason not in {"--", "no-suspicious-dbtext"}:
        return problem_reason

    return "候选路径未标明"


class DatasetStore:
    def __init__(self, dataset_root: Path, model_root: Path, package_dir: Path) -> None:
        self.dataset_root = dataset_root.resolve()
        self.model_root = model_root.resolve()
        self.package_dir = ensure_under(package_dir, self.dataset_root / "ExtractedCandidates")
        self.package_id = self.package_dir.name
        self.manifest_path = self.package_dir / "manifest.json"
        self.candidates_path = self.package_dir / "candidate_groups.jsonl"
        self.preview_path = self.package_dir / "preview.json"

        if not self.manifest_path.exists():
            raise FileNotFoundError(f"missing manifest: {self.manifest_path}")
        if not self.candidates_path.exists():
            raise FileNotFoundError(f"missing candidate groups: {self.candidates_path}")

        self.manifest = read_json(self.manifest_path)
        self.export_id = str(self.manifest.get("exportId") or self.package_id)
        self.review_dir = self.dataset_root / "ReviewedLabels"
        self.report_dir = self.dataset_root / "Reports"
        self.work_dir = self.dataset_root / ".work"
        self.review_dir.mkdir(parents=True, exist_ok=True)
        self.report_dir.mkdir(parents=True, exist_ok=True)
        self.work_dir.mkdir(parents=True, exist_ok=True)

        self.reviewed_path = self.review_dir / f"{self.export_id}_reviewed.jsonl"
        self.audit_path = self.review_dir / f"{self.export_id}_review_audit.tsv"
        self.training_dataset_path = self.dataset_root / "TrainingSets" / f"{self.export_id}_training_dataset.jsonl"
        self.training_dataset_path.parent.mkdir(parents=True, exist_ok=True)
        self.records = self._load_records()
        self.record_index = {str(item.get("groupId")): item for item in self.records}
        raw_reviewed = self._load_reviewed()
        self.training_dataset = self._load_training_dataset(raw_reviewed)
        self.reviewed = {
            group_id: record
            for group_id, record in raw_reviewed.items()
            if group_id not in self.training_dataset
        }
        self._review_groups_cache_key: tuple[object, ...] | None = None
        self._review_groups_cache: dict | None = None
        self.lock = threading.Lock()

    @property
    def features_path(self) -> Path:
        return self.dataset_root / "TrainingSets" / f"{self.export_id}_features.csv"

    @property
    def model_dir(self) -> Path:
        return self.model_root

    def _load_records(self) -> list[dict]:
        records: list[dict] = []
        with self.candidates_path.open("r", encoding="utf-8") as reader:
            for line_number, line in enumerate(reader, start=1):
                if not line.strip():
                    continue
                record = json.loads(line)
                group_id = record.get("groupId")
                if not group_id:
                    raise ValueError(f"line {line_number}: missing groupId")
                if not isinstance(record.get("candidates"), list) or not record["candidates"]:
                    raise ValueError(f"line {line_number}: candidates must be a non-empty array")
                records.append(record)
        return records

    def _load_reviewed(self) -> dict[str, dict]:
        reviewed: dict[str, dict] = {}
        if not self.reviewed_path.exists():
            return reviewed
        with self.reviewed_path.open("r", encoding="utf-8") as reader:
            for line in reader:
                if not line.strip():
                    continue
                record = json.loads(line)
                group_id = str(record.get("groupId") or "")
                if group_id:
                    reviewed[group_id] = record
        return reviewed

    def _load_training_dataset(self, raw_reviewed: dict[str, dict]) -> dict[str, dict]:
        training_records: dict[str, dict] = {}
        if self.training_dataset_path.exists():
            with self.training_dataset_path.open("r", encoding="utf-8") as reader:
                for line in reader:
                    if not line.strip():
                        continue
                    record = json.loads(line)
                    group_id = str(record.get("groupId") or "")
                    if group_id:
                        record.setdefault("trainingDatasetSchema", TRAINING_DATASET_SCHEMA)
                        training_records[group_id] = record
        if training_records:
            return training_records
        if self.features_path.exists():
            return self._load_training_dataset_from_features(raw_reviewed)
        if raw_reviewed:
            entered_utc = now_utc()
            build_id = f"reviewed-jsonl-import-{uuid.uuid4().hex}"
            return {
                group_id: self._build_training_dataset_entry(record, entered_utc, build_id, "reviewed-jsonl")
                for group_id, record in raw_reviewed.items()
            }
        return {}

    def _load_training_dataset_from_features(self, raw_reviewed: dict[str, dict]) -> dict[str, dict]:
        if not self.features_path.exists():
            return {}

        entered_utc = datetime.fromtimestamp(self.features_path.stat().st_mtime, timezone.utc).isoformat()
        rows_by_group: dict[str, list[dict]] = {}
        with self.features_path.open("r", encoding="utf-8-sig", newline="") as reader:
            for row in csv.DictReader(reader):
                group_id = str(row.get("group_id") or "")
                if group_id:
                    rows_by_group.setdefault(group_id, []).append(dict(row))

        training_records: dict[str, dict] = {}
        for group_id, rows in rows_by_group.items():
            if group_id in raw_reviewed:
                record = deepcopy(raw_reviewed[group_id])
            else:
                record = self._reconstruct_training_record_from_feature_rows(group_id, rows)
            if not record:
                continue
            record["schema"] = record.get("schema") or REVIEWED_SCHEMA
            record["trainingDatasetSchema"] = TRAINING_DATASET_SCHEMA
            record.setdefault("enteredTrainingUtc", entered_utc)
            record.setdefault("trainingSource", "features-csv")
            record.setdefault("trainingPackageId", self.package_id)
            record.setdefault("trainingExportId", self.export_id)
            training_records[group_id] = record
        return training_records

    def _reconstruct_training_record_from_feature_rows(self, group_id: str, rows: list[dict]) -> dict:
        source_record = deepcopy(self.record_index.get(group_id) or {})
        first = rows[0] if rows else {}
        context = deepcopy(source_record.get("context") or {})
        context.setdefault("handle", (source_record.get("context") or {}).get("handle") or "")
        context.setdefault("layer", first.get("layer") or "")
        context.setdefault("ownerBlockName", first.get("owner_block_name") or "")
        context.setdefault("textStyleName", first.get("text_style_name") or "")
        context.setdefault("textStyleFileName", first.get("font") or "")
        context.setdefault("textStyleBigFontFileName", first.get("bigfont") or "")
        context.setdefault("isFromExternalReference", str(first.get("is_from_xref") or "0") == "1")

        candidates: list[dict] = []
        selected_index: int | None = None
        for row in sorted(rows, key=lambda item: parse_candidate_index(item.get("candidate_index")) or 0):
            candidate_index = parse_candidate_index(row.get("candidate_index"))
            target_score = float(row.get("target_score") or 0.0)
            if target_score >= 1.0:
                selected_index = candidate_index
            candidates.append(
                {
                    "index": candidate_index if candidate_index is not None else len(candidates),
                    "text": row.get("candidate_text") or "",
                    "source": row.get("source") or "",
                    "reason": row.get("reason") or "",
                    "isRoundTrip": str(row.get("is_roundtrip") or "0") == "1",
                    "isNoOp": str(row.get("is_noop") or "0") == "1",
                    "targetScore": target_score,
                }
            )

        return {
            "schema": REVIEWED_SCHEMA,
            "featureSchema": first.get("feature_schema") or "dbtext-ai-features-v6",
            "groupId": group_id,
            "currentText": first.get("current_text") or source_record.get("currentText") or "",
            "labelAction": first.get("label_action") or "repair",
            "labelText": first.get("label_text") or "",
            "selectedCandidateIndex": selected_index,
            "reviewer": "feature-csv",
            "reviewedUtc": "",
            "origin": first.get("origin") or "features-csv",
            "originDetail": first.get("origin_detail") or "",
            "context": context,
            "candidates": candidates,
        }

    def data_payload(self) -> dict:
        return {
            "packageId": self.package_id,
            "manifest": self.manifest,
            "records": self.records,
            "reviewed": self.reviewed,
            "trainingDataset": self.training_dataset_payload(),
            "summary": self.summary(),
            "paths": {
                "package": str(self.package_dir),
                "reviewed": str(self.reviewed_path),
                "audit": str(self.audit_path),
                "trainingDataset": str(self.training_dataset_path),
                "features": str(self.features_path),
                "models": str(self.model_dir),
            },
        }

    def summary(self) -> dict:
        active_records = [record for record in self.records if str(record.get("groupId") or "") not in self.training_dataset]
        reviewed_count = len(self.reviewed)
        actions: dict[str, int] = {}
        problem_count = 0
        risk_count = 0
        for record in active_records:
            if (record.get("problemGate") or {}).get("hasProblem"):
                problem_count += 1
            if (record.get("risk") or {}).get("highRisk"):
                risk_count += 1
        for record in self.reviewed.values():
            action = str(record.get("labelAction") or "")
            actions[action] = actions.get(action, 0) + 1
        return {
            "total": len(active_records),
            "sourceTotal": len(self.records),
            "reviewed": reviewed_count,
            "remaining": max(0, len(active_records) - reviewed_count),
            "trained": len(self.training_dataset),
            "problems": problem_count,
            "highRisk": risk_count,
            "actions": actions,
        }

    def save_label(self, payload: dict) -> dict:
        group_id = str(payload.get("groupId") or "")
        action = str(payload.get("labelAction") or "")
        if group_id not in self.record_index:
            raise ValueError("unknown groupId")
        if group_id in self.training_dataset:
            raise ValueError("该记录已进入训练数据集。请先在训练数据集页面删除并回流后再复核。")
        if action not in LABEL_ACTIONS:
            raise ValueError("unsupported labelAction")

        reviewed = self._build_reviewed_record(
            self.record_index[group_id],
            action,
            payload.get("candidateIndex"),
            str(payload.get("labelText") or ""),
            str(payload.get("reviewer") or "").strip() or "developer",
            str(payload.get("note") or ""),
        )

        with self.lock:
            self.reviewed[group_id] = reviewed
            self._promote_reviewed_records_to_training_dataset([reviewed], "reviewed-jsonl")
            self._rewrite_training_dataset_file()
            self._rewrite_reviewed_files()
            self._invalidate_review_groups()

        return {"ok": True, "record": reviewed, "summary": self.summary()}

    def review_groups_payload(self) -> dict:
        cache_key = self._review_groups_cache_signature()
        if self._review_groups_cache_key == cache_key and self._review_groups_cache is not None:
            return deepcopy(self._review_groups_cache)

        groups: dict[tuple[object, ...], list[dict]] = {}
        for record in self.records:
            if str(record.get("groupId") or "") in self.training_dataset:
                continue
            groups.setdefault(self._review_group_key(record), []).append(record)

        summaries = [self._build_review_group_summary(records) for records in groups.values()]
        summaries.sort(
            key=lambda item: (
                1 if item["reviewStatus"] == "complete" else 0,
                0 if int(item.get("autoApplicableCount") or 0) > 0 else 1,
                0 if item.get("groupType") != "noop" else 1,
                -int(item.get("autoApplicableCount") or 0),
                -int(item.get("impactCount") or 0),
                str(item.get("candidateSource") or ""),
                str(item.get("candidateText") or ""),
            )
        )
        payload = {
            "ruleVersion": REVIEW_GROUP_RULE_VERSION,
            "summary": {
                "groups": len(summaries),
                "clusters": len(summaries),
                "direct": sum(1 for item in summaries if item["batchMode"] == "direct"),
                "sample": sum(1 for item in summaries if item["batchMode"] == "sample"),
                "manual": sum(1 for item in summaries if item["batchMode"] == "manual"),
                "complete": sum(1 for item in summaries if item["reviewStatus"] == "complete"),
                "autoApplicableGroups": sum(1 for item in summaries if int(item.get("autoApplicableCount") or 0) > 0),
                "autoApplicableClusters": sum(1 for item in summaries if int(item.get("autoApplicableCount") or 0) > 0),
                "reviewRequiredGroups": sum(1 for item in summaries if int(item.get("reviewRequiredCount") or 0) > 0),
                "riskSignalClusters": sum(1 for item in summaries if int(item.get("riskSignalCount") or 0) > 0),
                "humanPendingClusters": sum(
                    1
                    for item in summaries
                    if bool(item.get("humanReviewRequired")) and int(item.get("autoApplicableCount") or 0) > 0
                ),
                "humanPendingRecords": sum(
                    int(item.get("autoApplicableCount") or 0)
                    for item in summaries
                    if bool(item.get("humanReviewRequired"))
                ),
                "autoApplicableRecords": sum(int(item.get("autoApplicableCount") or 0) for item in summaries),
                "reviewRequiredRecords": sum(int(item.get("reviewRequiredCount") or 0) for item in summaries),
                "riskSignalRecords": sum(int(item.get("riskSignalCount") or 0) for item in summaries),
                "noopGroups": sum(1 for item in summaries if item.get("groupType") == "noop"),
                "noopClusters": sum(1 for item in summaries if item.get("groupType") == "noop"),
                "records": len(self.records),
                "reviewed": len(self.reviewed),
            },
            "clusters": summaries,
            "groups": summaries,
        }
        self._review_groups_cache_key = cache_key
        self._review_groups_cache = deepcopy(payload)
        return payload

    def review_clusters_payload(self) -> dict:
        return self.review_groups_payload()

    def review_group_records_payload(self, review_group_id: str, bucket: str) -> dict:
        group = self._find_review_group(str(review_group_id or ""))
        if group is None:
            raise ValueError("unknown reviewGroupId")
        summary = self._build_review_group_summary(group)
        partitions = self._partition_group_records(group, summary)
        bucket = str(bucket or "representatives")
        if bucket == "auto":
            records = partitions["auto"]
        elif bucket in {"review", "review-required", "conflict"}:
            records = self._risk_records(group)
        elif bucket == "reviewed":
            records = partitions["reviewed"]
        elif bucket == "risk":
            records = self._risk_records(group)
        elif bucket == "all":
            records = group
        else:
            records = self._representative_records(group, summary, partitions)
        return {
            "ok": True,
            "reviewGroupId": summary["id"],
            "reviewClusterId": summary["id"],
            "bucket": bucket,
            "count": len(records),
            "records": [self._record_digest(record, summary) for record in records],
            "summary": summary,
        }

    def review_cluster_records_payload(self, review_cluster_id: str, bucket: str) -> dict:
        return self.review_group_records_payload(review_cluster_id, bucket)

    def confirm_group(self, payload: dict) -> dict:
        review_group_id = str(payload.get("reviewClusterId") or payload.get("reviewGroupId") or "")
        action = str(payload.get("labelAction") or "")
        if action not in LABEL_ACTIONS:
            raise ValueError("unsupported labelAction")

        group = self._find_review_group(review_group_id)
        if group is None:
            raise ValueError("unknown reviewGroupId")
        summary = self._build_review_group_summary(group)
        apply_mode = str(payload.get("applyMode") or "cluster")
        candidate_index = payload.get("candidateIndex")
        if candidate_index is None or candidate_index == "":
            candidate_index = summary["recommendedCandidateIndex"]
        label_text = str(payload.get("labelText") or "")
        if action == "repair" and not label_text:
            label_text = str(summary.get("candidateText") or summary.get("currentText") or "")
        reviewer = str(payload.get("reviewer") or "").strip() or "developer"
        note = str(payload.get("note") or "")
        representative_id = str(
            payload.get("representativeGroupId")
            or payload.get("sourceRepresentativeGroupId")
            or (summary.get("representativeRecords") or [{}])[0].get("groupId")
            or ""
        )

        overwrite_reviewed = bool(payload.get("overwriteReviewed") or payload.get("includeReviewed"))
        targets = list(group) if overwrite_reviewed else [record for record in group if str(record.get("groupId") or "") not in self.reviewed]
        if apply_mode in {"highConfidence", "high-confidence"}:
            propagation_scope = "cluster-high-confidence"
        elif apply_mode in {"all", "full"}:
            propagation_scope = "cluster-all"
        else:
            propagation_scope = "cluster-sampled"

        if not targets:
            raise ValueError("该文本簇没有可写入记录。")

        batch_id = uuid.uuid4().hex
        source_handle = self._handle_for_group_id(representative_id) or (summary.get("representativeRecords") or [{}])[0].get("handle")
        propagation_rule = str(summary.get("propagationRule") or REVIEW_GROUP_RULE_VERSION)
        skip_counts = {
            "skippedConflictCount": 0,
            "skippedRiskCount": 0,
            "skippedRoundtripCount": 0,
            "skippedContextCount": 0,
            "skippedManualCount": int(summary.get("skippedManualCount") or 0),
        }
        batch_meta = {
            "batchId": batch_id,
            "batchRule": REVIEW_GROUP_RULE_VERSION,
            "batchReviewedSampleIds": [representative_id] if representative_id else [],
            "clusterReviewedSampleIds": [representative_id] if representative_id else [],
            "batchConfidenceLevel": summary["batchConfidenceLevel"],
            "origin": "browser-workbench-propagation",
            "appliedByGroup": True,
            "appliedByCluster": True,
            "propagationGroupId": summary["id"],
            "propagationClusterId": summary["id"],
            "propagationSignature": summary["propagationSignature"],
            "clusterRiskSummary": summary["risk"],
            "clusterContextSummary": summary["contextSummary"],
            "sourceRepresentativeHandle": source_handle,
            "sourceRepresentativeGroupId": representative_id,
            "appliedCount": len(targets),
            "propagationRule": propagation_rule,
            "propagationScope": propagation_scope,
            **skip_counts,
        }
        written: list[dict] = []
        with self.lock:
            for record in targets:
                group_id = str(record.get("groupId") or "")
                if not group_id or (group_id in self.reviewed and not overwrite_reviewed):
                    continue
                reviewed = self._build_reviewed_record(
                    record,
                    action,
                    candidate_index,
                    label_text,
                    reviewer,
                    note,
                    batch_meta,
                )
                self.reviewed[group_id] = reviewed
                written.append(reviewed)
            if not written:
                raise ValueError("该组没有可自动传播的记录。")
            self._promote_reviewed_records_to_training_dataset(written, "reviewed-jsonl")
            self._rewrite_training_dataset_file()
            self._rewrite_reviewed_files()
            self._invalidate_review_groups()

        updated_group = self._build_review_group_summary(group)
        return {
            "ok": True,
            "batchId": batch_id,
            "written": len(written),
            "appliedCount": len(written),
            "recordIds": [str(item.get("groupId") or "") for item in written],
            "records": written,
            "summary": self.summary(),
            "reviewGroup": updated_group,
            "reviewCluster": updated_group,
            **skip_counts,
        }

    def confirm_cluster(self, payload: dict) -> dict:
        return self.confirm_group(payload)

    def confirm_review_table_rows(self, payload: dict) -> dict:
        rows = payload.get("rows") or []
        if not isinstance(rows, list) or not rows:
            raise ValueError("请选择至少一行复核数据。")

        reviewer = str(payload.get("reviewer") or "").strip() or "developer"
        note = str(payload.get("note") or "manual-table-review")
        overwrite_reviewed = bool(payload.get("overwriteReviewed", True))
        batch_id = uuid.uuid4().hex
        written: list[dict] = []
        errors: list[dict] = []

        with self.lock:
            for row in rows:
                if not isinstance(row, dict):
                    errors.append({"reviewGroupId": "", "error": "invalid row payload"})
                    continue
                review_group_id = str(row.get("reviewGroupId") or row.get("reviewClusterId") or "")
                try:
                    action = str(row.get("labelAction") or "")
                    if action not in LABEL_ACTIONS:
                        raise ValueError("unsupported labelAction")
                    group = self._find_review_group(review_group_id)
                    if group is None:
                        raise ValueError("unknown reviewGroupId")
                    summary = self._build_review_group_summary(group)
                    candidate_index = row.get("candidateIndex")
                    if candidate_index is None or candidate_index == "":
                        candidate_index = summary["recommendedCandidateIndex"]
                    label_text = str(row.get("labelText") or "")
                    if action == "repair" and not label_text:
                        label_text = str(summary.get("candidateText") or summary.get("currentText") or "")
                    targets = list(group) if overwrite_reviewed else [
                        record for record in group if str(record.get("groupId") or "") not in self.reviewed
                    ]
                    if not targets:
                        raise ValueError("该文本簇没有可写入记录。")

                    representative_id = str(
                        row.get("representativeGroupId")
                        or row.get("sourceRepresentativeGroupId")
                        or (summary.get("representativeRecords") or [{}])[0].get("groupId")
                        or ""
                    )
                    source_handle = self._handle_for_group_id(representative_id) or (summary.get("representativeRecords") or [{}])[0].get("handle")
                    batch_meta = {
                        "batchId": batch_id,
                        "batchRule": REVIEW_GROUP_RULE_VERSION,
                        "batchReviewedSampleIds": [representative_id] if representative_id else [],
                        "clusterReviewedSampleIds": [representative_id] if representative_id else [],
                        "batchConfidenceLevel": summary["batchConfidenceLevel"],
                        "origin": "browser-workbench-table-review",
                        "appliedByGroup": True,
                        "appliedByCluster": True,
                        "propagationGroupId": summary["id"],
                        "propagationClusterId": summary["id"],
                        "propagationSignature": summary["propagationSignature"],
                        "clusterRiskSummary": summary["risk"],
                        "clusterContextSummary": summary["contextSummary"],
                        "sourceRepresentativeHandle": source_handle,
                        "sourceRepresentativeGroupId": representative_id,
                        "appliedCount": len(targets),
                        "propagationRule": str(summary.get("propagationRule") or REVIEW_GROUP_RULE_VERSION),
                        "propagationScope": "manual-table-review",
                        "humanReviewMode": "table-review-edit" if summary["reviewStatus"] == "complete" else "table-review-confirm",
                        "skippedConflictCount": 0,
                        "skippedRiskCount": 0,
                        "skippedRoundtripCount": 0,
                        "skippedContextCount": 0,
                        "skippedManualCount": 0,
                    }
                    for record in targets:
                        group_id = str(record.get("groupId") or "")
                        if not group_id or (group_id in self.reviewed and not overwrite_reviewed):
                            continue
                        reviewed = self._build_reviewed_record(
                            record,
                            action,
                            candidate_index,
                            label_text,
                            reviewer,
                            note,
                            batch_meta,
                        )
                        self.reviewed[group_id] = reviewed
                        written.append(reviewed)
                except Exception as exc:
                    errors.append({"reviewGroupId": review_group_id, "error": f"{type(exc).__name__}: {exc}"})

            if not written and errors:
                raise ValueError("; ".join(str(item["error"]) for item in errors[:3]))
            if written:
                self._promote_reviewed_records_to_training_dataset(written, "reviewed-jsonl")
                self._rewrite_training_dataset_file()
                self._rewrite_reviewed_files()
                self._invalidate_review_groups()

        return {
            "ok": True,
            "batchId": batch_id,
            "reviewedGroups": len(rows) - len(errors),
            "written": len(written),
            "recordIds": [str(item.get("groupId") or "") for item in written],
            "errors": errors,
            "summary": self.summary(),
        }

    def save_batch_label(self, payload: dict) -> dict:
        payload = dict(payload)
        payload.setdefault("applyMode", "safe")
        return self.confirm_group(payload)

    def save_batch_label_groups(self, payload: dict) -> dict:
        requested_ids = [
            str(value)
            for value in (payload.get("reviewClusterIds") or payload.get("reviewGroupIds") or [])
            if str(value)
        ]
        try:
            max_groups = int(payload.get("maxGroups") or 0)
        except (TypeError, ValueError):
            max_groups = 0
        reviewer = str(payload.get("reviewer") or "").strip() or "developer"
        note = str(payload.get("note") or "bulk-direct-batch")

        groups_by_id: dict[str, tuple[list[dict], dict]] = {}
        for group_summary in self.review_groups_payload().get("clusters", []):
            group_id = str(group_summary.get("id") or "")
            if not group_id:
                continue
            if requested_ids and group_id not in requested_ids:
                continue
            if group_summary.get("batchMode") != "direct" or int(group_summary.get("unreviewedCount") or 0) <= 0:
                continue
            group = self._find_review_group(group_id)
            if group is None:
                continue
            groups_by_id[group_id] = (group, self._build_review_group_summary(group))
            if max_groups > 0 and len(groups_by_id) >= max_groups:
                break

        if not groups_by_id:
            raise ValueError("没有可批量确认的低风险强一致分组。")

        batch_id = uuid.uuid4().hex
        written: list[dict] = []
        with self.lock:
            for group_id, (group, summary) in groups_by_id.items():
                batch_meta = {
                    "batchId": batch_id,
                    "batchRule": REVIEW_GROUP_RULE_VERSION,
                    "batchReviewedSampleIds": [],
                    "clusterReviewedSampleIds": [],
                    "batchConfidenceLevel": "bulk-direct",
                    "origin": "browser-workbench-bulk-direct",
                    "appliedByGroup": True,
                    "appliedByCluster": True,
                    "propagationGroupId": summary["id"],
                    "propagationClusterId": summary["id"],
                    "propagationSignature": summary["propagationSignature"],
                    "clusterRiskSummary": summary["risk"],
                    "clusterContextSummary": summary["contextSummary"],
                    "appliedCount": int(summary.get("unreviewedCount") or 0),
                    "propagationRule": str(summary.get("propagationRule") or REVIEW_GROUP_RULE_VERSION),
                    "propagationScope": "cluster-bulk-direct",
                }
                for record in group:
                    record_id = str(record.get("groupId") or "")
                    if not record_id or record_id in self.reviewed:
                        continue
                    reviewed = self._build_reviewed_record(
                        record,
                        str(summary.get("recommendedAction") or "repair"),
                        summary.get("recommendedCandidateIndex"),
                        str(summary.get("candidateText") or summary.get("currentText") or ""),
                        reviewer,
                        note,
                        batch_meta,
                    )
                    self.reviewed[record_id] = reviewed
                    written.append(reviewed)
            if not written:
                raise ValueError("选中的分组没有未审核记录可批量写入。")
            self._promote_reviewed_records_to_training_dataset(written, "reviewed-jsonl")
            self._rewrite_training_dataset_file()
            self._rewrite_reviewed_files()
            self._invalidate_review_groups()

        return {
            "ok": True,
            "batchId": batch_id,
            "written": len(written),
            "groups": len(groups_by_id),
            "clusters": len(groups_by_id),
            "recordIds": [str(item.get("groupId") or "") for item in written],
            "records": written,
            "summary": self.summary(),
        }

    def rollback_batch(self, payload: dict) -> dict:
        batch_id = str(payload.get("batchId") or "").strip()
        cluster_id = str(payload.get("propagationClusterId") or payload.get("reviewClusterId") or "").strip()
        if not batch_id and not cluster_id:
            raise ValueError("batchId or propagationClusterId is required")

        removed_ids: list[str] = []
        with self.lock:
            for group_id, record in list(self.reviewed.items()):
                if batch_id and str(record.get("batchId") or "") != batch_id:
                    continue
                if cluster_id and str(record.get("propagationClusterId") or record.get("propagationGroupId") or "") != cluster_id:
                    continue
                removed_ids.append(group_id)
                del self.reviewed[group_id]
            for group_id, record in list(self.training_dataset.items()):
                if batch_id and str(record.get("batchId") or "") != batch_id:
                    continue
                if cluster_id and str(record.get("propagationClusterId") or record.get("propagationGroupId") or "") != cluster_id:
                    continue
                if group_id not in removed_ids:
                    removed_ids.append(group_id)
                del self.training_dataset[group_id]
            if not removed_ids:
                raise ValueError("没有找到可回滚的批量标注记录。")
            self._rewrite_training_dataset_file()
            self._rewrite_features_without_group_ids(set(removed_ids))
            self._rewrite_reviewed_files()
            self._invalidate_review_groups()

        return {"ok": True, "batchId": batch_id, "removed": len(removed_ids), "recordIds": removed_ids, "summary": self.summary()}

    def reset_review_rows(self, payload: dict) -> dict:
        raw_ids = (
            payload.get("reviewGroupIds")
            or payload.get("reviewClusterIds")
            or payload.get("groupIds")
            or []
        )
        if isinstance(raw_ids, str):
            raw_ids = [raw_ids]
        if not isinstance(raw_ids, list):
            raise ValueError("reviewGroupIds must be an array")
        requested_ids = {str(value or "").strip() for value in raw_ids if str(value or "").strip()}
        if not requested_ids:
            raise ValueError("请选择要重置的复核行。")

        removed_ids: list[str] = []
        with self.lock:
            for group_id, record in list(self.reviewed.items()):
                if self._record_matches_reset_ids(group_id, record, requested_ids):
                    removed_ids.append(group_id)
                    del self.reviewed[group_id]
            for group_id, record in list(self.training_dataset.items()):
                if self._record_matches_reset_ids(group_id, record, requested_ids):
                    if group_id not in removed_ids:
                        removed_ids.append(group_id)
                    del self.training_dataset[group_id]
            if not removed_ids:
                raise ValueError("没有找到可重置的复核或训练记录。")
            self._rewrite_training_dataset_file()
            self._rewrite_features_without_group_ids(set(removed_ids))
            self._rewrite_reviewed_files()
            self._invalidate_review_groups()

        return {
            "ok": True,
            "reset": len(removed_ids),
            "recordIds": removed_ids,
            "summary": self.summary(),
            "trainingDataset": self.training_dataset_payload(),
        }

    def training_dataset_payload(self) -> dict:
        feature_counts = self._feature_row_counts()
        records = [
            self._training_dataset_digest(record, feature_counts.get(str(record.get("groupId") or ""), 0))
            for record in self.training_dataset.values()
        ]
        records.sort(key=lambda item: (str(item.get("enteredTrainingUtc") or ""), str(item.get("groupId") or "")), reverse=True)

        actions: dict[str, int] = {}
        layers: dict[str, int] = {}
        fonts: dict[str, int] = {}
        for item in records:
            action = str(item.get("labelAction") or "")
            layer = str(item.get("layer") or "")
            font = str(item.get("font") or item.get("textStyleName") or "")
            actions[action] = actions.get(action, 0) + 1
            if layer:
                layers[layer] = layers.get(layer, 0) + 1
            if font:
                fonts[font] = fonts.get(font, 0) + 1

        return {
            "schema": "dbtext-ai-training-dataset-v1",
            "packageId": self.package_id,
            "exportId": self.export_id,
            "path": str(self.training_dataset_path),
            "featurePath": str(self.features_path),
            "records": records,
            "summary": {
                "total": len(records),
                "featureRows": sum(feature_counts.values()),
                "labelActions": actions,
                "layers": layers,
                "fonts": fonts,
            },
        }

    def export_training_dataset(self, export_format: str) -> dict:
        export_format = str(export_format or "csv").lower()
        if export_format not in {"csv", "jsonl"}:
            raise ValueError("format must be csv or jsonl")
        if export_format == "jsonl":
            lines = [
                json.dumps(record, ensure_ascii=False, separators=(",", ":"))
                for record in self._ordered_training_records(self.training_dataset.values())
            ]
            data = ("\n".join(lines) + ("\n" if lines else "")).encode("utf-8")
            return {
                "filename": f"{self.export_id}_training_dataset.jsonl",
                "contentType": "application/x-ndjson; charset=utf-8",
                "data": data,
            }

        rows = self.training_dataset_payload().get("records") or []
        fieldnames = [
            "groupId",
            "currentText",
            "labelText",
            "labelAction",
            "candidateText",
            "candidateSource",
            "layer",
            "font",
            "bigFont",
            "drawingFileName",
            "handle",
            "reviewer",
            "reviewedUtc",
            "enteredTrainingUtc",
            "trainingSource",
            "featureRows",
        ]
        buffer = io.StringIO()
        writer = csv.DictWriter(buffer, fieldnames=fieldnames, extrasaction="ignore")
        writer.writeheader()
        for row in rows:
            writer.writerow({key: row.get(key, "") for key in fieldnames})
        return {
            "filename": f"{self.export_id}_training_dataset.csv",
            "contentType": "text/csv; charset=utf-8-sig",
            "data": ("\ufeff" + buffer.getvalue()).encode("utf-8"),
        }

    def import_training_dataset(self, payload: dict) -> dict:
        content = str(payload.get("content") or "")
        if not content.strip():
            raise ValueError("导入内容为空。")
        import_format = str(payload.get("format") or "jsonl").lower()
        if import_format not in {"jsonl", "ndjson"}:
            raise ValueError("训练集导入仅支持 JSONL。CSV 仅用于人工审阅导出。")
        mode = str(payload.get("mode") or "merge").lower()
        if mode not in {"merge", "replace"}:
            raise ValueError("mode must be merge or replace")

        imported: list[dict] = []
        errors: list[dict] = []
        build_id = f"jsonl-import-{uuid.uuid4().hex}"
        entered_utc = now_utc()
        for line_number, line in enumerate(content.splitlines(), start=1):
            if not line.strip():
                continue
            try:
                record = json.loads(line)
                if not isinstance(record, dict):
                    raise ValueError("record must be an object")
                imported.append(self._coerce_imported_training_record(record, entered_utc, build_id))
            except Exception as exc:
                errors.append({"line": line_number, "error": f"{type(exc).__name__}: {exc}"})

        if errors:
            return {
                "ok": False,
                "imported": 0,
                "errors": errors,
                "trainingDataset": self.training_dataset_payload(),
            }
        if not imported:
            raise ValueError("没有可导入的 JSONL 记录。")

        with self.lock:
            if mode == "replace":
                self.training_dataset = {}
                self.reviewed = {}
                if self.features_path.exists():
                    self.features_path.unlink()
            for record in imported:
                group_id = str(record.get("groupId") or "")
                self.training_dataset[group_id] = record
                self.reviewed.pop(group_id, None)
            self._rewrite_training_dataset_file()
            self._rewrite_reviewed_files()
            self._invalidate_review_groups()

        return {
            "ok": True,
            "imported": len(imported),
            "errors": [],
            "summary": self.summary(),
            "trainingDataset": self.training_dataset_payload(),
        }

    def prepare_training_dataset_build(self) -> tuple[list[dict], list[str], str]:
        build_id = uuid.uuid4().hex
        entered_utc = now_utc()
        with self.lock:
            merged = {group_id: deepcopy(record) for group_id, record in self.training_dataset.items()}
            promoted_ids: list[str] = []
            for group_id, record in self.reviewed.items():
                merged[group_id] = self._build_training_dataset_entry(record, entered_utc, build_id, "reviewed-jsonl")
                promoted_ids.append(group_id)
        records = self._ordered_training_records(merged.values())
        return records, promoted_ids, build_id

    def commit_training_dataset_build(self, records: list[dict], promoted_ids: list[str], features_tmp_path: Path) -> None:
        with self.lock:
            self._commit_training_dataset_records_unlocked(records, promoted_ids)
            self.features_path.parent.mkdir(parents=True, exist_ok=True)
            features_tmp_path.replace(self.features_path)
            self._invalidate_review_groups()

    def commit_training_dataset_records(self, records: list[dict], promoted_ids: list[str]) -> None:
        with self.lock:
            self._commit_training_dataset_records_unlocked(records, promoted_ids)
            self._invalidate_review_groups()

    def _commit_training_dataset_records_unlocked(self, records: list[dict], promoted_ids: list[str]) -> None:
        self.training_dataset = {
            str(record.get("groupId") or ""): record
            for record in records
            if str(record.get("groupId") or "")
        }
        for group_id in promoted_ids:
            self.reviewed.pop(group_id, None)
        self._rewrite_training_dataset_file()
        self._rewrite_reviewed_files()

    def delete_training_dataset_records(self, payload: dict) -> dict:
        group_ids = payload.get("groupIds")
        if group_ids is None:
            group_ids = [payload.get("groupId")]
        if not isinstance(group_ids, list):
            raise ValueError("groupIds must be an array")
        requested_ids = [str(group_id or "").strip() for group_id in group_ids if str(group_id or "").strip()]
        if not requested_ids:
            raise ValueError("请选择要删除的训练数据。")

        removed: list[dict] = []
        missing: list[str] = []
        with self.lock:
            for group_id in requested_ids:
                record = self.training_dataset.pop(group_id, None)
                if record is None:
                    missing.append(group_id)
                    continue
                removed.append(record)
                self.reviewed.pop(group_id, None)
            if not removed:
                raise ValueError("没有找到可删除的训练数据。")
            removed_ids = {str(record.get("groupId") or "") for record in removed}
            self._rewrite_training_dataset_file()
            self._rewrite_features_without_group_ids(removed_ids)
            self._rewrite_reviewed_files()
            self._invalidate_review_groups()

        return {
            "ok": True,
            "removed": len(removed),
            "recordIds": [str(record.get("groupId") or "") for record in removed],
            "missing": missing,
            "reflowStatus": "pending-review",
            "summary": self.summary(),
            "trainingDataset": self.training_dataset_payload(),
        }

    def _build_reviewed_record(
        self,
        source_record: dict,
        action: str,
        candidate_index: object,
        label_text: str,
        reviewer: str,
        note: str,
        metadata: dict | None = None,
    ) -> dict:
        record = deepcopy(source_record)
        candidates = deepcopy(record.get("candidates") or [])
        selected_index = parse_candidate_index(candidate_index)
        current_text = str(record.get("currentText") or (record.get("context") or {}).get("currentText") or "")

        if action == "repair":
            if label_text:
                matched_index = self._find_candidate_by_text(candidates, label_text)
                if matched_index is not None:
                    selected_index = matched_index
                else:
                    selected_index = None
            elif selected_index is not None and 0 <= selected_index < len(candidates):
                selected_text = str(candidates[selected_index].get("text") or "")
                label_text = label_text or selected_text
            if not label_text:
                raise ValueError("repair labels require labelText or a selected candidate")
            if selected_index is None or selected_index < 0 or selected_index >= len(candidates):
                selected_index = self._find_candidate_by_text(candidates, label_text)
            if selected_index is None:
                selected_index = len(candidates)
                candidates.append(
                    {
                        "index": selected_index,
                        "text": label_text,
                        "source": "manual-review",
                        "reason": "human-reviewed-correction",
                        "isRoundTrip": True,
                        "isNoOp": False,
                        "hasAiScore": False,
                        "unsafeText": False,
                    }
                )
        elif action == "keep":
            label_text = current_text
            selected_index = self._find_noop_candidate(candidates)
        else:
            label_text = label_text or current_text
            selected_index = self._find_noop_candidate(candidates)

        for index, candidate in enumerate(candidates):
            candidate["index"] = index
            candidate["targetScore"] = 1.0 if selected_index == index else 0.0

        record["schema"] = REVIEWED_SCHEMA
        record["legacyTrainingSchema"] = LEGACY_TRAINING_SCHEMA
        record["labelAction"] = action
        record["labelText"] = label_text
        record["selectedCandidateIndex"] = selected_index
        record["reviewer"] = reviewer
        record["reviewedUtc"] = now_utc()
        record["origin"] = "browser-workbench"
        record["originDetail"] = note
        record["candidates"] = candidates
        if metadata:
            record.update(metadata)
        return record

    def _build_training_dataset_entry(self, reviewed_record: dict, entered_utc: str, build_id: str, source: str) -> dict:
        record = deepcopy(reviewed_record)
        record["schema"] = record.get("schema") or REVIEWED_SCHEMA
        record["trainingDatasetSchema"] = TRAINING_DATASET_SCHEMA
        record["enteredTrainingUtc"] = entered_utc
        record["trainingFeatureBuildId"] = build_id
        record["trainingSource"] = source
        record["trainingPackageId"] = self.package_id
        record["trainingExportId"] = self.export_id
        return record

    def _promote_reviewed_records_to_training_dataset(self, reviewed_records: list[dict], source: str) -> list[str]:
        entered_utc = now_utc()
        build_id = f"pending-feature-{uuid.uuid4().hex}"
        promoted_ids: list[str] = []
        for reviewed_record in reviewed_records:
            group_id = str(reviewed_record.get("groupId") or "")
            if not group_id:
                continue
            self.training_dataset[group_id] = self._build_training_dataset_entry(
                reviewed_record,
                entered_utc,
                build_id,
                source,
            )
            self.reviewed.pop(group_id, None)
            promoted_ids.append(group_id)
        return promoted_ids

    def _review_group_key(self, record: dict) -> tuple[object, ...]:
        candidate = self._review_candidate(record)
        action = self._recommended_action(candidate)
        return (
            str(record.get("currentText") or ""),
            str(candidate.get("text") or ""),
            candidate_encoding_path(record, candidate),
            action,
        )

    def _build_review_group_summary(self, records: list[dict]) -> dict:
        first = records[0]
        context = first.get("context") or {}
        candidate_index, candidate = self._review_candidate_with_index(first)
        current_text = raw_current_text(first)
        display_text = display_text_for_record(first)
        candidate_text = display_candidate_text(first, candidate)
        candidate_source = candidate_encoding_path(first, candidate)
        group_type = "noop" if self._is_noop_candidate(candidate) else "encoding"
        action = self._recommended_action(candidate)
        group_id = uuid.uuid5(uuid.NAMESPACE_URL, REVIEW_GROUP_RULE_VERSION + "\0" + "\0".join(map(str, self._review_group_key(first)))).hex
        partitions = self._partition_group_records(records)
        reviewed_records = partitions["reviewed"]
        auto_records = partitions["auto"]
        risk_totals = self._risk_totals(records)
        context_summary = self._context_summary(records)
        warnings = self._review_group_blockers(records)
        unreviewed_count = len(auto_records)
        risk_records = self._risk_records(records)
        risk_signal_count = len(risk_records)
        context_variant_count = int(context_summary.get("uniqueContexts") or 0)
        human_review_required = True
        auto_review_policy = "human-confirm"
        suggested_sample_count = 0
        if risk_signal_count > 0 or context_variant_count > 1:
            batch_mode = "sample"
            confidence = "sample-reviewed"
        else:
            batch_mode = "direct"
            confidence = "text-pattern-direct"
        representatives = self._representative_records(records, None, partitions)
        source_variants = self._source_text_variants(records)
        propagation_signature = "|".join(map(str, self._review_group_key(first)))
        return {
            "id": group_id,
            "clusterId": group_id,
            "ruleVersion": REVIEW_GROUP_RULE_VERSION,
            "groupType": group_type,
            "clusterType": group_type,
            "encodingPath": candidate_source,
            "count": len(records),
            "impactCount": len(records),
            "reviewedCount": len(reviewed_records),
            "unreviewedCount": unreviewed_count,
            "autoApplicableCount": len(auto_records),
            "reviewRequiredCount": 0,
            "riskSignalCount": risk_signal_count,
            "alreadyReviewedCount": len(reviewed_records),
            "reviewStatus": "complete" if unreviewed_count == 0 else ("partial" if reviewed_records else "pending"),
            "batchMode": batch_mode,
            "batchConfidenceLevel": confidence,
            "autoReviewPolicy": auto_review_policy,
            "humanReviewRequired": human_review_required,
            "trainingSampleEligible": False,
            "suggestedSampleCount": suggested_sample_count,
            "canBatch": bool(auto_records),
            "canConfirm": bool(auto_records) and human_review_required,
            "canApplyAll": bool(auto_records) and human_review_required,
            "canBulkDirect": bool(auto_records) and batch_mode == "direct" and human_review_required,
            "blockers": warnings,
            "warnings": warnings,
            "recommendedAction": action,
            "recommendedCandidateIndex": candidate_index,
            "currentText": display_text,
            "rawCurrentText": current_text,
            "displayText": display_text,
            "sourcePatternLabel": f"{len(source_variants)} 种原文模式" if len(source_variants) > 1 else display_text,
            "sourceTextVariants": source_variants,
            "candidateText": candidate_text,
            "rawCandidateText": str(candidate.get("text") or ""),
            "candidateSource": candidate_source,
            "candidateReason": str(candidate.get("reason") or ""),
            "isRoundTrip": bool(candidate.get("isRoundTrip")),
            "risk": risk_totals,
            "riskSummary": risk_totals,
            "contextSummary": context_summary,
            "propagationSignature": propagation_signature,
            "context": {
                "baseLayer": context.get("layer"),
                "layer": context.get("layer"),
                "textStyleName": context.get("textStyleName"),
                "textStyleFileName": context.get("textStyleFileName"),
                "textStyleBigFontFileName": context.get("textStyleBigFontFileName"),
                "ownerBlockName": context.get("ownerBlockName"),
            },
            "appliedCount": len(reviewed_records),
            "skippedConflictCount": 0,
            "skippedRiskCount": 0,
            "skippedRoundtripCount": 0,
            "skippedContextCount": 0,
            "skippedManualCount": len(reviewed_records),
            "propagationRule": self._propagation_rule_text(first),
            "recordIds": [str(record.get("groupId") or "") for record in records],
            "sampleRecords": representatives,
            "representativeRecords": representatives,
            "autoApplicableRecords": [self._record_digest(record) for record in auto_records[:8]],
            "reviewRequiredRecords": [self._record_digest(record) for record in risk_records[:8]],
            "riskSignalRecords": [self._record_digest(record) for record in risk_records[:8]],
            "alreadyReviewedRecords": [self._record_digest(record) for record in reviewed_records[:8]],
        }

    def _review_group_blockers(self, records: list[dict]) -> list[str]:
        blockers: set[str] = set()
        for record in records:
            blockers.update(self._cluster_risk_reasons(record, records[0]))
        return sorted(blockers)

    @staticmethod
    def _recommended_candidate(record: dict) -> dict:
        candidates = record.get("candidates") or []
        return candidates[0] if candidates else {}

    @staticmethod
    def _recommended_candidate_with_index(record: dict) -> tuple[int | None, dict]:
        candidates = record.get("candidates") or []
        return (0, candidates[0]) if candidates else (None, {})

    def _review_candidate(self, record: dict) -> dict:
        return self._review_candidate_with_index(record)[1]

    def _review_candidate_with_index(self, record: dict) -> tuple[int | None, dict]:
        if self._should_prefer_noop_candidate(record):
            noop_index = self._find_noop_candidate(record.get("candidates") or [])
            if noop_index is not None:
                candidates = record.get("candidates") or []
                if 0 <= noop_index < len(candidates):
                    return noop_index, candidates[noop_index]
        return self._recommended_candidate_with_index(record)

    def _record_digest(self, record: dict, summary: dict | None = None) -> dict:
        context = record.get("context") or {}
        candidate = self._review_candidate(record)
        reviewed = self.reviewed.get(str(record.get("groupId") or ""))
        candidate_source = candidate_encoding_path(record, candidate)
        return {
            "groupId": record.get("groupId"),
            "handle": context.get("handle"),
            "currentText": display_text_for_record(record),
            "rawCurrentText": raw_current_text(record),
            "displayText": display_text_for_record(record),
            "layer": context.get("layer"),
            "textStyleName": context.get("textStyleName"),
            "textStyleFileName": context.get("textStyleFileName"),
            "textStyleBigFontFileName": context.get("textStyleBigFontFileName"),
            "ownerBlockName": context.get("ownerBlockName"),
            "candidateText": display_candidate_text(record, candidate),
            "rawCandidateText": candidate.get("text"),
            "candidateSource": candidate_source,
            "candidateReason": candidate.get("reason"),
            "isRoundTrip": bool(candidate.get("isRoundTrip")),
            "problemReason": (record.get("problemGate") or {}).get("reason"),
            "highRisk": bool((record.get("risk") or {}).get("highRisk")),
            "candidateConflict": bool((record.get("risk") or {}).get("candidateConflict")),
            "hasNonRoundTrip": bool((record.get("risk") or {}).get("hasNonRoundTrip")),
            "currentUnsafe": bool((record.get("risk") or {}).get("currentUnsafe")),
            "candidateUnsafe": bool((record.get("risk") or {}).get("candidateUnsafe")),
            "isFromExternalReference": bool(context.get("isFromExternalReference")),
            "reviewed": bool(reviewed),
            "labelAction": reviewed.get("labelAction") if reviewed else None,
            "skipReasons": self._auto_skip_reasons(record, summary and self.record_index.get(str((summary.get("recordIds") or [""])[0])) or None),
            "riskReasons": self._cluster_risk_reasons(record, summary and self.record_index.get(str((summary.get("recordIds") or [""])[0])) or None),
        }

    def _training_dataset_digest(self, record: dict, feature_rows: int) -> dict:
        context = record.get("context") or {}
        selected = self._selected_candidate(record)
        drawing = self.manifest.get("drawing") or {}
        display_text = display_text_for_record(record)
        display_label = display_text if record.get("labelAction") == "keep" else record.get("labelText")
        candidate_source = candidate_encoding_path(record, selected)
        return {
            "groupId": record.get("groupId"),
            "currentText": display_text,
            "rawCurrentText": raw_current_text(record),
            "displayText": display_text,
            "labelText": display_label,
            "rawLabelText": record.get("labelText"),
            "labelAction": record.get("labelAction"),
            "reviewer": record.get("reviewer"),
            "reviewedUtc": record.get("reviewedUtc"),
            "enteredTrainingUtc": record.get("enteredTrainingUtc"),
            "trainingSource": record.get("trainingSource"),
            "trainingFeatureBuildId": record.get("trainingFeatureBuildId"),
            "packageId": record.get("trainingPackageId") or self.package_id,
            "exportId": record.get("trainingExportId") or self.export_id,
            "drawingFileName": drawing.get("fileName") or drawing.get("name") or "",
            "drawingPath": drawing.get("path") or drawing.get("fullName") or "",
            "handle": context.get("handle"),
            "layer": context.get("layer"),
            "textStyleName": context.get("textStyleName"),
            "font": context.get("textStyleFileName"),
            "bigFont": context.get("textStyleBigFontFileName"),
            "ownerBlockName": context.get("ownerBlockName"),
            "isFromExternalReference": bool(context.get("isFromExternalReference")),
            "candidateText": display_candidate_text(record, selected),
            "rawCandidateText": selected.get("text"),
            "candidateSource": candidate_source,
            "candidateReason": selected.get("reason"),
            "selectedCandidateIndex": record.get("selectedCandidateIndex"),
            "candidateCount": len(record.get("candidates") or []),
            "featureRows": feature_rows,
            "origin": record.get("origin"),
            "originDetail": record.get("originDetail"),
        }

    def _coerce_imported_training_record(self, record: dict, entered_utc: str, build_id: str) -> dict:
        group_id = str(record.get("groupId") or "").strip()
        if not group_id:
            raise ValueError("missing groupId")
        if group_id not in self.record_index:
            raise ValueError(f"groupId 不属于当前数据包：{group_id}")

        if record.get("trainingDatasetSchema") == TRAINING_DATASET_SCHEMA and isinstance(record.get("candidates"), list):
            imported = deepcopy(record)
            imported["trainingDatasetSchema"] = TRAINING_DATASET_SCHEMA
            imported.setdefault("trainingFeatureBuildId", build_id)
            imported.setdefault("trainingSource", "jsonl-import")
            imported.setdefault("trainingPackageId", self.package_id)
            imported.setdefault("trainingExportId", self.export_id)
            imported.setdefault("enteredTrainingUtc", entered_utc)
            return imported

        action = str(record.get("labelAction") or "").strip()
        if action not in LABEL_ACTIONS:
            raise ValueError("unsupported labelAction")
        selected_index = record.get("selectedCandidateIndex")
        if selected_index is None:
            selected_index = record.get("candidateIndex")
        reviewed = self._build_reviewed_record(
            self.record_index[group_id],
            action,
            selected_index,
            str(record.get("labelText") or ""),
            str(record.get("reviewer") or "").strip() or "jsonl-import",
            str(record.get("originDetail") or record.get("note") or "training-dataset-import"),
        )
        return self._build_training_dataset_entry(reviewed, entered_utc, build_id, "jsonl-import")

    @staticmethod
    def _record_matches_reset_ids(group_id: str, record: dict, requested_ids: set[str]) -> bool:
        if group_id in requested_ids:
            return True
        return any(
            str(record.get(key) or "") in requested_ids
            for key in ("propagationGroupId", "propagationClusterId", "batchId")
        )

    @staticmethod
    def _selected_candidate(record: dict) -> dict:
        candidates = record.get("candidates") or []
        selected_index = parse_candidate_index(record.get("selectedCandidateIndex"))
        if selected_index is not None and 0 <= selected_index < len(candidates):
            return candidates[selected_index]
        for candidate in candidates:
            try:
                if float(candidate.get("targetScore") or 0.0) >= 1.0:
                    return candidate
            except (TypeError, ValueError):
                continue
        return candidates[0] if candidates else {}

    def _feature_row_counts(self) -> dict[str, int]:
        counts: dict[str, int] = {}
        if not self.features_path.exists():
            return counts
        with self.features_path.open("r", encoding="utf-8-sig", newline="") as reader:
            for row in csv.DictReader(reader):
                group_id = str(row.get("group_id") or "")
                if group_id:
                    counts[group_id] = counts.get(group_id, 0) + 1
        return counts

    def _ordered_training_records(self, records: object) -> list[dict]:
        order = {str(record.get("groupId")): index for index, record in enumerate(self.records)}
        return sorted(
            list(records),
            key=lambda item: order.get(str(item.get("groupId")), 10**9),
        )

    @staticmethod
    def _risk_totals(records: list[dict]) -> dict:
        return {
            "problem": sum(1 for record in records if (record.get("problemGate") or {}).get("hasProblem")),
            "highRisk": sum(1 for record in records if (record.get("risk") or {}).get("highRisk")),
            "candidateConflict": sum(1 for record in records if (record.get("risk") or {}).get("candidateConflict")),
            "hasNonRoundTrip": sum(1 for record in records if (record.get("risk") or {}).get("hasNonRoundTrip")),
            "currentUnsafe": sum(1 for record in records if (record.get("risk") or {}).get("currentUnsafe")),
            "candidateUnsafe": sum(1 for record in records if (record.get("risk") or {}).get("candidateUnsafe")),
            "xref": sum(1 for record in records if (record.get("context") or {}).get("isFromExternalReference")),
        }

    def _find_review_group(self, review_group_id: str) -> list[dict] | None:
        for group in self.review_groups_payload().get("groups", []):
            if str(group.get("id") or "") == review_group_id:
                record_ids = set(group.get("recordIds") or [])
                return [record for record in self.records if str(record.get("groupId") or "") in record_ids]
        return None

    def _partition_group_records(self, records: list[dict], summary: dict | None = None) -> dict:
        del summary
        auto_records: list[dict] = []
        review_records: list[dict] = []
        reviewed_records: list[dict] = []
        trained_records: list[dict] = []
        skip_counts: dict[str, int] = {}
        for record in records:
            record_id = str(record.get("groupId") or "")
            if record_id in self.training_dataset:
                trained_records.append(record)
                skip_counts["already-trained"] = skip_counts.get("already-trained", 0) + 1
                continue
            if record_id in self.reviewed:
                reviewed_records.append(record)
                skip_counts["already-reviewed"] = skip_counts.get("already-reviewed", 0) + 1
                continue
            reasons = self._auto_skip_reasons(record, records[0])
            if any(reason != "already-reviewed" for reason in reasons):
                review_records.append(record)
                for reason in reasons:
                    skip_counts[reason] = skip_counts.get(reason, 0) + 1
            else:
                auto_records.append(record)
        return {
            "auto": auto_records,
            "review": review_records,
            "reviewed": reviewed_records,
            "trained": trained_records,
            "skipCounts": skip_counts,
        }

    def _auto_skip_reasons(self, record: dict, base_record: dict | None = None) -> list[str]:
        if not base_record:
            base_record = record
        reasons: list[str] = []
        if str(record.get("groupId") or "") in self.training_dataset:
            reasons.append("already-trained")
        if str(record.get("groupId") or "") in self.reviewed:
            reasons.append("already-reviewed")
        risk = record.get("risk") or {}
        context = record.get("context") or {}
        candidate = self._recommended_candidate(record)
        base_candidate = self._recommended_candidate(base_record)
        if str(candidate.get("source") or "") != str(base_candidate.get("source") or ""):
            reasons.append("candidate-source-mismatch")
        if str(candidate.get("text") or "") != str(base_candidate.get("text") or ""):
            reasons.append("candidate-text-mismatch")
        return sorted(set(reasons))

    def _cluster_risk_reasons(self, record: dict, base_record: dict | None = None) -> list[str]:
        if not base_record:
            base_record = record
        reasons: list[str] = []
        risk = record.get("risk") or {}
        context = record.get("context") or {}
        candidate = self._recommended_candidate(record)
        if risk.get("highRisk"):
            reasons.append("high-risk")
        if risk.get("candidateConflict"):
            reasons.append("candidate-conflict")
        if risk.get("hasNonRoundTrip"):
            reasons.append("non-roundtrip-risk")
        if not bool(candidate.get("isRoundTrip")):
            reasons.append("non-roundtrip-candidate")
        if risk.get("currentUnsafe") or risk.get("candidateUnsafe"):
            reasons.append("unsafe-text")
        if context.get("isFromExternalReference"):
            reasons.append("xref")
        if self._context_safety_signature(record) != self._context_safety_signature(base_record):
            reasons.append("context-mismatch")
        return sorted(set(reasons))

    def _risk_records(self, records: list[dict]) -> list[dict]:
        base_record = records[0] if records else None
        return [record for record in records if self._cluster_risk_reasons(record, base_record)]

    @staticmethod
    def _context_safety_signature(record: dict) -> tuple[str, str, str, str, str]:
        context = record.get("context") or {}
        return (
            str(context.get("layer") or ""),
            str(context.get("textStyleName") or ""),
            str(context.get("textStyleFileName") or ""),
            str(context.get("textStyleBigFontFileName") or ""),
            str(context.get("ownerBlockName") or ""),
        )

    @staticmethod
    def _risk_pattern(record: dict) -> str:
        risk = record.get("risk") or {}
        names = [
            name
            for name in ["highRisk", "candidateConflict", "hasNonRoundTrip", "currentUnsafe", "candidateUnsafe"]
            if risk.get(name)
        ]
        return "+".join(names) if names else "safe"

    @staticmethod
    def _is_noop_candidate(candidate: dict) -> bool:
        return bool(candidate.get("isNoOp")) or "current-noop" in str(candidate.get("source") or "").lower()

    def _recommended_action(self, candidate: dict) -> str:
        return "keep" if self._is_noop_candidate(candidate) else "repair"

    def _should_prefer_noop_candidate(self, record: dict) -> bool:
        candidates = record.get("candidates") or []
        noop_index = self._find_noop_candidate(candidates)
        if noop_index is None:
            return False

        current_text = str(record.get("currentText") or (record.get("context") or {}).get("currentText") or "").strip()
        if not current_text or self._has_unsafe_text(current_text):
            return False
        if (record.get("risk") or {}).get("currentUnsafe"):
            return False

        if self._looks_like_safe_number_or_code(current_text):
            return True
        if self._has_native_decode_evidence(record):
            return False
        if self._has_strong_repair_candidate(record):
            return False
        return self._looks_like_plain_text(current_text)

    @staticmethod
    def _has_unsafe_text(text: str) -> bool:
        for char in text:
            category = unicodedata.category(char)
            if char == "\uFFFD" or category in {"Cc", "Cs", "Cn"}:
                return True
        return False

    @staticmethod
    def _contains_cjk(text: str) -> bool:
        return any(0x3400 <= ord(char) <= 0x4DBF or 0x4E00 <= ord(char) <= 0x9FFF or 0xF900 <= ord(char) <= 0xFAFF for char in text)

    @staticmethod
    def _contains_mojibake_marker(text: str) -> bool:
        return any(char in MOJIBAKE_MARKER_CHARS for char in text)

    @staticmethod
    def _has_native_decode_evidence(record: dict) -> bool:
        context = record.get("context") or {}
        evidence = context.get("nativeDecodeEvidence") or {}
        return (
            bool((record.get("problemGate") or {}).get("hasProblem"))
            or bool(context.get("hasNativeDecodeEvidence"))
            or bool(evidence.get("hasEvidence"))
        )

    @staticmethod
    def _looks_like_safe_number_or_code(text: str) -> bool:
        if not text or DatasetStore._contains_cjk(text):
            return False
        allowed_punctuation = set(" -_.,:;#/\\()+*xX[]=<>%°'\"")
        has_alnum = False
        for char in text:
            if char.isalnum():
                has_alnum = True
                continue
            if char.isspace() or char in allowed_punctuation:
                continue
            return False
        return has_alnum

    @staticmethod
    def _looks_like_plain_text(text: str) -> bool:
        if not text:
            return False
        allowed_punctuation = set(" -_.,:;#/\\()+*xX[]=<>%°'\"，。；：、（）【】《》")
        has_cjk = False
        has_alnum = False
        for char in text:
            code = ord(char)
            if 0x3400 <= code <= 0x4DBF or 0x4E00 <= code <= 0x9FFF or 0xF900 <= code <= 0xFAFF:
                has_cjk = True
                continue
            if char.isalnum():
                has_alnum = True
                continue
            if char.isspace() or char in allowed_punctuation:
                continue
            return False
        return has_cjk or has_alnum

    def _has_strong_repair_candidate(self, record: dict) -> bool:
        if not self._has_native_decode_evidence(record):
            return False

        current_text = str(record.get("currentText") or "")
        candidate = self._recommended_candidate(record)
        if not candidate or self._is_noop_candidate(candidate):
            return False
        candidate_text = str(candidate.get("text") or "")
        if not candidate_text or candidate_text == current_text:
            return False
        if self._has_unsafe_text(candidate_text) or self._contains_mojibake_marker(candidate_text):
            return False
        if not bool(candidate.get("isRoundTrip")):
            return False

        current_keyword_count = self._semantic_keyword_count(current_text)
        candidate_keyword_count = self._semantic_keyword_count(candidate_text)
        if candidate_keyword_count >= current_keyword_count + 2 and self._contains_cjk(candidate_text):
            return True
        if self._contains_mojibake_marker(current_text) and not self._contains_mojibake_marker(candidate_text):
            return True
        return False

    @staticmethod
    def _semantic_keyword_count(text: str) -> int:
        return sum(1 for char in text if char in CAD_SEMANTIC_CHARS)

    def _representative_records(self, records: list[dict], summary: dict | None, partitions: dict | None = None) -> list[dict]:
        del summary
        if partitions is None:
            partitions = self._partition_group_records(records)
        selected: list[dict] = []
        seen: set[str] = set()

        def add(record: dict | None) -> None:
            if not record:
                return
            record_id = str(record.get("groupId") or "")
            if record_id and record_id not in seen and len(selected) < 8:
                selected.append(record)
                seen.add(record_id)

        add((partitions.get("auto") or records)[0] if (partitions.get("auto") or records) else None)
        base_layer = str((records[0].get("context") or {}).get("layer") or "")
        for record in records:
            if str((record.get("context") or {}).get("layer") or "") != base_layer:
                add(record)
                break
        for record in records:
            if (record.get("risk") or {}).get("highRisk"):
                add(record)
                break
        for record in records:
            if (record.get("risk") or {}).get("candidateConflict"):
                add(record)
                break
        for record in partitions.get("review", []):
            add(record)
        for record in records:
            add(record)
            if len(selected) >= 8:
                break
        return [self._record_digest(record) for record in selected]

    @staticmethod
    def _source_text_variants(records: list[dict]) -> list[dict]:
        counts: dict[str, int] = {}
        for record in records:
            text = display_text_for_record(record)
            counts[text] = counts.get(text, 0) + 1
        return [
            {"text": text, "count": count}
            for text, count in sorted(counts.items(), key=lambda item: (-item[1], item[0]))[:8]
        ]

    def _context_summary(self, records: list[dict]) -> dict:
        signatures: dict[tuple[str, str, str, str, str], int] = {}
        layers: dict[str, int] = {}
        styles: dict[str, int] = {}
        fonts: dict[str, int] = {}
        bigfonts: dict[str, int] = {}
        blocks: dict[str, int] = {}
        xref_count = 0
        for record in records:
            context = record.get("context") or {}
            signature = self._context_safety_signature(record)
            signatures[signature] = signatures.get(signature, 0) + 1
            layer, style, font, bigfont, block = signature
            layers[layer] = layers.get(layer, 0) + 1
            styles[style] = styles.get(style, 0) + 1
            fonts[font] = fonts.get(font, 0) + 1
            bigfonts[bigfont] = bigfonts.get(bigfont, 0) + 1
            blocks[block] = blocks.get(block, 0) + 1
            if context.get("isFromExternalReference"):
                xref_count += 1

        def top_values(values: dict[str, int]) -> list[dict]:
            return [
                {"value": value, "count": count}
                for value, count in sorted(values.items(), key=lambda item: (-item[1], item[0]))[:8]
            ]

        return {
            "uniqueContexts": len(signatures),
            "layers": top_values(layers),
            "textStyles": top_values(styles),
            "fonts": top_values(fonts),
            "bigFonts": top_values(bigfonts),
            "ownerBlocks": top_values(blocks),
            "xrefCount": xref_count,
        }

    def _handle_for_group_id(self, group_id: str) -> str | None:
        record = self.record_index.get(str(group_id or ""))
        if not record:
            return None
        return (record.get("context") or {}).get("handle")

    def _propagation_rule_text(self, record: dict) -> str:
        candidate = self._recommended_candidate(record)
        return (
            f"{REVIEW_GROUP_RULE_VERSION}: currentText+candidateText+candidateSource+action; "
            f"current={record.get('currentText') or ''}; "
            f"candidate={candidate.get('text') or ''}; "
            f"source={candidate_encoding_path(record, candidate)}; "
            "risk-and-context-audited"
        )

    def _review_groups_cache_signature(self) -> tuple[object, ...]:
        reviewed_mtime = self.reviewed_path.stat().st_mtime if self.reviewed_path.exists() else 0
        training_mtime = self.training_dataset_path.stat().st_mtime if self.training_dataset_path.exists() else 0
        return (
            REVIEW_GROUP_RULE_VERSION,
            self.package_id,
            self.candidates_path.stat().st_mtime,
            reviewed_mtime,
            training_mtime,
            len(self.reviewed),
            len(self.training_dataset),
        )

    def _invalidate_review_groups(self) -> None:
        self._review_groups_cache_key = None
        self._review_groups_cache = None

    @staticmethod
    def _find_candidate_by_text(candidates: list[dict], text: str) -> int | None:
        for index, candidate in enumerate(candidates):
            if str(candidate.get("text") or "") == text:
                return index
        return None

    @staticmethod
    def _find_noop_candidate(candidates: list[dict]) -> int | None:
        for index, candidate in enumerate(candidates):
            source = str(candidate.get("source") or "")
            if bool(candidate.get("isNoOp")) or "current-noop" in source.lower():
                return index
        return 0 if candidates else None

    def _rewrite_training_dataset_file(self) -> None:
        training_records = self._ordered_training_records(self.training_dataset.values())
        if not training_records:
            if self.training_dataset_path.exists():
                self.training_dataset_path.unlink()
            return

        tmp_path = self.work_dir / f"{self.training_dataset_path.name}.{uuid.uuid4().hex}.tmp"
        try:
            with tmp_path.open("w", encoding="utf-8", newline="\n") as writer:
                for record in training_records:
                    writer.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
                    writer.write("\n")
            tmp_path.replace(self.training_dataset_path)
        finally:
            if tmp_path.exists():
                tmp_path.unlink()

    def _rewrite_features_without_group_ids(self, removed_ids: set[str]) -> None:
        if not self.features_path.exists():
            return
        with self.features_path.open("r", encoding="utf-8-sig", newline="") as reader:
            csv_reader = csv.DictReader(reader)
            fieldnames = list(csv_reader.fieldnames or [])
            rows = [row for row in csv_reader if str(row.get("group_id") or "") not in removed_ids]

        if not rows:
            self.features_path.unlink()
            return

        tmp_path = self.work_dir / f"{self.features_path.name}.{uuid.uuid4().hex}.tmp"
        try:
            with tmp_path.open("w", encoding="utf-8-sig", newline="") as writer:
                csv_writer = csv.DictWriter(writer, fieldnames=fieldnames)
                csv_writer.writeheader()
                csv_writer.writerows(rows)
            tmp_path.replace(self.features_path)
        finally:
            if tmp_path.exists():
                tmp_path.unlink()

    def _rewrite_reviewed_files(self) -> None:
        order = {str(record.get("groupId")): index for index, record in enumerate(self.records)}
        reviewed_records = sorted(
            self.reviewed.values(),
            key=lambda item: order.get(str(item.get("groupId")), 10**9),
        )
        last_error: PermissionError | None = None
        for attempt in range(6):
            tmp_path = self.work_dir / f"{self.reviewed_path.name}.{uuid.uuid4().hex}.tmp"
            try:
                with tmp_path.open("w", encoding="utf-8", newline="\n") as writer:
                    for record in reviewed_records:
                        writer.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
                        writer.write("\n")
                tmp_path.replace(self.reviewed_path)
                break
            except PermissionError as exc:
                last_error = exc
                time.sleep(0.05 * (attempt + 1))
            finally:
                if tmp_path.exists():
                    tmp_path.unlink()
        else:
            if last_error is not None:
                raise last_error
        self._write_audit(reviewed_records)

    def _write_audit(self, records: list[dict]) -> None:
        with self.audit_path.open("w", encoding="utf-8-sig", newline="\n") as writer:
            writer.write(
                "group_id\thandle\tlabel_action\tlabel_text\tselected_candidate\treviewer\treviewed_utc\t"
                "batch_id\tbatch_rule\tbatch_confidence\tsample_reviewed_count\tapplied_by_group\t"
                "propagation_group_id\tsource_representative_handle\tsource_representative_group_id\t"
                "applied_by_cluster\tpropagation_cluster_id\tpropagation_signature\tcluster_risk_summary\t"
                "cluster_context_summary\tcluster_sample_reviewed_count\t"
                "applied_count\tskipped_conflict_count\tskipped_risk_count\tskipped_roundtrip_count\t"
                "skipped_context_count\tskipped_manual_count\tpropagation_scope\tpropagation_rule\t"
                "human_review_mode\tnote\n"
            )
            for record in records:
                context = record.get("context") or {}
                sample_ids = record.get("batchReviewedSampleIds") or []
                cluster_sample_ids = record.get("clusterReviewedSampleIds") or []
                writer.write(
                    "\t".join(
                        [
                            tsv(record.get("groupId")),
                            tsv(context.get("handle")),
                            tsv(record.get("labelAction")),
                            tsv(record.get("labelText")),
                            tsv(record.get("selectedCandidateIndex")),
                            tsv(record.get("reviewer")),
                            tsv(record.get("reviewedUtc")),
                            tsv(record.get("batchId")),
                            tsv(record.get("batchRule")),
                            tsv(record.get("batchConfidenceLevel")),
                            tsv(len(sample_ids) if isinstance(sample_ids, list) else 0),
                            tsv(record.get("appliedByGroup")),
                            tsv(record.get("propagationGroupId")),
                            tsv(record.get("sourceRepresentativeHandle")),
                            tsv(record.get("sourceRepresentativeGroupId")),
                            tsv(record.get("appliedByCluster")),
                            tsv(record.get("propagationClusterId")),
                            tsv(record.get("propagationSignature")),
                            tsv(json.dumps(record.get("clusterRiskSummary") or {}, ensure_ascii=False, separators=(",", ":"))),
                            tsv(json.dumps(record.get("clusterContextSummary") or {}, ensure_ascii=False, separators=(",", ":"))),
                            tsv(len(cluster_sample_ids) if isinstance(cluster_sample_ids, list) else 0),
                            tsv(record.get("appliedCount")),
                            tsv(record.get("skippedConflictCount")),
                            tsv(record.get("skippedRiskCount")),
                            tsv(record.get("skippedRoundtripCount")),
                            tsv(record.get("skippedContextCount")),
                            tsv(record.get("skippedManualCount")),
                            tsv(record.get("propagationScope")),
                            tsv(record.get("propagationRule")),
                            tsv(record.get("humanReviewMode")),
                            tsv(record.get("originDetail")),
                        ]
                    )
                )
                writer.write("\n")


class ProcessJob:
    def __init__(self, command: list[str], cwd: Path, log_path: Path) -> None:
        self.id = uuid.uuid4().hex
        self.command = command
        self.cwd = cwd
        self.log_path = log_path
        self.status = "idle"
        self.started_utc = ""
        self.ended_utc = ""
        self.return_code: int | None = None
        self.lines: list[str] = []
        self.lock = threading.Lock()
        self.thread: threading.Thread | None = None
        self.process: subprocess.Popen[str] | None = None
        self.cancel_requested = False

    def start(self) -> None:
        if self.status == "running":
            raise RuntimeError("job already running")
        self.status = "running"
        self.started_utc = now_utc()
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self) -> None:
        self.log_path.parent.mkdir(parents=True, exist_ok=True)
        with self.log_path.open("w", encoding="utf-8", newline="\n") as log:
            log.write(f"$ {' '.join(self.command)}\n\n")
            try:
                process = subprocess.Popen(
                    self.command,
                    cwd=str(self.cwd),
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    env=utf8_subprocess_env(),
                )
                self.process = process
                assert process.stdout is not None
                for line in process.stdout:
                    self._append_line(line.rstrip("\n"), log)
                self.return_code = process.wait()
                self.status = "canceled" if self.cancel_requested else ("succeeded" if self.return_code == 0 else "failed")
            except Exception as exc:  # pragma: no cover - displayed by UI
                self.return_code = -1
                self.status = "canceled" if self.cancel_requested else "failed"
                self._append_line(f"{type(exc).__name__}: {exc}", log)
            finally:
                if self.process and self.process.stdout:
                    self.process.stdout.close()
                self.process = None
                self.ended_utc = now_utc()

    def _append_line(self, line: str, log) -> None:
        with self.lock:
            self.lines.append(line)
            if len(self.lines) > 800:
                self.lines = self.lines[-800:]
        log.write(line + "\n")
        log.flush()

    def payload(self) -> dict:
        with self.lock:
            lines = list(self.lines)
        return {
            "id": self.id,
            "status": self.status,
            "startedUtc": self.started_utc,
            "endedUtc": self.ended_utc,
            "returnCode": self.return_code,
            "logPath": str(self.log_path),
            "lines": lines,
            "command": self.command,
        }

    def cancel(self) -> bool:
        if self.status != "running":
            return False
        self.cancel_requested = True
        process = self.process
        if process and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=3)
        self.status = "canceled"
        self.ended_utc = now_utc()
        return True


class WorkbenchState:
    def __init__(self, tool_root: Path, dataset_root: Path, model_root: Path, python: str, package: str | None) -> None:
        self.tool_root = tool_root.resolve()
        self.dataset_root = dataset_root.resolve()
        self.model_root = model_root.resolve()
        self.python = python
        self.extract_root = self.dataset_root / "ExtractedCandidates"
        self.review_root = self.dataset_root / "ReviewedLabels"
        self.training_sets_root = self.dataset_root / "TrainingSets"
        self.reports_root = self.dataset_root / "Reports"
        for path in [self.extract_root, self.review_root, self.training_sets_root, self.model_root, self.reports_root]:
            path.mkdir(parents=True, exist_ok=True)

        self.store: DatasetStore | None = None
        self.training_job: ProcessJob | None = None
        self.simulation_job: ProcessJob | None = None
        self.training_context: dict = {}
        if package:
            self.select_package(package)
        else:
            latest = self.latest_package_id()
            if latest:
                self.select_package(latest)

    def latest_package_id(self) -> str | None:
        packages = self.list_packages()
        return str(packages[0]["id"]) if packages else None

    def list_packages(self) -> list[dict]:
        items: list[dict] = []
        for package_dir in self.extract_root.iterdir() if self.extract_root.exists() else []:
            if not package_dir.is_dir():
                continue
            manifest_path = package_dir / "manifest.json"
            candidates_path = package_dir / "candidate_groups.jsonl"
            if not manifest_path.exists() or not candidates_path.exists():
                continue
            try:
                manifest = read_json(manifest_path)
            except Exception:
                manifest = {}
            counts = manifest.get("counts") or {}
            reviewed_path = self.review_root / f"{manifest.get('exportId') or package_dir.name}_reviewed.jsonl"
            export_id = str(manifest.get("exportId") or package_dir.name)
            training_dataset_path = self.training_sets_root / f"{export_id}_training_dataset.jsonl"
            features_path = self.training_sets_root / f"{export_id}_features.csv"
            reviewed_count = count_jsonl(reviewed_path)
            training_count = count_jsonl(training_dataset_path)
            if training_count == 0 and features_path.exists():
                training_count = int(summarize_features(features_path).get("groups") or 0)
            if self.store and self.store.package_id == package_dir.name:
                reviewed_count = len(self.store.reviewed)
                training_count = len(self.store.training_dataset)
            items.append(
                {
                    "id": package_dir.name,
                    "path": str(package_dir.resolve()),
                    "active": bool(self.store and self.store.package_id == package_dir.name),
                    "modifiedUtc": datetime.fromtimestamp(package_dir.stat().st_mtime, timezone.utc).isoformat(),
                    "manifest": manifest,
                    "drawing": manifest.get("drawing") or {},
                    "counts": counts,
                    "reviewed": reviewed_count,
                    "trainingDataset": training_count,
                }
            )
        items.sort(key=lambda item: str(item.get("modifiedUtc") or ""), reverse=True)
        return items

    def select_package(self, package: str) -> dict:
        package_path = self.resolve_package(package)
        self.store = DatasetStore(self.dataset_root, self.model_root, package_path)
        return self.bootstrap_payload()

    def delete_package(self, payload: dict) -> dict:
        package_id = str(payload.get("packageId") or payload.get("package") or "").strip()
        if not package_id:
            raise ValueError("packageId is required")
        package_path = self.resolve_package(package_id)
        if not package_path.exists():
            raise FileNotFoundError(f"找不到数据包：{package_id}")

        try:
            manifest = read_json(package_path / "manifest.json")
        except Exception:
            manifest = {}
        export_id = str(manifest.get("exportId") or package_path.name)

        deleted: list[dict] = []

        def delete_path(path: Path, label: str) -> None:
            if not path.exists():
                return
            if path.is_dir():
                shutil.rmtree(path)
            else:
                path.unlink()
            deleted.append({"label": label, "path": str(path)})

        delete_path(package_path, "package")
        delete_path(self.review_root / f"{export_id}_reviewed.jsonl", "reviewed")
        delete_path(self.review_root / f"{export_id}_review_audit.tsv", "audit")
        delete_path(self.training_sets_root / f"{export_id}_training_dataset.jsonl", "trainingDataset")
        delete_path(self.training_sets_root / f"{export_id}_features.csv", "features")

        if self.store and self.store.package_id == package_path.name:
            self.store = None
            latest = self.latest_package_id()
            if latest:
                self.select_package(latest)

        return {
            "ok": True,
            "deletedPackageId": package_path.name,
            "deletedPaths": deleted,
            "bootstrap": self.bootstrap_payload(),
        }

    def resolve_package(self, package: str) -> Path:
        package = str(package or "").strip()
        if not package:
            raise ValueError("package is required")
        path = Path(package)
        if not path.is_absolute():
            path = self.extract_root / package
        return ensure_under(path, self.extract_root)

    def package_store(self, package: str) -> DatasetStore:
        package_path = self.resolve_package(package)
        if self.store and self.store.package_dir == package_path:
            return self.store
        return DatasetStore(self.dataset_root, self.model_root, package_path)

    def package_stores(self, package_ids: object) -> list[DatasetStore]:
        if not isinstance(package_ids, list):
            raise ValueError("packageIds must be an array")
        ordered_ids: list[str] = []
        seen: set[str] = set()
        for value in package_ids:
            package_id = str(value or "").strip()
            if not package_id or package_id in seen:
                continue
            seen.add(package_id)
            ordered_ids.append(package_id)
        if not ordered_ids:
            raise ValueError("请选择至少一个训练数据包。")
        return [self.package_store(package_id) for package_id in ordered_ids]

    def bootstrap_payload(self) -> dict:
        data = self.store.data_payload() if self.store else empty_data_payload()
        return {
            "toolRoot": str(self.tool_root),
            "datasetRoot": str(self.dataset_root),
            "modelRoot": str(self.model_root),
            "python": self.python,
            "packages": self.list_packages(),
            "data": data,
            "features": self.features_payload(),
            "training": self.training_payload(),
            "report": self.report_payload(),
        }

    def save_label(self, payload: dict) -> dict:
        store = self.require_store()
        result = store.save_label(payload)
        return {"ok": True, "label": result}

    def review_groups_payload(self) -> dict:
        store = self.require_store()
        return store.review_groups_payload()

    def review_clusters_payload(self) -> dict:
        store = self.require_store()
        return store.review_clusters_payload()

    def review_group_records_payload(self, review_group_id: str, bucket: str) -> dict:
        store = self.require_store()
        return store.review_group_records_payload(review_group_id, bucket)

    def review_cluster_records_payload(self, review_cluster_id: str, bucket: str) -> dict:
        store = self.require_store()
        return store.review_cluster_records_payload(review_cluster_id, bucket)

    def confirm_group(self, payload: dict) -> dict:
        store = self.require_store()
        return store.confirm_group(payload)

    def confirm_cluster(self, payload: dict) -> dict:
        store = self.require_store()
        return store.confirm_cluster(payload)

    def confirm_review_table_rows(self, payload: dict) -> dict:
        store = self.require_store()
        return store.confirm_review_table_rows(payload)

    def reset_review_rows(self, payload: dict) -> dict:
        store = self.require_store()
        result = store.reset_review_rows(payload)
        return {
            **result,
            "data": store.data_payload(),
            "features": self.features_payload(),
            "reviewClusters": store.review_clusters_payload(),
        }

    def save_batch_label(self, payload: dict) -> dict:
        store = self.require_store()
        return store.save_batch_label(payload)

    def save_batch_label_groups(self, payload: dict) -> dict:
        store = self.require_store()
        return store.save_batch_label_groups(payload)

    def rollback_batch(self, payload: dict) -> dict:
        store = self.require_store()
        return store.rollback_batch(payload)

    def training_dataset_payload(self) -> dict:
        store = self.require_store()
        return store.training_dataset_payload()

    def export_training_dataset(self, export_format: str) -> dict:
        store = self.require_store()
        return store.export_training_dataset(export_format)

    def import_training_dataset(self, payload: dict) -> dict:
        store = self.require_store()
        result = store.import_training_dataset(payload)
        return {
            **result,
            "data": store.data_payload(),
            "features": self.features_payload(),
            "reviewClusters": store.review_clusters_payload(),
        }

    def delete_training_dataset_records(self, payload: dict) -> dict:
        store = self.require_store()
        result = store.delete_training_dataset_records(payload)
        return {
            **result,
            "data": store.data_payload(),
            "features": self.features_payload(),
            "reviewClusters": store.review_clusters_payload(),
        }

    def _run_build_features(self, input_path: Path, output_path: Path) -> subprocess.CompletedProcess[str]:
        script = self.tool_root / "training" / "build_features.py"
        result = subprocess.run(
            [
                self.python,
                str(script),
                "--input",
                str(input_path),
                "--output",
                str(output_path),
            ],
            cwd=str(self.tool_root),
            text=True,
            encoding="utf-8",
            errors="replace",
            env=utf8_subprocess_env(),
            capture_output=True,
        )
        if result.returncode != 0:
            raise RuntimeError((result.stdout + "\n" + result.stderr).strip())
        return result

    def build_features(self) -> dict:
        store = self.require_store()
        training_records, promoted_ids, build_id = store.prepare_training_dataset_build()
        if not training_records:
            raise ValueError("请先完成至少一条人工审核标注，再生成 Feature。")

        store.features_path.parent.mkdir(parents=True, exist_ok=True)
        training_tmp_path = store.work_dir / f"{store.training_dataset_path.name}.{build_id}.build.tmp"
        features_tmp_path = store.work_dir / f"{store.features_path.name}.{build_id}.tmp"
        with training_tmp_path.open("w", encoding="utf-8", newline="\n") as writer:
            for record in training_records:
                writer.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
                writer.write("\n")

        try:
            result = self._run_build_features(training_tmp_path, features_tmp_path)
            store.commit_training_dataset_build(training_records, promoted_ids, features_tmp_path)
            store.features_path.touch()
            return {
                "ok": True,
                "message": result.stdout.strip(),
                "promoted": len(promoted_ids),
                "trainingRecords": len(training_records),
                "features": self.features_payload(),
                "data": store.data_payload(),
                "reviewClusters": store.review_clusters_payload(),
            }
        finally:
            if training_tmp_path.exists():
                training_tmp_path.unlink()
            if features_tmp_path.exists():
                features_tmp_path.unlink()

    def build_selected_training_features(self, stores: list[DatasetStore]) -> dict:
        build_id = uuid.uuid4().hex
        combined_dir = self.training_sets_root / "Combined"
        combined_dir.mkdir(parents=True, exist_ok=True)
        work_dir = self.dataset_root / ".work"
        work_dir.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        feature_path = combined_dir / f"{stamp}_{len(stores)}packages_features.csv"
        training_tmp_path = work_dir / f"combined_training_dataset.{build_id}.jsonl"
        features_tmp_path = work_dir / f"{feature_path.name}.{build_id}.tmp"

        prepared: list[tuple[DatasetStore, list[dict], list[str]]] = []
        empty_packages: list[str] = []
        training_records: list[dict] = []
        feature_records: list[dict] = []
        for store in stores:
            records, promoted_ids, _ = store.prepare_training_dataset_build()
            if not records:
                empty_packages.append(store.package_id)
                continue
            prepared.append((store, records, promoted_ids))
            training_records.extend(records)
            feature_records.extend(self._records_for_selected_feature_build(store, records, len(stores)))

        if empty_packages:
            raise ValueError("所选数据包没有训练数据：" + ", ".join(empty_packages))
        if not training_records:
            raise ValueError("所选数据包没有可训练记录。")

        try:
            with training_tmp_path.open("w", encoding="utf-8", newline="\n") as writer:
                for record in feature_records:
                    writer.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
                    writer.write("\n")

            result = self._run_build_features(training_tmp_path, features_tmp_path)
            for store, records, promoted_ids in prepared:
                store.commit_training_dataset_records(records, promoted_ids)
            features_tmp_path.replace(feature_path)
            feature_path.touch()
            summary = summarize_features(feature_path)
            return {
                "ok": True,
                "message": result.stdout.strip(),
                "featurePath": str(feature_path),
                "packageIds": [store.package_id for store in stores],
                "packageCount": len(stores),
                "trainingRecords": len(training_records),
                "features": {
                    "exists": True,
                    "path": str(feature_path),
                    "trainingDatasetRows": len(training_records),
                    "selectedPackages": [store.package_id for store in stores],
                    **summary,
                },
            }
        finally:
            if training_tmp_path.exists():
                training_tmp_path.unlink()
            if features_tmp_path.exists():
                features_tmp_path.unlink()

    @staticmethod
    def _records_for_selected_feature_build(store: DatasetStore, records: list[dict], package_count: int) -> list[dict]:
        if package_count <= 1:
            return records

        combined_records: list[dict] = []
        for record in records:
            combined = deepcopy(record)
            group_id = str(record.get("groupId") or "")
            combined["groupId"] = f"{store.package_id}::{group_id}" if group_id else store.package_id
            combined.setdefault("trainingPackageId", store.package_id)
            combined.setdefault("trainingExportId", store.export_id)
            combined_records.append(combined)
        return combined_records

    def features_payload(self) -> dict:
        if not self.store:
            return {"exists": False}
        path = self.store.features_path
        reviewed_path = self.store.reviewed_path
        training_dataset_path = self.store.training_dataset_path
        payload = {
            "path": str(path),
            "exists": path.exists(),
            "reviewedPath": str(reviewed_path),
            "reviewedRows": len(self.store.reviewed),
            "pendingReviewedRows": len(self.store.reviewed),
            "trainingDatasetPath": str(training_dataset_path),
            "trainingDatasetRows": len(self.store.training_dataset),
            "stale": False,
            "staleReasons": [],
        }
        if path.exists():
            payload.update(summarize_features(path))
            stale_reasons: list[str] = []
            if self.store.reviewed:
                stale_reasons.append("pending-reviewed")
            if training_dataset_path.exists():
                if training_dataset_path.stat().st_mtime > path.stat().st_mtime:
                    stale_reasons.append("training-dataset-newer")
            payload["staleReasons"] = stale_reasons
            payload["stale"] = bool(stale_reasons)
        return payload

    def start_training(self, payload: dict | None = None) -> dict:
        store = self.require_store()
        if self.training_job and self.training_job.status == "running":
            raise ValueError("已有训练任务正在运行。")

        payload = payload or {}
        training_options = normalize_training_options(payload)
        selected_package_ids = payload.get("packageIds")
        explicit_packages = isinstance(selected_package_ids, list) and len(selected_package_ids) > 0
        auto_built_features = False
        selected_context: dict
        if explicit_packages:
            stores = self.package_stores(selected_package_ids)
            selected_build = self.build_selected_training_features(stores)
            features_path = Path(str(selected_build["featurePath"]))
            features_payload = selected_build["features"]
            auto_built_features = True
            selected_context = {
                "selectedPackages": selected_build["packageIds"],
                "selectedPackageCount": selected_build["packageCount"],
                "trainingRecords": selected_build["trainingRecords"],
                "featurePath": selected_build["featurePath"],
                "trainingConfig": training_options,
            }
        else:
            if not store.reviewed and not store.training_dataset:
                raise ValueError("请先完成至少一条人工审核标注，再开始训练。")

            features_payload = self.features_payload()
            if not features_payload.get("exists") or features_payload.get("stale"):
                self.build_features()
                auto_built_features = True
                features_payload = self.features_payload()
            features_path = store.features_path
            selected_context = {
                "selectedPackages": [store.package_id],
                "selectedPackageCount": 1,
                "trainingRecords": int(features_payload.get("trainingDatasetRows") or len(store.training_dataset)),
                "featurePath": str(features_path),
                "trainingConfig": training_options,
            }

        store.model_dir.mkdir(parents=True, exist_ok=True)
        log_prefix = "combined" if explicit_packages else store.export_id
        log_name = f"{log_prefix}_train_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
        command = [
            self.python,
            str(self.tool_root / "training" / "train_lightgbm.py"),
            "--features",
            str(features_path),
            "--output-dir",
            str(store.model_dir),
            "--max-rounds",
            str(training_options["maxRounds"]),
            "--early-stopping-rounds",
            str(training_options["earlyStoppingRounds"]),
            "--seed",
            str(training_options["seed"]),
            "--defer-simulation",
            "--simulation-sample-groups",
            "0",
            "--simulation-min-groups",
            "1",
            "--simulation-mode",
            "full",
        ]
        self.training_job = ProcessJob(command, self.tool_root, self.reports_root / log_name)
        self.simulation_job = None
        self.training_context = selected_context
        self.training_job.start()
        return {
            "ok": True,
            "autoBuiltFeatures": auto_built_features,
            "features": features_payload,
            "training": self.training_payload(),
            **selected_context,
        }

    def cancel_training(self) -> dict:
        if not self.training_job or self.training_job.status != "running":
            raise ValueError("没有正在运行的训练任务。")
        canceled = self.training_job.cancel()
        return {"ok": True, "canceled": canceled, "training": self.training_payload()}

    def training_payload(self) -> dict:
        if not self.training_job:
            return {"status": "idle", "statusLabel": training_status_label("idle"), "lines": []}
        payload = self.training_job.payload()
        payload["statusLabel"] = training_status_label(str(payload.get("status") or "idle"))
        payload.update(self.training_context)
        return payload

    def start_simulation_test(self, payload: dict | None = None) -> dict:
        store = self.require_store()
        if self.training_job and self.training_job.status == "running":
            raise ValueError("训练任务仍在运行，完成后才能开始模拟测试。")
        if self.simulation_job and self.simulation_job.status == "running":
            raise ValueError("已有模拟测试正在运行。")

        payload = payload or {}
        model_dir = store.model_dir
        state_path = model_dir / "AFR.GlyphCore.TrainingState.json"
        state = read_json_or_none(state_path) or {}
        training_config = state.get("trainingConfig") if isinstance(state.get("trainingConfig"), dict) else {}
        simulation_config = state.get("simulation") if isinstance(state.get("simulation"), dict) else {}
        raw_features_path = payload.get("featurePath") or state.get("featuresPath") or self.training_context.get("featurePath") or store.features_path
        features_path = Path(str(raw_features_path))
        if not features_path.is_absolute():
            features_path = (self.tool_root / features_path).resolve()
        if not features_path.exists():
            raise FileNotFoundError(f"找不到 Feature 文件：{features_path}")
        model_path = model_dir / "AFR.GlyphCore.Model.txt"
        if not model_path.exists():
            raise FileNotFoundError(f"找不到已训练模型：{model_path}")

        min_groups = clamp_int(
            payload.get("simulationMinGroups") or simulation_config.get("minimumGroups"),
            1,
            1,
            100000,
        )
        sample_groups = clamp_int(
            payload.get("simulationSampleGroups") or simulation_config.get("sampleGroups"),
            0,
            0,
            100000,
        )
        seed = clamp_int(training_config.get("seed"), DEFAULT_TRAINING_OPTIONS["seed"], 1, 2147483647)
        log_name = f"{store.export_id}_simulate_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
        command = [
            self.python,
            str(self.tool_root / "training" / "train_lightgbm.py"),
            "--simulate-only",
            "--features",
            str(features_path),
            "--output-dir",
            str(model_dir),
            "--seed",
            str(seed),
            "--simulation-sample-groups",
            str(sample_groups),
            "--simulation-min-groups",
            str(min_groups),
            "--simulation-mode",
            "full",
        ]
        self.simulation_job = ProcessJob(command, self.tool_root, self.reports_root / log_name)
        self.simulation_job.start()
        return {"ok": True, "simulation": self.simulation_payload(), "report": self.report_payload()}

    def simulation_payload(self) -> dict:
        if self.simulation_job:
            payload = self.simulation_job.payload()
            status = str(payload.get("status") or "idle")
            if status != "running":
                model_dir = self.store.model_dir if self.store else self.model_root
                manifest = read_json_or_none(model_dir / "AFR.GlyphCore.ModelManifest.json") or {}
                summary = read_json_or_none(model_dir / "training_summary.json") or {}
                simulation = first_dict_value("simulation", manifest, summary) or {}
                merged_status = str(simulation.get("status") or status)
                payload = {**payload, **simulation, "status": merged_status}
                status = merged_status
            payload["statusLabel"] = payload.get("label") or simulation_status_label(status)
            return payload

        model_dir = self.store.model_dir if self.store else self.model_root
        manifest = read_json_or_none(model_dir / "AFR.GlyphCore.ModelManifest.json") or {}
        summary = read_json_or_none(model_dir / "training_summary.json") or {}
        simulation = first_dict_value("simulation", manifest, summary) or {}
        status = str(simulation.get("status") or "idle")
        return {
            "status": status,
            "statusLabel": simulation.get("label") or simulation_status_label(status),
            **simulation,
        }

    def report_payload(self) -> dict:
        model_dir = self.store.model_dir if self.store else self.model_root
        manifest_path = model_dir / "AFR.GlyphCore.ModelManifest.json"
        blind_report_path = model_dir / "blind_validation_report.json"
        test_report_path = model_dir / "test_validation_report.json"
        train_report_path = model_dir / "train_validation_report.json"
        valid_report_path = model_dir / "valid_validation_report.json"
        summary_path = model_dir / "training_summary.json"
        history_path = model_dir / "training_history.jsonl"
        onnx_path = model_dir / "AFR.GlyphCore.Model.onnx"
        manifest = read_json_or_none(manifest_path)
        blind_report = read_json_or_none(blind_report_path) or read_json_or_none(test_report_path)
        test_report = blind_report
        train_report = read_json_or_none(train_report_path)
        valid_report = read_json_or_none(valid_report_path)
        summary = read_json_or_none(summary_path)
        history = read_training_history(history_path)
        model_version = (
            manifest.get("modelVersion")
            if isinstance(manifest, dict)
            else None
        ) or (
            summary.get("modelVersion")
            if isinstance(summary, dict)
            else None
        )
        training_status = self.training_job.status if self.training_job else ("succeeded" if manifest_path.exists() else "idle")
        training_result = {
            "status": training_status,
            "label": training_status_label(training_status),
            "detail": training_result_detail(training_status, manifest, test_report),
        }
        acceptance = first_dict_value("acceptance", manifest, summary) or derive_acceptance(test_report)
        overfitting = first_dict_value("overfitting", manifest, summary) or {}
        split_summary = first_dict_value("splitSummary", manifest, summary) or {}
        training_config = first_dict_value("trainingConfig", manifest, summary) or {}
        simulation = self.simulation_payload()
        error_samples = validation_error_samples(test_report)
        repo_root = find_repo_root(self.tool_root)
        release_command = (
            f'$env:AFR_GLYPHCORE_MODEL_PATH = "{onnx_path}"\n'
            f'$env:AFR_GLYPHCORE_MODEL_MANIFEST_PATH = "{manifest_path}"\n'
            f'cd "{repo_root}"\n'
            ".\\tools\\Publish-ReleaseAssets.ps1"
        )
        return {
            "exists": bool(manifest_path.exists() or test_report_path.exists() or onnx_path.exists()),
            "modelDir": str(model_dir),
            "onnxPath": str(onnx_path),
            "manifestPath": str(manifest_path),
            "testReportPath": str(test_report_path),
            "blindReportPath": str(blind_report_path),
            "modelVersion": model_version,
            "modelCreatedUtc": manifest.get("createdUtc") if isinstance(manifest, dict) else None,
            "trainingResult": training_result,
            "manifest": manifest,
            "testReport": test_report,
            "blindReport": blind_report,
            "trainReport": train_report,
            "validReport": valid_report,
            "summary": summary,
            "trainingConfig": training_config,
            "splitSummary": split_summary,
            "acceptance": acceptance,
            "overfitting": overfitting,
            "simulation": simulation,
            "errorSamples": error_samples,
            "history": history,
            "releaseCommand": release_command,
        }

    def reset_model(self) -> dict:
        if self.training_job and self.training_job.status == "running":
            raise ValueError("训练任务仍在运行，不能重置模型。")
        if self.simulation_job and self.simulation_job.status == "running":
            raise ValueError("模拟测试仍在运行，不能重置模型。")

        self.model_root.mkdir(parents=True, exist_ok=True)
        trash_dir = unique_child_path(self.model_root / ".trash", timestamp_id())
        resettable_names = {
            "AFR.GlyphCore.Model.onnx",
            "AFR.GlyphCore.Model.txt",
            "AFR.GlyphCore.ModelManifest.json",
            "AFR.GlyphCore.TrainingState.json",
            "training_history.jsonl",
            "training_summary.json",
        }
        moved: list[dict] = []
        for item in list(self.model_root.iterdir()):
            if item.name == ".trash" or item.is_dir():
                continue
            if item.name not in resettable_names and not item.name.endswith("_validation_report.json"):
                continue
            target = unique_child_path(trash_dir, item.name)
            trash_dir.mkdir(parents=True, exist_ok=True)
            shutil.move(str(item), str(target))
            moved.append({"from": str(item), "to": str(target)})
        self.training_job = None
        self.simulation_job = None
        self.training_context = {}
        return {
            "ok": True,
            "reset": len(moved),
            "trashPath": str(trash_dir),
            "moved": moved,
            "report": self.report_payload(),
            "training": self.training_payload(),
        }

    def require_store(self) -> DatasetStore:
        if not self.store:
            raise ValueError("没有可用数据包。请先运行 AFRGLYPHCOREEXPORT 导出图纸数据。")
        return self.store


def training_status_label(status: str) -> str:
    labels = {
        "idle": "未开始训练",
        "running": "训练中",
        "succeeded": "训练完成",
        "failed": "训练失败",
        "canceled": "已取消",
    }
    return labels.get(status, status or "未知状态")


def simulation_status_label(status: str) -> str:
    labels = {
        "idle": "未开始模拟测试",
        "pending": "等待手动模拟测试",
        "pending-simulation": "等待手动模拟测试",
        "running": "模拟测试中",
        "succeeded": "模拟测试完成",
        "completed": "模拟测试完成",
        "failed": "模拟测试失败",
    }
    return labels.get(status, status or "未知状态")


def utf8_subprocess_env() -> dict[str, str]:
    env = os.environ.copy()
    env["PYTHONIOENCODING"] = "utf-8"
    env["PYTHONUTF8"] = "1"
    return env


def normalize_training_options(payload: dict) -> dict:
    raw = payload.get("trainingOptions") if isinstance(payload.get("trainingOptions"), dict) else payload
    return {
        "maxRounds": clamp_int(raw.get("maxRounds"), DEFAULT_TRAINING_OPTIONS["maxRounds"], 1, 5000),
        "earlyStoppingRounds": clamp_int(raw.get("earlyStoppingRounds"), DEFAULT_TRAINING_OPTIONS["earlyStoppingRounds"], 1, 500),
        "seed": clamp_int(raw.get("seed"), DEFAULT_TRAINING_OPTIONS["seed"], 1, 2147483647),
        "autoEarlyStopping": True,
    }


def clamp_int(value: object, default: int, minimum: int, maximum: int) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        parsed = default
    return max(minimum, min(maximum, parsed))


def training_result_detail(status: str, manifest: object, test_report: object) -> str:
    if status == "running":
        return "训练进程正在本机运行，日志会持续刷新。"
    if status == "failed":
        return "训练进程失败，请查看训练日志中的错误。"
    if not isinstance(manifest, dict):
        return "尚未生成可用模型。"
    acceptance = manifest.get("acceptance") if isinstance(manifest.get("acceptance"), dict) else {}
    if str(acceptance.get("status") or "") == "pending-simulation":
        return "训练完成；需要在模型报告页手动开始全量模拟测试后，才会生成模拟指标。"
    summary = test_report.get("summary") if isinstance(test_report, dict) else {}
    false_repair_rate = float((summary or {}).get("falseRepairRate") or 0)
    recall = float((summary or {}).get("repairRecall") or 0)
    return f"训练完成；验证误修率 {false_repair_rate * 100:.2f}%，修复召回率 {recall * 100:.2f}%。"


def first_dict_value(key: str, *sources: object) -> dict | None:
    for source in sources:
        if isinstance(source, dict) and isinstance(source.get(key), dict):
            return source.get(key)
    return None


def derive_acceptance(test_report: object) -> dict:
    summary = test_report.get("summary") if isinstance(test_report, dict) else {}
    false_repairs = int((summary or {}).get("falseRepairs") or 0)
    status = "passed" if false_repairs == 0 and int((summary or {}).get("groups") or 0) > 0 else "failed"
    return {
        "status": status,
        "label": "通过" if status == "passed" else "未通过",
        "canPublish": status == "passed",
        "blockers": [] if status == "passed" else ["simulation-false-repairs"],
        "policy": "simulation-false-repairs-must-be-zero-and-no-severe-overfit",
    }


def validation_error_samples(test_report: object, limit: int = 200) -> list[dict]:
    if not isinstance(test_report, dict):
        return []
    details = test_report.get("details")
    if not isinstance(details, list):
        return []
    severity_rank = {"false-repair": 0, "wrong-repair": 1, "missed-repair": 2}
    errors = [
        detail for detail in details
        if isinstance(detail, dict) and str(detail.get("severity") or "ok") != "ok"
    ]
    errors.sort(
        key=lambda item: (
            severity_rank.get(str(item.get("severity") or ""), 9),
            -float(item.get("score") or 0),
            str(item.get("groupId") or ""),
        )
    )
    return errors[:limit]


def read_training_history(path: Path, limit: int = 20) -> list[dict]:
    if not path.exists():
        return []
    rows: list[dict] = []
    with path.open("r", encoding="utf-8") as reader:
        for line in reader:
            if not line.strip():
                continue
            try:
                value = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(value, dict):
                rows.append(value)
    return rows[-limit:][::-1]


class WorkbenchHandler(BaseHTTPRequestHandler):
    server_version = "AFRGlyphCoreWorkbench/1.0"

    def do_GET(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        path = parsed.path
        try:
            if path == "/":
                index_path = UI_DIST_DIR / "index.html"
                if index_path.exists():
                    self._send_file(index_path)
                else:
                    self._send_json(
                        {
                            "ok": False,
                            "error": "React workbench assets are missing. Run npm run build in AFR.GlyphCore/tools/workbench/frontend or start through Start-GlyphCoreWorkbench.ps1.",
                        },
                        status=503,
                    )
            elif path == "/api/health":
                self._send_json({"ok": True})
            elif path == "/api/bootstrap":
                self._send_json(self.state.bootstrap_payload())
            elif path == "/api/packages":
                self._send_json({"packages": self.state.list_packages()})
            elif path == "/api/data":
                self._send_json(self.state.store.data_payload() if self.state.store else empty_data_payload())
            elif path == "/api/review-groups":
                self._send_json(self.state.review_groups_payload())
            elif path == "/api/review-clusters":
                self._send_json(self.state.review_clusters_payload())
            elif path == "/api/review-group-records":
                query = parse_qs(parsed.query)
                self._send_json(
                    self.state.review_group_records_payload(
                        (query.get("reviewGroupId") or [""])[0],
                        (query.get("bucket") or ["representatives"])[0],
                    )
                )
            elif path == "/api/review-cluster-records":
                query = parse_qs(parsed.query)
                self._send_json(
                    self.state.review_cluster_records_payload(
                        (query.get("reviewClusterId") or query.get("reviewGroupId") or [""])[0],
                        (query.get("bucket") or ["representatives"])[0],
                    )
                )
            elif path == "/api/training-dataset":
                self._send_json(self.state.training_dataset_payload())
            elif path == "/api/training-dataset/export":
                query = parse_qs(parsed.query)
                export = self.state.export_training_dataset((query.get("format") or ["csv"])[0])
                self._send_download(export["data"], export["filename"], export["contentType"])
            elif path == "/api/features":
                self._send_json(self.state.features_payload())
            elif path == "/api/train":
                self._send_json(self.state.training_payload())
            elif path == "/api/report":
                self._send_json(self.state.report_payload())
            elif path.startswith("/assets/"):
                self._send_static_asset(path)
            else:
                self.send_error(HTTPStatus.NOT_FOUND)
        except Exception as exc:  # pragma: no cover - displayed by UI
            self._send_error_json(exc)

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        try:
            payload = self._read_json_body()
            if path == "/api/package":
                self._send_json({"ok": True, "bootstrap": self.state.select_package(str(payload.get("package") or ""))})
            elif path == "/api/package/delete":
                self._send_json(self.state.delete_package(payload))
            elif path == "/api/label":
                self._send_json(self.state.save_label(payload))
            elif path == "/api/confirm-group":
                self._send_json(self.state.confirm_group(payload))
            elif path == "/api/confirm-cluster":
                self._send_json(self.state.confirm_cluster(payload))
            elif path == "/api/review-table/confirm":
                self._send_json(self.state.confirm_review_table_rows(payload))
            elif path == "/api/review-table/reset":
                self._send_json(self.state.reset_review_rows(payload))
            elif path == "/api/batch-label":
                self._send_json(self.state.save_batch_label(payload))
            elif path == "/api/batch-label-groups":
                self._send_json(self.state.save_batch_label_groups(payload))
            elif path == "/api/batch-rollback":
                self._send_json(self.state.rollback_batch(payload))
            elif path == "/api/training-dataset/delete":
                self._send_json(self.state.delete_training_dataset_records(payload))
            elif path == "/api/training-dataset/import":
                self._send_json(self.state.import_training_dataset(payload))
            elif path == "/api/features":
                self._send_json(self.state.build_features())
            elif path == "/api/train":
                self._send_json(self.state.start_training(payload))
            elif path == "/api/train/cancel":
                self._send_json(self.state.cancel_training())
            elif path == "/api/simulate-test":
                self._send_json(self.state.start_simulation_test(payload))
            elif path == "/api/model/reset":
                self._send_json(self.state.reset_model())
            else:
                self.send_error(HTTPStatus.NOT_FOUND)
        except Exception as exc:  # pragma: no cover - displayed by UI
            self._send_error_json(exc)

    @property
    def state(self) -> WorkbenchState:
        return self.server.state  # type: ignore[attr-defined]

    def log_message(self, format: str, *args: object) -> None:
        sys.stderr.write("[%s] %s\n" % (self.log_date_time_string(), format % args))

    def _read_json_body(self) -> dict:
        content_length = int(self.headers.get("Content-Length") or "0")
        if content_length <= 0:
            return {}
        body = self.rfile.read(content_length)
        return json.loads(body.decode("utf-8") or "{}")

    def _send_json(self, value: object, status: int = 200) -> None:
        data = json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _send_download(self, data: bytes, filename: str, content_type: str) -> None:
        filename = str(filename or "download").replace('"', "")
        fallback = "".join(char if 32 <= ord(char) < 127 and char not in {'"', "\\"} else "_" for char in filename) or "download"
        encoded_filename = quote(filename.encode("utf-8"))
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Disposition", f'attachment; filename="{fallback}"; filename*=UTF-8\'\'{encoded_filename}')
        self.send_header("Cache-Control", "no-store, max-age=0")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _send_error_json(self, exc: Exception) -> None:
        self._send_json({"ok": False, "error": f"{type(exc).__name__}: {exc}"}, status=400)

    def _send_static_asset(self, path: str) -> None:
        if not UI_DIST_DIR.exists():
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        asset_path = ensure_under(UI_DIST_DIR / path.lstrip("/"), UI_DIST_DIR)
        if not asset_path.exists() or not asset_path.is_file():
            self.send_error(HTTPStatus.NOT_FOUND)
            return
        self._send_file(asset_path)

    def _send_file(self, path: Path) -> None:
        data = path.read_bytes()
        content_type = mimetypes.guess_type(str(path))[0] or "application/octet-stream"
        if path.suffix.lower() == ".html":
            content_type = "text/html; charset=utf-8"
        elif path.suffix.lower() in {".js", ".mjs"}:
            content_type = "text/javascript; charset=utf-8"
        elif path.suffix.lower() == ".css":
            content_type = "text/css; charset=utf-8"
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Cache-Control", "no-store, max-age=0")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class WorkbenchServer(ThreadingHTTPServer):
    def __init__(self, address: tuple[str, int], state: WorkbenchState) -> None:
        super().__init__(address, WorkbenchHandler)
        self.state = state


def parse_candidate_index(value: object) -> int | None:
    if value is None or value == "":
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def empty_data_payload() -> dict:
    return {
        "packageId": "",
        "manifest": {},
        "records": [],
        "reviewed": {},
        "trainingDataset": {"records": [], "summary": {"total": 0, "featureRows": 0, "labelActions": {}}},
        "summary": {"total": 0, "sourceTotal": 0, "reviewed": 0, "remaining": 0, "trained": 0, "problems": 0, "highRisk": 0, "actions": {}},
        "paths": {},
    }


def ensure_under(path: Path, root: Path) -> Path:
    resolved = path.resolve()
    root_resolved = root.resolve()
    resolved.relative_to(root_resolved)
    return resolved


def timestamp_id() -> str:
    return datetime.now().strftime("%Y%m%d_%H%M%S_%f")


def safe_name(value: str) -> str:
    text = str(value or "").strip() or "item"
    return "".join(char if char.isalnum() or char in {"-", "_", "."} else "_" for char in text)[:120]


def unique_child_path(root: Path, name: str) -> Path:
    root.mkdir(parents=True, exist_ok=True)
    base = root / safe_name(name)
    if not base.exists():
        return base
    for index in range(1, 1000):
        candidate = root / f"{safe_name(name)}_{index}"
        if not candidate.exists():
            return candidate
    raise FileExistsError(f"cannot allocate unique archive path under {root}")


def find_repo_root(start: Path) -> Path:
    current = start.resolve()
    for candidate in [current, *current.parents]:
        if (candidate / "CADFontAutoReplace.slnx").exists():
            return candidate
    return current


def read_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def read_json_or_none(path: Path) -> dict | None:
    if not path.exists():
        return None
    try:
        return read_json(path)
    except Exception:
        return None


def count_jsonl(path: Path) -> int:
    if not path.exists():
        return 0
    with path.open("r", encoding="utf-8") as reader:
        return sum(1 for line in reader if line.strip())


def summarize_features(path: Path) -> dict:
    rows = 0
    groups: set[str] = set()
    labels: dict[str, int] = {}
    positives = 0
    with path.open("r", encoding="utf-8-sig", newline="") as reader:
        csv_reader = csv.DictReader(reader)
        feature_columns = [
            name
            for name in (csv_reader.fieldnames or [])
            if name.startswith("f") and len(name) > 1 and name[1].isdigit() and "_" in name
        ]
        for row in csv_reader:
            rows += 1
            groups.add(row.get("group_id") or "")
            label = row.get("label_action") or ""
            labels[label] = labels.get(label, 0) + 1
            if str(row.get("is_positive") or "0") == "1":
                positives += 1
    return {
        "rows": rows,
        "groups": len([group for group in groups if group]),
        "labelActions": labels,
        "positiveRows": positives,
        "featureColumns": len(feature_columns),
        "modifiedUtc": datetime.fromtimestamp(path.stat().st_mtime, timezone.utc).isoformat(),
    }


def tsv(value: object) -> str:
    return str(value or "").replace("\t", " ").replace("\r", " ").replace("\n", " ")


def now_utc() -> str:
    return datetime.now(timezone.utc).isoformat()


def main() -> int:
    parser = argparse.ArgumentParser(description="Open the local AFR GlyphCore DBText AI training workbench.")
    default_tool_root = Path(__file__).resolve().parents[1]
    default_repo_root = find_repo_root(default_tool_root)
    parser.add_argument("--tool-root", default=str(default_tool_root), help="GlyphCore DbText tool root.")
    parser.add_argument("--dataset-root", default=str(default_repo_root / "AFR.GlyphCore" / "datasets"), help="GlyphCore dataset root.")
    parser.add_argument("--model-root", default=str(default_repo_root / "AFR.GlyphCore" / "models"), help="GlyphCore model root.")
    parser.add_argument("--package", default="", help="Optional ExtractedCandidates package id or path.")
    parser.add_argument("--host", default="127.0.0.1", help="Loopback host.")
    parser.add_argument("--port", type=int, default=0, help="Loopback port, 0 means auto.")
    parser.add_argument("--no-open", action="store_true", help="Do not open the default browser.")
    args = parser.parse_args()

    state = WorkbenchState(Path(args.tool_root), Path(args.dataset_root), Path(args.model_root), sys.executable, args.package or None)
    server = WorkbenchServer((args.host, args.port), state)
    host, port = server.server_address
    url = f"http://{host}:{port}/"
    print(f"AFR GlyphCore training workbench: {url}")
    print(f"Tool root: {state.tool_root}")
    print(f"Dataset root: {state.dataset_root}")
    print(f"Model root: {state.model_root}")
    if state.store:
        print(f"Active package: {state.store.package_dir}")
    else:
        print("No active package. Run AFRGLYPHCOREEXPORT first, then refresh the workbench.")
    print("Press Ctrl+C to stop.")
    if not args.no_open:
        time.sleep(0.2)
        webbrowser.open(url)

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nStopped.")
    finally:
        server.server_close()
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
