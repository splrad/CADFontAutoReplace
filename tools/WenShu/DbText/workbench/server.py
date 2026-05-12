from __future__ import annotations

import argparse
import csv
import json
import subprocess
import sys
import threading
import time
import uuid
import webbrowser
from copy import deepcopy
from datetime import datetime, timezone
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse


LABEL_ACTIONS = {"repair", "keep", "unsafe", "unknown", "glyph-issue"}
REVIEWED_SCHEMA = "dbtext-ai-reviewed-label-v1"
LEGACY_TRAINING_SCHEMA = "dbtext-ai-candidates-v1"


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
        self.records = self._load_records()
        self.record_index = {str(item.get("groupId")): item for item in self.records}
        self.reviewed = self._load_reviewed()
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

    def data_payload(self) -> dict:
        return {
            "packageId": self.package_id,
            "manifest": self.manifest,
            "records": self.records,
            "reviewed": self.reviewed,
            "summary": self.summary(),
            "paths": {
                "package": str(self.package_dir),
                "reviewed": str(self.reviewed_path),
                "audit": str(self.audit_path),
                "features": str(self.features_path),
                "models": str(self.model_dir),
            },
        }

    def summary(self) -> dict:
        reviewed_count = len(self.reviewed)
        actions: dict[str, int] = {}
        problem_count = 0
        risk_count = 0
        for record in self.records:
            if (record.get("problemGate") or {}).get("hasProblem"):
                problem_count += 1
            if (record.get("risk") or {}).get("highRisk"):
                risk_count += 1
        for record in self.reviewed.values():
            action = str(record.get("labelAction") or "")
            actions[action] = actions.get(action, 0) + 1
        return {
            "total": len(self.records),
            "reviewed": reviewed_count,
            "remaining": max(0, len(self.records) - reviewed_count),
            "problems": problem_count,
            "highRisk": risk_count,
            "actions": actions,
        }

    def save_label(self, payload: dict) -> dict:
        group_id = str(payload.get("groupId") or "")
        action = str(payload.get("labelAction") or "")
        if group_id not in self.record_index:
            raise ValueError("unknown groupId")
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
            self._rewrite_reviewed_files()

        return {"ok": True, "record": reviewed, "summary": self.summary()}

    def _build_reviewed_record(
        self,
        source_record: dict,
        action: str,
        candidate_index: object,
        label_text: str,
        reviewer: str,
        note: str,
    ) -> dict:
        record = deepcopy(source_record)
        candidates = deepcopy(record.get("candidates") or [])
        selected_index = parse_candidate_index(candidate_index)
        current_text = str(record.get("currentText") or (record.get("context") or {}).get("currentText") or "")

        if action == "repair":
            if selected_index is not None and 0 <= selected_index < len(candidates):
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
        return record

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
            writer.write("group_id\thandle\tlabel_action\tlabel_text\tselected_candidate\treviewer\treviewed_utc\tnote\n")
            for record in records:
                context = record.get("context") or {}
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
                )
                assert process.stdout is not None
                for line in process.stdout:
                    self._append_line(line.rstrip("\n"), log)
                self.return_code = process.wait()
                self.status = "succeeded" if self.return_code == 0 else "failed"
            except Exception as exc:  # pragma: no cover - displayed by UI
                self.return_code = -1
                self.status = "failed"
                self._append_line(f"{type(exc).__name__}: {exc}", log)
            finally:
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
            reviewed_count = count_jsonl(reviewed_path)
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
                }
            )
        items.sort(key=lambda item: str(item.get("modifiedUtc") or ""), reverse=True)
        return items

    def select_package(self, package: str) -> dict:
        package_path = self.resolve_package(package)
        self.store = DatasetStore(self.dataset_root, self.model_root, package_path)
        return self.bootstrap_payload()

    def resolve_package(self, package: str) -> Path:
        package = str(package or "").strip()
        if not package:
            raise ValueError("package is required")
        path = Path(package)
        if not path.is_absolute():
            path = self.extract_root / package
        return ensure_under(path, self.extract_root)

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

    def build_features(self) -> dict:
        store = self.require_store()
        if not store.reviewed_path.exists() or count_jsonl(store.reviewed_path) == 0:
            raise ValueError("请先完成至少一条人工审核标注，再生成 Feature。")

        store.features_path.parent.mkdir(parents=True, exist_ok=True)
        script = self.tool_root / "training" / "build_features.py"
        result = subprocess.run(
            [
                self.python,
                str(script),
                "--input",
                str(store.reviewed_path),
                "--output",
                str(store.features_path),
            ],
            cwd=str(self.tool_root),
            text=True,
            encoding="utf-8",
            errors="replace",
            capture_output=True,
        )
        if result.returncode != 0:
            raise RuntimeError((result.stdout + "\n" + result.stderr).strip())
        return {"ok": True, "message": result.stdout.strip(), "features": self.features_payload()}

    def features_payload(self) -> dict:
        if not self.store:
            return {"exists": False}
        path = self.store.features_path
        reviewed_path = self.store.reviewed_path
        payload = {
            "path": str(path),
            "exists": path.exists(),
            "reviewedPath": str(reviewed_path),
            "reviewedRows": count_jsonl(reviewed_path),
            "stale": False,
        }
        if path.exists():
            payload.update(summarize_features(path))
            if reviewed_path.exists():
                payload["stale"] = reviewed_path.stat().st_mtime > path.stat().st_mtime
        return payload

    def start_training(self) -> dict:
        store = self.require_store()
        if self.training_job and self.training_job.status == "running":
            raise ValueError("已有训练任务正在运行。")
        if not store.reviewed_path.exists() or count_jsonl(store.reviewed_path) == 0:
            raise ValueError("请先完成至少一条人工审核标注，再开始训练。")

        features = self.features_payload()
        auto_built_features = False
        if not features.get("exists") or features.get("stale"):
            self.build_features()
            auto_built_features = True

        store.model_dir.mkdir(parents=True, exist_ok=True)
        log_name = f"{store.export_id}_train_{datetime.now().strftime('%Y%m%d_%H%M%S')}.log"
        command = [
            self.python,
            str(self.tool_root / "training" / "train_lightgbm.py"),
            "--features",
            str(store.features_path),
            "--output-dir",
            str(store.model_dir),
        ]
        self.training_job = ProcessJob(command, self.tool_root, self.reports_root / log_name)
        self.training_job.start()
        return {
            "ok": True,
            "autoBuiltFeatures": auto_built_features,
            "features": self.features_payload(),
            "training": self.training_payload(),
        }

    def training_payload(self) -> dict:
        if not self.training_job:
            return {"status": "idle", "lines": []}
        return self.training_job.payload()

    def report_payload(self) -> dict:
        model_dir = self.store.model_dir if self.store else self.model_root
        manifest_path = model_dir / "AFR.DBTextAI.ModelManifest.json"
        test_report_path = model_dir / "test_validation_report.json"
        summary_path = model_dir / "training_summary.json"
        onnx_path = model_dir / "AFR.DBTextAI.Model.onnx"
        manifest = read_json_or_none(manifest_path)
        test_report = read_json_or_none(test_report_path)
        summary = read_json_or_none(summary_path)
        repo_root = find_repo_root(self.tool_root)
        release_command = (
            f'$env:AFR_WENSHU_DBTEXT_MODEL_PATH = "{onnx_path}"\n'
            f'$env:AFR_WENSHU_DBTEXT_MODEL_MANIFEST_PATH = "{manifest_path}"\n'
            f'cd "{repo_root}"\n'
            ".\\tools\\Publish-ReleaseAssets.ps1"
        )
        return {
            "exists": bool(manifest_path.exists() or test_report_path.exists() or onnx_path.exists()),
            "modelDir": str(model_dir),
            "onnxPath": str(onnx_path),
            "manifestPath": str(manifest_path),
            "testReportPath": str(test_report_path),
            "manifest": manifest,
            "testReport": test_report,
            "summary": summary,
            "releaseCommand": release_command,
        }

    def require_store(self) -> DatasetStore:
        if not self.store:
            raise ValueError("没有可用数据包。请先运行 AFRDBTEXTEXPORTAI 导出图纸数据。")
        return self.store


