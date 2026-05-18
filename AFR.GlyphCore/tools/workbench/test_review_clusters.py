from __future__ import annotations

import csv
import json
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

TOOL_ROOT = Path(__file__).resolve().parents[1]
if str(TOOL_ROOT) not in sys.path:
    sys.path.insert(0, str(TOOL_ROOT))

from afr_glyphcore import FEATURE_COUNT, FEATURE_SCHEMA_VERSION
from server import DatasetStore, ProcessJob, WorkbenchState


class ReviewClusterTests(unittest.TestCase):
    def test_shx_number_sign_alias_is_display_only(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            store = self._create_store(root, [self._display_alias_record("FL-井1")])

            cluster = store.review_clusters_payload()["clusters"][0]
            self.assertEqual("FL-#1", cluster["currentText"])
            self.assertEqual("FL-井1", cluster["rawCurrentText"])
            self.assertEqual("FL-#1", cluster["candidateText"])

            result = store.confirm_cluster(
                {
                    "reviewClusterId": cluster["id"],
                    "labelAction": "keep",
                    "labelText": "FL-#1",
                    "reviewer": "unit",
                    "representativeGroupId": cluster["representativeRecords"][0]["groupId"],
                }
            )

            self.assertEqual(1, result["written"])
            training_record = next(iter(store.training_dataset.values()))
            self.assertEqual("FL-井1", training_record["currentText"])
            self.assertEqual("FL-井1", training_record["labelText"])

            digest = store.training_dataset_payload()["records"][0]
            self.assertEqual("FL-#1", digest["currentText"])
            self.assertEqual("FL-#1", digest["labelText"])
            self.assertEqual("FL-井1", digest["rawCurrentText"])

    def test_shx_number_sign_alias_does_not_rewrite_chinese_well_text(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            store = self._create_store(root, [self._display_alias_record("检查井1")])

            cluster = store.review_clusters_payload()["clusters"][0]
            self.assertEqual("检查井1", cluster["currentText"])
            self.assertEqual("检查井1", cluster["rawCurrentText"])

    def test_repeated_text_forms_one_cluster_and_propagates_once(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            store = self._create_store(root, self._records(1000))

            payload = store.review_clusters_payload()
            clusters = payload["clusters"]

            self.assertEqual(1, len(clusters))
            cluster = clusters[0]
            self.assertEqual("review-cluster-v3", cluster["ruleVersion"])
            self.assertEqual(1000, cluster["unreviewedCount"])
            self.assertEqual(0, cluster["reviewRequiredCount"])
            self.assertGreater(cluster["riskSignalCount"], 0)
            self.assertGreater(cluster["contextSummary"]["uniqueContexts"], 1)

            result = store.confirm_cluster(
                {
                    "reviewClusterId": cluster["id"],
                    "labelAction": "repair",
                    "labelText": "人工修复",
                    "reviewer": "unit",
                    "representativeGroupId": cluster["representativeRecords"][0]["groupId"],
                }
            )

            self.assertEqual(1000, result["written"])
            self.assertEqual(0, len(store.reviewed))
            self.assertEqual(1000, len(store.training_dataset))
            first_reviewed = next(iter(store.training_dataset.values()))
            self.assertEqual(cluster["id"], first_reviewed["propagationClusterId"])
            self.assertEqual(cluster["propagationSignature"], first_reviewed["propagationSignature"])
            self.assertEqual("人工修复", first_reviewed["labelText"])
            self.assertEqual("manual-review", first_reviewed["candidates"][first_reviewed["selectedCandidateIndex"]]["source"])

            feature_output = root / "features.csv"
            completed = subprocess.run(
                [
                    sys.executable,
                    str(Path(__file__).resolve().parents[1] / "training" / "build_features.py"),
                    "--input",
                    str(store.training_dataset_path),
                    "--output",
                    str(feature_output),
                ],
                cwd=str(Path(__file__).resolve().parents[1]),
                text=True,
                encoding="utf-8",
                errors="replace",
                capture_output=True,
            )
            self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
            self.assertTrue(feature_output.exists())

            rollback = store.rollback_batch(
                {
                    "batchId": result["batchId"],
                    "propagationClusterId": cluster["id"],
                }
            )
            self.assertEqual(1000, rollback["removed"])
            self.assertEqual(0, len(store.reviewed))
            self.assertEqual(0, len(store.training_dataset))

    def test_normal_text_clusters_require_human_confirmation(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            records = []
            records.extend(self._normal_records("n", "12345", 1000))
            records.extend(self._normal_records("cn", "陡坡", 20))
            records.extend(self._repair_like_records("bad", 7))
            store = self._create_store(root, records)

            payload = store.review_clusters_payload()
            clusters = payload["clusters"]
            human_pending = [cluster for cluster in clusters if cluster["humanReviewRequired"]]
            noop_clusters = [cluster for cluster in clusters if cluster["groupType"] == "noop"]

            self.assertEqual(3, len(human_pending))
            self.assertEqual(3, payload["summary"]["humanPendingClusters"])
            self.assertNotIn("systemKeepRecords", payload["summary"])
            self.assertEqual(2, len(noop_clusters))
            self.assertTrue(all(cluster["autoReviewPolicy"] == "human-confirm" for cluster in clusters))
            self.assertTrue(all(cluster["recommendedAction"] == "keep" for cluster in noop_clusters))
            self.assertTrue(all(cluster["canConfirm"] is True for cluster in noop_clusters))

            cluster = next(cluster for cluster in noop_clusters if cluster["currentText"] == "12345")
            result = store.confirm_cluster(
                {
                    "reviewClusterId": cluster["id"],
                    "labelAction": "keep",
                    "labelText": "12345",
                    "reviewer": "unit",
                    "representativeGroupId": cluster["representativeRecords"][0]["groupId"],
                }
            )

            self.assertEqual(1000, result["written"])
            self.assertEqual(0, len(store.reviewed))
            self.assertEqual(1000, len(store.training_dataset))
            for reviewed in store.training_dataset.values():
                self.assertEqual("keep", reviewed["labelAction"])
                self.assertEqual("unit", reviewed["reviewer"])
                self.assertEqual("cluster-sampled", reviewed["propagationScope"])
                selected = reviewed["candidates"][reviewed["selectedCandidateIndex"]]
                self.assertTrue(selected["isNoOp"])

            remaining = store.review_clusters_payload()
            remaining_human = [cluster for cluster in remaining["clusters"] if cluster["humanReviewRequired"] and cluster["unreviewedCount"] > 0]
            self.assertEqual(2, len(remaining_human))

    def test_noop_cluster_can_be_manually_repaired_without_system_override(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            store = self._create_store(root, self._normal_records("safe", "12345", 5))

            payload = store.review_clusters_payload()
            cluster = next(cluster for cluster in payload["clusters"] if cluster["groupType"] == "noop")

            result = store.confirm_cluster(
                {
                    "reviewClusterId": cluster["id"],
                    "labelAction": "repair",
                    "labelText": "12345A",
                    "reviewer": "unit",
                    "representativeGroupId": cluster["representativeRecords"][0]["groupId"],
                }
            )

            self.assertEqual(5, result["written"])
            self.assertEqual(0, len(store.reviewed))
            self.assertEqual(5, len(store.training_dataset))
            for reviewed in store.training_dataset.values():
                self.assertEqual("repair", reviewed["labelAction"])
                self.assertEqual("12345A", reviewed["labelText"])
                self.assertEqual("browser-workbench-propagation", reviewed["origin"])
                selected = reviewed["candidates"][reviewed["selectedCandidateIndex"]]
                self.assertEqual("manual-review", selected["source"])

    def test_review_table_rows_can_update_already_reviewed_clusters(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            store = self._create_store(root, self._normal_records("edit", "12345", 3))
            cluster = store.review_clusters_payload()["clusters"][0]

            first = store.confirm_review_table_rows(
                {
                    "rows": [
                        {
                            "reviewGroupId": cluster["id"],
                            "labelAction": "repair",
                            "labelText": "第一次修正",
                            "candidateIndex": 0,
                        }
                    ],
                    "reviewer": "unit",
                    "note": "table-review",
                }
            )
            self.assertEqual(3, first["written"])
            self.assertEqual(0, len(store.reviewed))
            self.assertEqual(3, len(store.training_dataset))
            self.assertTrue(all(item["labelText"] == "第一次修正" for item in store.training_dataset.values()))
            self.assertEqual(0, len(store.review_clusters_payload()["clusters"]))

    def test_training_dataset_promotes_hides_and_delete_reflows_records(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            state = self._create_state(root, self._normal_records("train", "12345", 3))
            store = state.require_store()
            cluster = store.review_clusters_payload()["clusters"][0]

            store.confirm_review_table_rows(
                {
                    "rows": [
                        {
                            "reviewGroupId": cluster["id"],
                            "labelAction": "keep",
                            "labelText": "12345",
                            "candidateIndex": 1,
                        }
                    ],
                    "reviewer": "unit",
                    "note": "training-dataset-test",
                }
            )
            self.assertEqual(0, len(store.reviewed))
            self.assertEqual(3, len(store.training_dataset))

            built = state.build_features()
            self.assertEqual(0, built["promoted"])
            self.assertEqual(0, len(store.reviewed))
            self.assertEqual(3, len(store.training_dataset))
            self.assertEqual(0, len(store.review_clusters_payload()["clusters"]))
            self.assertTrue(store.features_path.exists())

            deleted_group_id = built["data"]["trainingDataset"]["records"][0]["groupId"]
            delete_result = state.delete_training_dataset_records({"groupIds": [deleted_group_id]})
            self.assertEqual(1, delete_result["removed"])
            self.assertNotIn(deleted_group_id, store.training_dataset)
            self.assertNotIn(deleted_group_id, store.reviewed)

            reflowed = store.review_clusters_payload()["clusters"]
            self.assertEqual(1, len(reflowed))
            self.assertIn(deleted_group_id, reflowed[0]["recordIds"])
            self.assertEqual(1, reflowed[0]["unreviewedCount"])

            feature_group_ids = self._feature_group_ids(store.features_path)
            self.assertNotIn(deleted_group_id, feature_group_ids)

            remaining_group_ids = [record["groupId"] for record in self._read_jsonl(store.training_dataset_path)]
            self.assertNotIn(deleted_group_id, remaining_group_ids)

            remaining_ids = [record["groupId"] for record in built["data"]["trainingDataset"]["records"][1:]]
            delete_all = state.delete_training_dataset_records({"groupIds": remaining_ids})
            self.assertEqual(2, delete_all["removed"])
            self.assertFalse(store.training_dataset_path.exists())
            self.assertFalse(store.features_path.exists())

    def test_review_table_reset_reflows_cluster_and_removes_features(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            state = self._create_state(root, self._normal_records("reset", "12345", 3))
            store = state.require_store()
            cluster = store.review_clusters_payload()["clusters"][0]

            store.confirm_review_table_rows(
                {
                    "rows": [
                        {
                            "reviewGroupId": cluster["id"],
                            "labelAction": "keep",
                            "labelText": "12345",
                            "candidateIndex": 1,
                        }
                    ],
                    "reviewer": "unit",
                    "note": "reset-test",
                }
            )
            state.build_features()
            self.assertTrue(store.features_path.exists())
            self.assertEqual(3, len(store.training_dataset))

            reset = state.reset_review_rows({"reviewGroupIds": [cluster["id"]]})

            self.assertEqual(3, reset["reset"])
            self.assertEqual(0, len(store.reviewed))
            self.assertEqual(0, len(store.training_dataset))
            self.assertFalse(store.training_dataset_path.exists())
            self.assertFalse(store.features_path.exists())
            reflowed = store.review_clusters_payload()["clusters"]
            self.assertEqual(1, len(reflowed))
            self.assertEqual(3, reflowed[0]["unreviewedCount"])

    def test_training_dataset_export_and_import_jsonl_round_trip(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            state = self._create_state(root, self._normal_records("import", "12345", 2))
            store = state.require_store()
            cluster = store.review_clusters_payload()["clusters"][0]
            store.confirm_review_table_rows(
                {
                    "rows": [
                        {
                            "reviewGroupId": cluster["id"],
                            "labelAction": "keep",
                            "labelText": "12345",
                            "candidateIndex": 1,
                        }
                    ],
                    "reviewer": "unit",
                    "note": "export-import-test",
                }
            )

            exported = state.export_training_dataset("jsonl")
            self.assertIn("training_dataset.jsonl", exported["filename"])
            csv_export = state.export_training_dataset("csv")
            self.assertIn("currentText", csv_export["data"].decode("utf-8-sig"))

            state.reset_review_rows({"reviewGroupIds": [cluster["id"]]})
            self.assertEqual(0, len(store.training_dataset))

            imported = state.import_training_dataset(
                {
                    "format": "jsonl",
                    "content": exported["data"].decode("utf-8"),
                }
            )

            self.assertTrue(imported["ok"])
            self.assertEqual(2, imported["imported"])
            self.assertEqual(2, len(store.training_dataset))

    def test_command_export_records_promote_to_model_feature_schema(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            state = self._create_state(root, self._command_export_records())
            store = state.require_store()
            cluster = store.review_clusters_payload()["clusters"][0]

            store.confirm_review_table_rows(
                {
                    "rows": [
                        {
                            "reviewGroupId": cluster["id"],
                            "labelAction": "repair",
                            "labelText": "检修洞800宽X500高,顶到梁底",
                            "candidateIndex": 0,
                        }
                    ],
                    "reviewer": "unit",
                    "note": "command-export-alignment",
                }
            )

            built = state.build_features()

            self.assertEqual(0, built["promoted"])
            self.assertEqual(FEATURE_COUNT, built["features"]["featureColumns"])
            rows = self._read_csv(store.features_path)
            self.assertEqual(1, len(rows))
            row = rows[0]
            self.assertEqual("dbtext-ai-training-row-v1", row["schema"])
            self.assertEqual(FEATURE_SCHEMA_VERSION, row["feature_schema"])
            self.assertEqual("cmd0000", row["group_id"])
            self.assertEqual("repair", row["label_action"])
            self.assertEqual("object", row["native_decode_scope"])
            self.assertEqual("dbtext-deserialize", row["native_decode_hook_hit"])
            feature_columns = [column for column in row if column.startswith("f") and len(column) > 1 and column[1].isdigit()]
            self.assertEqual(FEATURE_COUNT, len(feature_columns))

    def test_delete_package_removes_related_local_files(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            state = self._create_state(root, self._normal_records("delete", "12345", 1))
            store = state.require_store()
            store.reviewed_path.write_text("{}\n", encoding="utf-8")
            store.audit_path.write_text("audit\n", encoding="utf-8")
            store.training_dataset_path.write_text("{}\n", encoding="utf-8")
            store.features_path.write_text("group_id\nx\n", encoding="utf-8")

            result = state.delete_package({"packageId": "pkg"})

            self.assertTrue(result["ok"])
            self.assertFalse(store.package_dir.exists())
            self.assertFalse(store.reviewed_path.exists())
            self.assertFalse(store.training_dataset_path.exists())
            self.assertFalse(store.features_path.exists())
            self.assertNotIn("trashPath", result)
            self.assertGreaterEqual(len(result["deletedPaths"]), 4)
            self.assertEqual([], result["bootstrap"]["packages"])

    def test_cancel_training_job_and_reset_model_archive_outputs(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            state = self._create_state(root, self._normal_records("job", "12345", 1))
            job = ProcessJob(
                [sys.executable, "-c", "import time; time.sleep(10)"],
                root,
                root / "train.log",
            )
            job.start()
            try:
                for _ in range(100):
                    if job.process is not None:
                        break
                    time.sleep(0.01)
                state.training_job = job
                canceled = state.cancel_training()
                self.assertTrue(canceled["canceled"])
                job.thread.join(timeout=5)
                self.assertEqual("canceled", job.status)
            finally:
                if job.status == "running":
                    job.cancel()

            (root / "model").mkdir(exist_ok=True)
            (root / "model" / "AFR.GlyphCore.ModelManifest.json").write_text("{}", encoding="utf-8")
            (root / "model" / "training_summary.json").write_text("{}", encoding="utf-8")

            reset = state.reset_model()

            self.assertTrue(reset["ok"])
            self.assertEqual(2, reset["reset"])
            self.assertFalse((root / "model" / "AFR.GlyphCore.ModelManifest.json").exists())
            self.assertTrue(Path(reset["trashPath"]).exists())

    @staticmethod
    def _create_store(root: Path, records: list[dict]) -> DatasetStore:
        dataset_root = root / "dataset"
        package_dir = dataset_root / "ExtractedCandidates" / "pkg"
        package_dir.mkdir(parents=True)
        (package_dir / "manifest.json").write_text(json.dumps({"exportId": "pkg"}, ensure_ascii=False), encoding="utf-8")
        with (package_dir / "candidate_groups.jsonl").open("w", encoding="utf-8", newline="\n") as writer:
            for record in records:
                writer.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
                writer.write("\n")
        return DatasetStore(dataset_root, root / "model", package_dir)

    @staticmethod
    def _create_state(root: Path, records: list[dict]) -> WorkbenchState:
        dataset_root = root / "dataset"
        package_dir = dataset_root / "ExtractedCandidates" / "pkg"
        package_dir.mkdir(parents=True)
        (package_dir / "manifest.json").write_text(json.dumps({"exportId": "pkg"}, ensure_ascii=False), encoding="utf-8")
        with (package_dir / "candidate_groups.jsonl").open("w", encoding="utf-8", newline="\n") as writer:
            for record in records:
                writer.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
                writer.write("\n")
        return WorkbenchState(Path(__file__).resolve().parents[1], dataset_root, root / "model", sys.executable, "pkg")

    @staticmethod
    def _feature_group_ids(path: Path) -> set[str]:
        with path.open("r", encoding="utf-8-sig", newline="") as reader:
            return {str(row.get("group_id") or "") for row in csv.DictReader(reader)}

    @staticmethod
    def _read_jsonl(path: Path) -> list[dict]:
        with path.open("r", encoding="utf-8") as reader:
            return [json.loads(line) for line in reader if line.strip()]

    @staticmethod
    def _read_csv(path: Path) -> list[dict[str, str]]:
        with path.open("r", encoding="utf-8-sig", newline="") as reader:
            return list(csv.DictReader(reader))

    @staticmethod
    def _command_export_records() -> list[dict]:
        return [
            {
                "schema": "dbtext-ai-candidate-group-v1",
                "featureSchema": FEATURE_SCHEMA_VERSION,
                "exportId": "pkg",
                "groupId": "cmd0000",
                "currentText": "潰党韌800遵X500詢,階善褽菁",
                "drawing": {
                    "fileName": "unit.dwg",
                    "handle": "42",
                    "kind": "DBText",
                },
                "context": {
                    "currentText": "潰党韌800遵X500詢,階善褽菁",
                    "drawingFileName": "unit.dwg",
                    "handle": "42",
                    "layer": "BAD",
                    "ownerBlockName": "*Model_Space",
                    "textStyleName": "HZTXT",
                    "textStyleFileName": "txt.shx",
                    "textStyleBigFontFileName": "tssdchn.shx",
                    "textStyleTypeFace": "",
                    "isFromExternalReference": False,
                    "nativeDecodeEvidence": {
                        "hasEvidence": True,
                        "familyMismatch": True,
                        "scope": "object",
                        "sourceCodePageFamily": "gbk",
                        "appliedCodePageFamily": "big5",
                        "hookHitType": "dbtext-deserialize",
                        "objectCorrelation": 1.0,
                        "clusterCorrelation": 0.25,
                        "hasLdFileFontEvidence": True,
                        "hasHookRawDecodeEvidence": True,
                        "hookRawPayloadLength": 18,
                        "hookPreferredDecodedText": "检修洞800宽X500高,顶到梁底",
                        "hookRawCandidateSource": "ldfile-hook",
                        "hookRawRoundTrip": True,
                        "hookRawConfidence": 0.93,
                        "rippleSeedCount": 4,
                        "rippleContextText": "检修洞",
                        "rippleSeedQuality": 0.82,
                        "rippleDistanceRatio": 0.17,
                    },
                },
                "geometry": {
                    "position": {"x": 12.5, "y": 32.0, "z": 0.0},
                    "rotation": 0.0,
                },
                "risk": {
                    "highRisk": False,
                    "candidateConflict": False,
                    "hasNonRoundTrip": False,
                    "currentUnsafe": False,
                    "candidateUnsafe": False,
                },
                "problemGate": {
                    "hasProblem": True,
                    "reason": "native-decode-evidence",
                },
                "candidates": [
                    {
                        "index": 0,
                        "text": "检修洞800宽X500高,顶到梁底",
                        "source": "big5-carrier-to-gbk",
                        "reason": "roundtrip-ok",
                        "isRoundTrip": True,
                        "isNoOp": False,
                        "hasAiScore": False,
                        "aiScore": None,
                        "unsafeText": False,
                        "features": [0.0] * FEATURE_COUNT,
                    }
                ],
            }
        ]

    @staticmethod
    def _records(count: int) -> list[dict]:
        records: list[dict] = []
        for index in range(count):
            records.append(
                {
                    "schema": "dbtext-ai-candidates-v1",
                    "featureSchema": FEATURE_SCHEMA_VERSION,
                    "groupId": f"r{index:04d}",
                    "currentText": "重复文本",
                    "context": {
                        "handle": f"{index:X}",
                        "layer": f"L{index % 17}",
                        "textStyleName": f"Style{index % 13}",
                        "textStyleFileName": "SIMPLEX",
                        "textStyleBigFontFileName": "GBCBIG",
                        "ownerBlockName": f"Block{index % 19}",
                        "isFromExternalReference": index % 97 == 0,
                    },
                    "risk": {
                        "highRisk": index % 31 == 0,
                        "candidateConflict": index % 37 == 0,
                        "hasNonRoundTrip": False,
                        "currentUnsafe": False,
                        "candidateUnsafe": False,
                    },
                    "problemGate": {
                        "hasProblem": index % 5 == 0,
                        "reason": "unit-test",
                    },
                    "candidates": [
                        {
                            "index": 0,
                            "text": "推荐修复",
                            "source": "big5-carrier-to-gbk",
                            "reason": "roundtrip-ok",
                            "isRoundTrip": True,
                            "isNoOp": False,
                        }
                    ],
                }
            )
        return records

    @staticmethod
    def _normal_records(prefix: str, text: str, count: int) -> list[dict]:
        records: list[dict] = []
        for index in range(count):
            records.append(
                {
                    "schema": "dbtext-ai-candidates-v1",
                    "featureSchema": FEATURE_SCHEMA_VERSION,
                    "groupId": f"{prefix}{index:04d}",
                    "currentText": text,
                    "context": {
                        "handle": f"{prefix}{index:X}",
                        "layer": f"SAFE{index % 11}",
                        "textStyleName": "HZTXT",
                        "textStyleFileName": "txt.shx",
                        "textStyleBigFontFileName": "tssdchn.shx",
                        "ownerBlockName": f"Block{index % 3}",
                        "isFromExternalReference": False,
                    },
                    "risk": {
                        "highRisk": True,
                        "candidateConflict": True,
                        "hasNonRoundTrip": False,
                        "currentUnsafe": False,
                        "candidateUnsafe": False,
                    },
                    "problemGate": {
                        "hasProblem": False,
                        "reason": "no-suspicious-dbtext",
                    },
                    "candidates": [
                        {
                            "index": 0,
                            "text": "皛℡",
                            "source": "big5-carrier-to-gbk",
                            "reason": "roundtrip-ok",
                            "isRoundTrip": True,
                            "isNoOp": False,
                        },
                        {
                            "index": 1,
                            "text": text,
                            "source": "current-noop",
                            "reason": "current text",
                            "isRoundTrip": True,
                            "isNoOp": True,
                        },
                    ],
                }
            )
        return records

    @staticmethod
    def _display_alias_record(text: str) -> dict:
        return {
            "schema": "dbtext-ai-candidate-group-v1",
            "featureSchema": FEATURE_SCHEMA_VERSION,
            "exportId": "pkg",
            "groupId": "alias0000",
            "currentText": text,
            "context": {
                "currentText": text,
                "handle": "A1",
                "layer": "SYMBOL",
                "textStyleName": "HZTXT",
                "textStyleFileName": "txt.shx",
                "textStyleBigFontFileName": "tssdchn.shx",
                "ownerBlockName": "*Model_Space",
                "isFromExternalReference": False,
            },
            "risk": {
                "highRisk": False,
                "candidateConflict": False,
                "hasNonRoundTrip": False,
                "currentUnsafe": False,
                "candidateUnsafe": False,
            },
            "problemGate": {
                "hasProblem": False,
                "reason": "no-suspicious-dbtext",
            },
            "candidates": [
                {
                    "index": 0,
                    "text": text,
                    "source": "current-noop",
                    "reason": "current text",
                    "isRoundTrip": True,
                    "isNoOp": True,
                }
            ],
        }

    @staticmethod
    def _repair_like_records(prefix: str, count: int) -> list[dict]:
        records: list[dict] = []
        current = "潰党韌800遵X500詢,階善褽菁"
        candidate = "检修洞800宽X500高,顶到梁底"
        for index in range(count):
            records.append(
                {
                    "schema": "dbtext-ai-candidates-v1",
                    "featureSchema": FEATURE_SCHEMA_VERSION,
                    "groupId": f"{prefix}{index:04d}",
                    "currentText": current,
                    "context": {
                        "handle": f"{prefix}{index:X}",
                        "layer": "BAD",
                        "textStyleName": "HZTXT",
                        "textStyleFileName": "txt.shx",
                        "textStyleBigFontFileName": "tssdchn.shx",
                        "ownerBlockName": "*Model_Space",
                        "isFromExternalReference": False,
                        "nativeDecodeEvidence": {
                            "hasEvidence": True,
                            "familyMismatch": True,
                            "scope": "object",
                            "sourceCodePageFamily": "gbk",
                            "appliedCodePageFamily": "big5",
                            "hookHitType": "dbtext-deserialize",
                            "objectCorrelation": 1.0,
                            "clusterCorrelation": 0.0,
                        },
                    },
                    "risk": {
                        "highRisk": False,
                        "candidateConflict": False,
                        "hasNonRoundTrip": False,
                        "currentUnsafe": False,
                        "candidateUnsafe": False,
                    },
                    "problemGate": {
                        "hasProblem": True,
                        "reason": "native-dbcs-codepage-mismatch:object:gbk-as-big5",
                    },
                    "candidates": [
                        {
                            "index": 0,
                            "text": candidate,
                            "source": "big5-carrier-to-gbk",
                            "reason": "roundtrip-ok",
                            "isRoundTrip": True,
                            "isNoOp": False,
                        },
                        {
                            "index": 1,
                            "text": current,
                            "source": "current-noop",
                            "reason": "current text",
                            "isRoundTrip": True,
                            "isNoOp": True,
                        },
                    ],
                }
            )
        return records


if __name__ == "__main__":
    unittest.main()