class WorkbenchHandler(BaseHTTPRequestHandler):
    server_version = "AFRWenShuWorkbench/1.0"

    def do_GET(self) -> None:  # noqa: N802
        path = urlparse(self.path).path
        try:
            if path == "/":
                self._send_text(INDEX_HTML, "text/html; charset=utf-8")
            elif path == "/api/health":
                self._send_json({"ok": True})
            elif path == "/api/bootstrap":
                self._send_json(self.state.bootstrap_payload())
            elif path == "/api/packages":
                self._send_json({"packages": self.state.list_packages()})
            elif path == "/api/data":
                self._send_json(self.state.store.data_payload() if self.state.store else empty_data_payload())
            elif path == "/api/features":
                self._send_json(self.state.features_payload())
            elif path == "/api/train":
                self._send_json(self.state.training_payload())
            elif path == "/api/report":
                self._send_json(self.state.report_payload())
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
            elif path == "/api/label":
                self._send_json(self.state.save_label(payload))
            elif path == "/api/features":
                self._send_json(self.state.build_features())
            elif path == "/api/train":
                self._send_json(self.state.start_training())
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

    def _send_error_json(self, exc: Exception) -> None:
        self._send_json({"ok": False, "error": f"{type(exc).__name__}: {exc}"}, status=400)

    def _send_text(self, value: str, content_type: str) -> None:
        data = value.encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", content_type)
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
        "summary": {"total": 0, "reviewed": 0, "remaining": 0, "problems": 0, "highRisk": 0, "actions": {}},
        "paths": {},
    }


def ensure_under(path: Path, root: Path) -> Path:
    resolved = path.resolve()
    root_resolved = root.resolve()
    resolved.relative_to(root_resolved)
    return resolved


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
        feature_columns = [name for name in (csv_reader.fieldnames or []) if name.startswith("f") and "_" in name]
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
    parser = argparse.ArgumentParser(description="Open the local AFR WenShu DBText AI training workbench.")
    default_tool_root = Path(__file__).resolve().parents[1]
    default_repo_root = find_repo_root(default_tool_root)
    parser.add_argument("--tool-root", default=str(default_tool_root), help="WenShu DbText tool root.")
    parser.add_argument("--dataset-root", default=str(default_repo_root / "datasets" / "WenShu" / "DbText"), help="WenShu DbText dataset root.")
    parser.add_argument("--model-root", default=str(default_repo_root / "models" / "WenShu" / "DbText" / "Current"), help="WenShu DbText current model root.")
    parser.add_argument("--package", default="", help="Optional ExtractedCandidates package id or path.")
    parser.add_argument("--host", default="127.0.0.1", help="Loopback host.")
    parser.add_argument("--port", type=int, default=0, help="Loopback port, 0 means auto.")
    parser.add_argument("--no-open", action="store_true", help="Do not open the default browser.")
    args = parser.parse_args()

    state = WorkbenchState(Path(args.tool_root), Path(args.dataset_root), Path(args.model_root), sys.executable, args.package or None)
    server = WorkbenchServer((args.host, args.port), state)
    host, port = server.server_address
    url = f"http://{host}:{port}/"
    print(f"AFR WenShu training workbench: {url}")
    print(f"Tool root: {state.tool_root}")
    print(f"Dataset root: {state.dataset_root}")
    print(f"Model root: {state.model_root}")
    if state.store:
        print(f"Active package: {state.store.package_dir}")
    else:
        print("No active package. Run AFRDBTEXTEXPORTAI first, then refresh the workbench.")
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


INDEX_HTML = r"""<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>AFR 文枢训练工作台</title>
  <style>
    :root {
      --bg: #f4f6f8;
      --panel: #ffffff;
      --line: #d9dee7;
      --line-strong: #c7cfda;
      --text: #182230;
      --muted: #5f6c7b;
      --subtle: #eef2f6;
      --accent: #0b63ce;
      --accent-soft: #e9f2ff;
      --risk: #b42318;
      --risk-soft: #fff1f3;
      --warn: #b54708;
      --warn-soft: #fff7ed;
      --ok: #067647;
      --ok-soft: #ecfdf3;
      --shadow: 0 10px 24px rgba(16, 24, 40, .08);
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: var(--bg);
      color: var(--text);
      font-family: "Segoe UI", "Microsoft YaHei", Arial, sans-serif;
      font-size: 14px;
      letter-spacing: 0;
    }
    header {
      height: 64px;
      display: flex;
      align-items: center;
      gap: 18px;
      padding: 0 20px;
      background: #111827;
      color: white;
      border-bottom: 1px solid #0f172a;
    }
    h1 { margin: 0; font-size: 18px; font-weight: 650; white-space: nowrap; }
    h2 { margin: 0 0 12px; font-size: 16px; font-weight: 650; }
    h3 { margin: 0 0 10px; font-size: 14px; font-weight: 650; }
    .header-meta {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      color: #cbd5e1;
    }
    .tabs {
      display: flex;
      gap: 6px;
      padding: 10px 12px 0;
    }
    .tab {
      height: 36px;
      border: 1px solid var(--line);
      background: #fff;
      color: var(--muted);
      padding: 0 14px;
      border-radius: 6px 6px 0 0;
      cursor: pointer;
    }
    .tab.active {
      color: var(--accent);
      border-color: var(--line-strong);
      border-bottom-color: #fff;
      font-weight: 650;
    }
    main { height: calc(100vh - 110px); padding: 0 12px 12px; }
    .view { display: none; height: 100%; }
    .view.active { display: block; }
    .grid { height: 100%; display: grid; gap: 10px; }
    .grid.packages { grid-template-columns: 360px 1fr; }
    .grid.review { grid-template-columns: 320px minmax(360px, 1fr) 430px; }
    .grid.two { grid-template-columns: minmax(380px, .9fr) minmax(420px, 1.1fr); }
    .panel {
      min-height: 0;
      background: var(--panel);
      border: 1px solid var(--line);
      border-radius: 7px;
      box-shadow: var(--shadow);
      overflow: hidden;
    }
    .panel-header {
      min-height: 48px;
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 10px 12px;
      border-bottom: 1px solid var(--line);
      background: #fbfcfe;
    }
    .panel-body { padding: 12px; overflow: auto; height: calc(100% - 49px); }
    .list { height: calc(100% - 50px); overflow: auto; }
    .row {
      padding: 10px 12px;
      border-bottom: 1px solid #edf0f4;
      cursor: pointer;
    }
    .row:hover { background: #f8fafc; }
    .row.active { background: var(--accent-soft); }
    .row.reviewed { border-left: 4px solid var(--ok); }
    .row.problem:not(.reviewed) { border-left: 4px solid var(--warn); }
    .row-top { display: flex; justify-content: space-between; gap: 8px; color: var(--muted); font-size: 12px; }
    .row-text { margin-top: 5px; line-height: 1.38; word-break: break-all; }
    .cards { display: grid; grid-template-columns: repeat(4, minmax(110px, 1fr)); gap: 10px; margin-bottom: 12px; }
    .card {
      border: 1px solid var(--line);
      border-radius: 7px;
      padding: 12px;
      background: #fff;
    }
    .card .value { font-size: 24px; font-weight: 700; margin-bottom: 4px; }
    .card .label { color: var(--muted); font-size: 12px; }
    .kv { display: grid; grid-template-columns: 140px 1fr; gap: 8px 12px; }
    .kv div:nth-child(odd) { color: var(--muted); }
    .path { word-break: break-all; color: var(--muted); font-size: 12px; }
    input, select, textarea, button {
      font: inherit;
      letter-spacing: 0;
    }
    input, select, textarea {
      border: 1px solid var(--line);
      border-radius: 5px;
      padding: 8px 9px;
      background: white;
      color: var(--text);
      min-width: 0;
    }
    textarea { min-height: 88px; resize: vertical; }
    button {
      border: 1px solid var(--line-strong);
      border-radius: 5px;
      padding: 8px 11px;
      background: white;
      color: var(--text);
      cursor: pointer;
    }
    button.primary { background: var(--accent); border-color: var(--accent); color: white; font-weight: 650; }
    button.ok { color: var(--ok); }
    button.risk { color: var(--risk); }
    button:disabled { opacity: .55; cursor: not-allowed; }
    .stack { display: grid; gap: 10px; }
    .form-row { display: grid; gap: 6px; margin-bottom: 11px; }
    .actions { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
    .badges { display: flex; flex-wrap: wrap; gap: 6px; margin: 8px 0 12px; }
    .badge {
      display: inline-flex;
      align-items: center;
      border: 1px solid var(--line);
      border-radius: 999px;
      padding: 2px 8px;
      font-size: 12px;
      color: var(--muted);
      background: #f8fafc;
    }
    .badge.warn { border-color: #fed7aa; color: var(--warn); background: var(--warn-soft); }
    .badge.risk { border-color: #fecdd3; color: var(--risk); background: var(--risk-soft); }
    .badge.ok { border-color: #bbf7d0; color: var(--ok); background: var(--ok-soft); }
    .canvas-wrap { height: 38%; border-bottom: 1px solid var(--line); background: #f8fafc; }
    svg { width: 100%; height: 100%; display: block; }
    .detail { height: 62%; overflow: auto; padding: 12px; }
    .candidate {
      border: 1px solid var(--line);
      border-radius: 7px;
      padding: 10px;
      margin-bottom: 8px;
      background: white;
      cursor: pointer;
    }
    .candidate.selected { border-color: var(--accent); background: var(--accent-soft); }
    .candidate-head { display: flex; justify-content: space-between; gap: 8px; color: var(--muted); font-size: 12px; margin-bottom: 5px; }
    .candidate-text { word-break: break-all; line-height: 1.45; }
    pre {
      margin: 0;
      padding: 12px;
      border: 1px solid var(--line);
      border-radius: 6px;
      background: #0f172a;
      color: #e5e7eb;
      overflow: auto;
      min-height: 220px;
      max-height: 460px;
      white-space: pre-wrap;
      word-break: break-word;
      font-size: 12px;
      line-height: 1.45;
    }
    .notice {
      padding: 12px;
      border: 1px solid var(--line);
      border-radius: 7px;
      background: #f8fafc;
      color: var(--muted);
      line-height: 1.55;
    }
    .error { color: var(--risk); }
    @media (max-width: 1180px) {
      main { height: auto; }
      .grid, .grid.packages, .grid.review, .grid.two { grid-template-columns: 1fr; height: auto; }
      .panel { min-height: 320px; }
      .cards { grid-template-columns: repeat(2, minmax(110px, 1fr)); }
    }
  </style>
</head>
<body>
  <header>
    <h1>AFR 文枢训练工作台</h1>
    <div class="header-meta" id="headerMeta">正在加载...</div>
  </header>
  <nav class="tabs">
    <button class="tab active" data-view="packages">数据包</button>
    <button class="tab" data-view="review">标注审核</button>
    <button class="tab" data-view="features">特征生成</button>
    <button class="tab" data-view="training">模型训练</button>
    <button class="tab" data-view="report">模型报告</button>
  </nav>
  <main>
    <section id="view-packages" class="view active">
      <div class="grid packages">
        <div class="panel">
          <div class="panel-header">
            <strong>ExtractedCandidates</strong>
            <button onclick="refresh()" style="margin-left:auto">刷新</button>
          </div>
          <div class="list" id="packageList"></div>
        </div>
        <div class="panel">
          <div class="panel-header"><strong>当前数据包</strong></div>
          <div class="panel-body">
            <div class="cards" id="packageCards"></div>
            <div class="kv" id="packageDetails"></div>
          </div>
        </div>
      </div>
    </section>
    <section id="view-review" class="view">
      <div class="grid review">
        <div class="panel">
          <div class="panel-header">
            <input id="search" placeholder="搜索文本 / 图层 / 样式" style="flex:1">
            <select id="filter">
              <option value="all">全部</option>
              <option value="problem">疑似异常</option>
              <option value="unreviewed">未标注</option>
              <option value="reviewed">已标注</option>
              <option value="risk">高风险</option>
            </select>
          </div>
          <div class="list" id="recordList"></div>
        </div>
        <div class="panel">
          <div class="canvas-wrap"><svg id="preview" viewBox="0 0 100 100" preserveAspectRatio="xMidYMid meet"></svg></div>
          <div class="detail">
            <div class="kv" id="recordDetails"></div>
            <div class="badges" id="badges"></div>
            <h3>Candidate 候选结果</h3>
            <div id="candidateList"></div>
          </div>
        </div>
        <div class="panel">
          <div class="panel-header">
            <strong>人工审核</strong>
            <span id="reviewProgress" style="margin-left:auto;color:var(--muted)"></span>
          </div>
          <div class="panel-body">
            <div class="form-row">
              <label>标注动作</label>
              <select id="labelAction">
                <option value="repair">repair - 修复</option>
                <option value="keep">keep - 保持原文</option>
                <option value="unsafe">unsafe - 不安全</option>
                <option value="unknown">unknown - 无法确认</option>
                <option value="glyph-issue">glyph-issue - 字形/字体问题</option>
              </select>
            </div>
            <div class="form-row">
              <label>最终文本</label>
              <textarea id="labelText" placeholder="repair 时填写正确文本；其它动作默认保留原文"></textarea>
            </div>
            <div class="form-row">
              <label>审核人</label>
              <input id="reviewer" value="developer">
            </div>
            <div class="form-row">
              <label>备注</label>
              <textarea id="note" style="min-height:70px" placeholder="记录判断依据或需复查原因"></textarea>
            </div>
            <div class="path" id="reviewedPath"></div>
            <div class="actions" style="margin-top:12px">
              <button class="ok" onclick="quickLabel('keep')">保持原文</button>
              <button class="risk" onclick="quickLabel('unsafe')">标为不安全</button>
              <button onclick="nextRecord()">下一条</button>
              <button id="saveButton" class="primary" onclick="saveLabel()">保存标注</button>
              <button onclick="goTraining()">去训练</button>
            </div>
          </div>
        </div>
      </div>
    </section>
    <section id="view-features" class="view">
      <div class="grid two">
        <div class="panel">
          <div class="panel-header"><strong>Feature 生成</strong></div>
          <div class="panel-body stack">
            <div class="notice">Feature 表只能从 reviewed JSONL 生成。未审核的 Candidate 数据不会直接进入训练。也可以跳过本页，在“模型训练”中手动点击按钮一次完成 Feature 生成与训练。</div>
            <button class="primary" onclick="buildFeatures()">生成 Feature 表</button>
            <div class="kv" id="featureDetails"></div>
            <div id="featureError" class="error"></div>
          </div>
        </div>
        <div class="panel">
          <div class="panel-header"><strong>标签分布</strong></div>
          <div class="panel-body" id="featureStats"></div>
        </div>
      </div>
    </section>
    <section id="view-training" class="view">
      <div class="grid two">
        <div class="panel">
          <div class="panel-header"><strong>LightGBM 训练</strong></div>
          <div class="panel-body stack">
            <div class="notice">训练只会在手动点击按钮后开始。点击后工作台会先生成或刷新 Feature 表，再在本机后台启动 LightGBM。</div>
            <button class="primary" id="trainButton" onclick="startTraining()">生成 Feature 并训练模型</button>
            <div id="trainingMessage" class="path"></div>
            <div class="kv" id="trainingDetails"></div>
            <div id="trainingError" class="error"></div>
          </div>
        </div>
        <div class="panel">
          <div class="panel-header">
            <strong>训练日志</strong>
            <button onclick="refresh()" style="margin-left:auto">刷新</button>
          </div>
          <div class="panel-body"><pre id="trainLog"></pre></div>
        </div>
      </div>
    </section>
    <section id="view-report" class="view">
      <div class="grid two">
        <div class="panel">
          <div class="panel-header"><strong>模型验证摘要</strong></div>
          <div class="panel-body">
            <div class="cards" id="reportCards"></div>
            <div class="kv" id="reportDetails"></div>
          </div>
        </div>
        <div class="panel">
          <div class="panel-header"><strong>Release 注入命令</strong></div>
          <div class="panel-body stack">
            <pre id="releaseCommand"></pre>
            <button onclick="copyReleaseCommand()">复制命令</button>
          </div>
        </div>
      </div>
    </section>
  </main>
  <script>
    let app = null;
    let selectedId = null;
    let selectedCandidate = null;
    let activeView = 'packages';
    let pollTimer = null;
    let recordById = new Map();
    let saveInFlight = false;

    async function api(path, options) {
      const res = await fetch(path, options || {});
      const data = await res.json();
      if (!res.ok || data.ok === false) throw new Error(data.error || '请求失败');
      return data;
    }

    async function refresh() {
      setApp(await api('/api/bootstrap'));
      const records = getRecords();
      if (!selectedId && records.length) selectedId = records[0].groupId;
      render();
      schedulePoll();
    }

    function setApp(nextApp) {
      app = nextApp;
      recordById = new Map(getRecords().map(record => [record.groupId, record]));
    }

    function getData() { return (app && app.data) || {records: [], reviewed: {}, summary: {}, paths: {}, manifest: {}}; }
    function getRecords() { return getData().records || []; }
    function getReviewed() { return getData().reviewed || {}; }
    function currentRecord() { return recordById.get(selectedId) || getRecords()[0] || null; }

    function render() {
      renderHeader();
      renderPackages();
      renderReview();
      renderFeatures();
      renderTraining();
      renderReport();
    }

    function renderHeader() {
      const data = getData();
      const manifest = data.manifest || {};
      const drawing = manifest.drawing || {};
      document.getElementById('headerMeta').textContent =
        data.packageId ? `${data.packageId} · ${drawing.fileName || ''}` : '未选择数据包';
    }

    function renderPackages() {
      const list = document.getElementById('packageList');
      list.innerHTML = '';
      const packages = (app && app.packages) || [];
      if (!packages.length) {
        list.innerHTML = '<div class="notice" style="margin:12px">未发现数据包。请在 AutoCAD Debug 中运行 AFRDBTEXTEXPORTAI。</div>';
      }
      for (const item of packages) {
        const counts = item.counts || {};
        const drawing = item.drawing || {};
        const row = document.createElement('div');
        row.className = `row ${item.active ? 'active' : ''}`;
        row.onclick = () => selectPackage(item.id);
        row.innerHTML = `
          <div class="row-top"><span>${escapeHtml(drawing.fileName || item.id)}</span><span>${escapeHtml(String(counts.exported || 0))} 项</span></div>
          <div class="row-text">${escapeHtml(item.id)}</div>
          <div class="path">${escapeHtml(item.modifiedUtc || '')}</div>`;
        list.appendChild(row);
      }
      const data = getData();
      const s = data.summary || {};
      document.getElementById('packageCards').innerHTML = card('DBText', s.total || 0) + card('疑似异常', s.problems || 0) + card('已审核', s.reviewed || 0) + card('高风险', s.highRisk || 0);
      const manifest = data.manifest || {};
      const drawing = manifest.drawing || {};
      document.getElementById('packageDetails').innerHTML = kv([
        ['数据包', data.packageId || ''],
        ['DWG', drawing.fileName || ''],
        ['DWG hash', drawing.sha256 || ''],
        ['Candidate schema', manifest.candidateGroupSchema || ''],
        ['Feature schema', manifest.featureSchema || ''],
        ['Reviewed JSONL', (data.paths || {}).reviewed || ''],
        ['Package path', (data.paths || {}).package || ''],
      ]);
    }

    async function selectPackage(id) {
      const data = await api('/api/package', {
        method: 'POST',
        headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({package: id})
      });
      setApp(data.bootstrap);
      selectedId = null;
      selectedCandidate = null;
      const records = getRecords();
      if (records.length) selectedId = records[0].groupId;
      render();
    }

    function filteredRecords() {
      const q = (document.getElementById('search')?.value || '').trim().toLowerCase();
      const filter = document.getElementById('filter')?.value || 'all';
      return getRecords().filter(r => {
        const ctx = r.context || {};
        const text = [r.currentText, ctx.layer, ctx.textStyleName, ctx.ownerBlockName, ctx.handle].join(' ').toLowerCase();
        if (q && !text.includes(q)) return false;
        const reviewed = !!getReviewed()[r.groupId];
        if (filter === 'problem' && !(r.problemGate && r.problemGate.hasProblem)) return false;
        if (filter === 'reviewed' && !reviewed) return false;
        if (filter === 'unreviewed' && reviewed) return false;
        if (filter === 'risk' && !(r.risk && r.risk.highRisk)) return false;
        return true;
      });
    }

    function renderReview() {
      renderReviewStatus();
      renderRecordList();
      renderPreview();
      renderRecordDetails();
    }

    function renderReviewStatus() {
      const data = getData();
      const summary = data.summary || {};
      document.getElementById('reviewProgress').textContent = `${summary.reviewed || 0}/${summary.total || 0}`;
      document.getElementById('reviewedPath').textContent = `Reviewed: ${(data.paths || {}).reviewed || ''}`;
    }

    function renderRecordList() {
      const list = document.getElementById('recordList');
      list.innerHTML = '';
      const fragment = document.createDocumentFragment();
      for (const record of filteredRecords()) {
        const ctx = record.context || {};
        const reviewed = getReviewed()[record.groupId];
        const div = document.createElement('div');
        div.className = `row ${record.groupId === selectedId ? 'active' : ''} ${reviewed ? 'reviewed' : ''} ${record.problemGate?.hasProblem ? 'problem' : ''}`;
        div.dataset.groupId = record.groupId;
        div.onclick = () => selectRecord(record.groupId);
        div.innerHTML = `
          <div class="row-top"><span>${escapeHtml(ctx.handle || '')}</span><span>${escapeHtml(ctx.layer || '')}</span></div>
          <div class="row-text">${escapeHtml(record.currentText || '')}</div>`;
        fragment.appendChild(div);
      }
      list.appendChild(fragment);
      updateRecordSelection();
    }

    function renderPreview() {
      const svg = document.getElementById('preview');
      svg.innerHTML = '';
      const points = getRecords().map(r => ({r, p: r.geometry && r.geometry.position})).filter(x => x.p);
      if (!points.length) return;
      const xs = points.map(x => Number(x.p.x) || Number(x.p.X) || 0);
      const ys = points.map(x => Number(x.p.y) || Number(x.p.Y) || 0);
      const minX = Math.min(...xs), maxX = Math.max(...xs);
      const minY = Math.min(...ys), maxY = Math.max(...ys);
      const w = Math.max(1, maxX - minX), h = Math.max(1, maxY - minY);
      const fragment = document.createDocumentFragment();
      for (const item of points) {
        const px = Number(item.p.x) || Number(item.p.X) || 0;
        const py = Number(item.p.y) || Number(item.p.Y) || 0;
        const x = 5 + ((px - minX) / w) * 90;
        const y = 95 - ((py - minY) / h) * 90;
        const c = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        c.classList.add('preview-point');
        c.dataset.groupId = item.r.groupId;
        c.dataset.problem = item.r.problemGate?.hasProblem ? 'true' : 'false';
        c.setAttribute('cx', x);
        c.setAttribute('cy', y);
        c.setAttribute('r', item.r.groupId === selectedId ? 1.8 : 0.9);
        c.setAttribute('fill', item.r.groupId === selectedId ? '#0b63ce' : (item.r.problemGate?.hasProblem ? '#b54708' : '#98a2b3'));
        c.style.cursor = 'pointer';
        c.addEventListener('click', () => selectRecord(item.r.groupId));
        fragment.appendChild(c);
      }
      svg.appendChild(fragment);
      updatePreviewSelection();
    }

    function renderRecordDetails() {
      const record = currentRecord();
      const details = document.getElementById('recordDetails');
      const badges = document.getElementById('badges');
      const candidates = document.getElementById('candidateList');
      details.innerHTML = '';
      badges.innerHTML = '';
      candidates.innerHTML = '';
      if (!record) {
        details.innerHTML = '<div></div><div class="notice">没有可审核记录。</div>';
        return;
      }
      const ctx = record.context || {};
      const reviewed = getReviewed()[record.groupId];
      details.innerHTML = kv([
        ['Handle', ctx.handle],
        ['Layer', ctx.layer],
        ['Block', ctx.ownerBlockName],
        ['TextStyle', ctx.textStyleName],
        ['Font', ctx.textStyleFileName],
        ['BigFont', ctx.textStyleBigFontFileName],
        ['门控原因', record.problemGate?.reason],
        ['Label', reviewed ? reviewed.labelAction : '未标注'],
      ]);
      addBadge(record.problemGate?.hasProblem ? '疑似异常' : '未命中门控', record.problemGate?.hasProblem ? 'warn' : '');
      if (record.risk?.highRisk) addBadge('高风险', 'risk');
      if (ctx.isFromExternalReference) addBadge('Xref', 'risk');
      if (record.risk?.candidateConflict) addBadge('候选冲突', 'risk');
      if (record.risk?.hasNonRoundTrip) addBadge('不可逆候选', 'risk');
      if (reviewed) addBadge('已审核', 'ok');

      if (reviewed) {
        document.getElementById('labelAction').value = reviewed.labelAction || 'repair';
        document.getElementById('labelText').value = reviewed.labelText || '';
        document.getElementById('note').value = reviewed.originDetail || '';
        selectedCandidate = reviewed.selectedCandidateIndex;
      } else {
        document.getElementById('labelAction').value = 'repair';
        document.getElementById('labelText').value = '';
        document.getElementById('note').value = '';
      }

      (record.candidates || []).forEach((candidate, index) => {
        const div = document.createElement('div');
        div.className = `candidate ${selectedCandidate === index ? 'selected' : ''}`;
        div.dataset.index = String(index);
        div.onclick = () => selectCandidate(index);
        const score = candidate.hasAiScore ? `score ${candidate.aiScore}` : 'no score';
        div.innerHTML = `
          <div class="candidate-head"><span>#${index} · ${escapeHtml(candidate.source || '')}</span><span>${escapeHtml(score)}</span></div>
          <div class="candidate-text">${escapeHtml(candidate.text || '')}</div>
          <div class="candidate-head"><span>${escapeHtml(candidate.reason || '')}</span><span>${candidate.isRoundTrip ? 'roundtrip' : 'non-roundtrip'}</span></div>`;
        candidates.appendChild(div);
      });
      updateCandidateSelection();
    }

    function selectRecord(groupId) {
      if (!groupId || groupId === selectedId) return;
      selectedId = groupId;
      selectedCandidate = null;
      renderRecordDetails();
      updateSelectionState();
    }

    function updateSelectionState() {
      updateRecordSelection();
      updatePreviewSelection();
      updateCandidateSelection();
    }

    function updateRecordSelection() {
      document.querySelectorAll('#recordList .row').forEach(row => {
        row.classList.toggle('active', row.dataset.groupId === selectedId);
      });
    }

    function updateReviewedRow(groupId) {
      const rows = document.querySelectorAll('#recordList .row');
      for (const row of rows) {
        if (row.dataset.groupId === groupId) {
          row.classList.toggle('reviewed', !!getReviewed()[groupId]);
          return;
        }
      }
    }

    function updatePreviewSelection() {
      document.querySelectorAll('#preview .preview-point').forEach(point => {
        const active = point.dataset.groupId === selectedId;
        point.setAttribute('r', active ? 1.8 : 0.9);
        point.setAttribute('fill', active ? '#0b63ce' : (point.dataset.problem === 'true' ? '#b54708' : '#98a2b3'));
      });
    }

    function updateCandidateSelection() {
      document.querySelectorAll('#candidateList .candidate').forEach(item => {
        item.classList.toggle('selected', Number(item.dataset.index) === selectedCandidate);
      });
    }

    function selectCandidate(index) {
      const record = currentRecord();
      selectedCandidate = index;
      const candidate = record && record.candidates ? record.candidates[index] : null;
      if (candidate) {
        document.getElementById('labelAction').value = candidate.isNoOp ? 'keep' : 'repair';
        document.getElementById('labelText').value = candidate.text || '';
      }
      updateCandidateSelection();
    }

    function quickLabel(action) {
      const record = currentRecord();
      if (!record) return;
      document.getElementById('labelAction').value = action;
      document.getElementById('labelText').value = record.currentText || '';
      saveLabel();
    }

    async function saveLabel() {
      if (saveInFlight) return;
      const record = currentRecord();
      if (!record) return;
      const savedId = record.groupId;
      const saveButton = document.getElementById('saveButton');
      saveInFlight = true;
      if (saveButton) saveButton.disabled = true;
      try {
        const res = await fetch('/api/label', {
          method: 'POST',
          headers: {'Content-Type': 'application/json'},
          body: JSON.stringify({
            groupId: record.groupId,
            labelAction: document.getElementById('labelAction').value,
            candidateIndex: selectedCandidate,
            labelText: document.getElementById('labelText').value,
            reviewer: document.getElementById('reviewer').value,
            note: document.getElementById('note').value
          })
        });
        const data = await res.json();
        if (!res.ok || data.ok === false) throw new Error(data.error || '请求失败');
        const label = data.label || {};
        const currentData = getData();
        currentData.reviewed = currentData.reviewed || {};
        if (label.record) currentData.reviewed[savedId] = label.record;
        if (label.summary) currentData.summary = label.summary;
        renderReviewStatus();
        const filter = document.getElementById('filter')?.value || 'all';
        if (filter === 'reviewed' || filter === 'unreviewed') {
          renderRecordList();
        } else {
          updateReviewedRow(savedId);
        }
        nextRecord(false);
      } finally {
        saveInFlight = false;
        if (saveButton) saveButton.disabled = false;
      }
    }

    function nextRecord(fullRender) {
      const visible = filteredRecords();
      if (!visible.length) return;
      const currentIndex = visible.findIndex(r => r.groupId === selectedId);
      const nextIndex = currentIndex >= 0 ? (currentIndex + 1) % visible.length : 0;
      selectedId = visible[nextIndex].groupId;
      selectedCandidate = null;
      if (fullRender) {
        render();
      } else {
        renderRecordDetails();
        updateSelectionState();
      }
    }

    async function buildFeatures() {
      document.getElementById('featureError').textContent = '';
      try {
        const data = await api('/api/features', {method: 'POST', headers: {'Content-Type': 'application/json'}, body: '{}'});
        app.features = data.features;
        renderFeatures();
      } catch (err) {
        document.getElementById('featureError').textContent = err.message;
      }
    }

    function renderFeatures() {
      const f = (app && app.features) || {};
      document.getElementById('featureDetails').innerHTML = kv([
        ['状态', f.exists ? '已生成' : '未生成'],
        ['是否需刷新', f.stale ? '是' : '否'],
        ['Reviewed 行数', f.reviewedRows || 0],
        ['Feature CSV', f.path || ''],
        ['行数', f.rows || 0],
        ['样本组', f.groups || 0],
        ['正样本行', f.positiveRows || 0],
        ['Feature 列', f.featureColumns || 0],
        ['更新时间', f.modifiedUtc || ''],
      ]);
      const labels = f.labelActions || {};
      document.getElementById('featureStats').innerHTML = Object.keys(labels).length
        ? Object.entries(labels).map(([k,v]) => `<div class="card"><div class="value">${escapeHtml(v)}</div><div class="label">${escapeHtml(k)}</div></div>`).join('')
        : '<div class="notice">暂无 Feature 统计。</div>';
    }

    async function startTraining() {
      document.getElementById('trainingError').textContent = '';
      document.getElementById('trainingMessage').textContent = '';
      try {
        const data = await api('/api/train', {method: 'POST', headers: {'Content-Type': 'application/json'}, body: '{}'});
        if (data.features) app.features = data.features;
        app.training = data.training;
        document.getElementById('trainingMessage').textContent = data.autoBuiltFeatures
          ? '已根据 reviewed JSONL 生成/刷新 Feature 表，并开始训练。'
          : 'Feature 表已是最新，开始训练。';
        renderTraining();
        schedulePoll(true);
      } catch (err) {
        document.getElementById('trainingError').textContent = err.message;
      }
    }

    function renderTraining() {
      const t = (app && app.training) || {status: 'idle', lines: []};
      const f = (app && app.features) || {};
      document.getElementById('trainButton').disabled = t.status === 'running';
      document.getElementById('trainingDetails').innerHTML = kv([
        ['状态', translateStatus(t.status)],
        ['Feature 状态', f.exists ? (f.stale ? '需刷新，点击训练时会刷新' : '已就绪') : '未生成，点击训练时会生成'],
        ['Reviewed 行数', f.reviewedRows || 0],
        ['开始时间', t.startedUtc || ''],
        ['结束时间', t.endedUtc || ''],
        ['返回码', t.returnCode === null || t.returnCode === undefined ? '' : t.returnCode],
        ['日志文件', t.logPath || ''],
      ]);
      document.getElementById('trainLog').textContent = (t.lines || []).join('\n');
    }

    async function pollTraining() {
      if (!app || !app.training || app.training.status !== 'running') return;
      app.training = await api('/api/train');
      if (app.training.status !== 'running') {
        const latest = await api('/api/bootstrap');
        setApp(latest);
      }
      renderTraining();
      renderReport();
      schedulePoll();
    }

    function schedulePoll(force) {
      if (pollTimer) clearTimeout(pollTimer);
      const running = app && app.training && app.training.status === 'running';
      if (running || force) pollTimer = setTimeout(pollTraining, 1200);
    }

    function renderReport() {
      const r = (app && app.report) || {};
      const summary = r.testReport && r.testReport.summary ? r.testReport.summary : {};
      const manifest = r.manifest || {};
      document.getElementById('reportCards').innerHTML =
        card('误修率', pct(summary.falseRepairRate)) +
        card('正确修复', summary.correctRepairs || 0) +
        card('漏修', summary.missedRepairs || 0) +
        card('跳过', summary.skipped || 0);
      document.getElementById('reportDetails').innerHTML = kv([
        ['模型版本', manifest.modelVersion || ''],
        ['Feature schema', manifest.featureSchemaVersion || ''],
        ['训练数据 hash', manifest.trainingDataHash || ''],
        ['最低置信度', manifest.minimumConfidence || ''],
        ['最小分差', manifest.minimumScoreMargin || ''],
        ['ONNX', r.onnxPath || ''],
        ['Manifest', r.manifestPath || ''],
        ['验证报告', r.testReportPath || ''],
      ]);
      document.getElementById('releaseCommand').textContent = r.releaseCommand || '';
    }

    function switchView(view) {
      activeView = view;
      document.querySelectorAll('.tab').forEach(tab => tab.classList.toggle('active', tab.dataset.view === view));
      document.querySelectorAll('.view').forEach(panel => panel.classList.toggle('active', panel.id === `view-${view}`));
    }

    function goTraining() {
      switchView('training');
      renderTraining();
    }

    function addBadge(text, cls) {
      const span = document.createElement('span');
      span.className = `badge ${cls || ''}`;
      span.textContent = text;
      document.getElementById('badges').appendChild(span);
    }

    function kv(rows) {
      return rows.map(([k,v]) => `<div>${escapeHtml(k)}</div><div>${escapeHtml(v ?? '')}</div>`).join('');
    }

    function card(label, value) {
      return `<div class="card"><div class="value">${escapeHtml(value)}</div><div class="label">${escapeHtml(label)}</div></div>`;
    }

    function translateStatus(status) {
      return {idle: '未开始', running: '运行中', succeeded: '成功', failed: '失败'}[status] || status || '';
    }

    function pct(value) {
      const n = Number(value);
      if (!Number.isFinite(n)) return '0%';
      return `${(n * 100).toFixed(3)}%`;
    }

    function copyReleaseCommand() {
      const text = document.getElementById('releaseCommand').textContent;
      if (navigator.clipboard && text) navigator.clipboard.writeText(text);
    }

    function escapeHtml(value) {
      return String(value ?? '').replace(/[&<>"']/g, ch => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
      }[ch]));
    }

    document.querySelectorAll('.tab').forEach(tab => tab.addEventListener('click', () => switchView(tab.dataset.view)));
    document.getElementById('search').addEventListener('input', renderRecordList);
    document.getElementById('filter').addEventListener('change', renderRecordList);
    document.addEventListener('keydown', event => {
      if (event.ctrlKey && event.key === 'Enter') saveLabel();
      if (event.key === 'ArrowDown' && activeView === 'review') nextRecord();
    });
    refresh().catch(err => {
      document.getElementById('headerMeta').textContent = err.message;
    });
  </script>
</body>
</html>
"""


if __name__ == "__main__":
    raise SystemExit(main())
