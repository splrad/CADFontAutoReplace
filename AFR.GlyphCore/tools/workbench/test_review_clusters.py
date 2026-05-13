from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from server import DatasetStore, WorkbenchState


class ReviewClusterTests(unittest.TestCase):
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
        import csv

        with path.open("r", encoding="utf-8-sig", newline="") as reader:
            return {str(row.get("group_id") or "") for row in csv.DictReader(reader)}

    @staticmethod
    def _records(count: int) -> list[dict]:
        records: list[dict] = []
        for index in range(count):
            records.append(
                {
                    "schema": "dbtext-ai-candidates-v1",
                    "featureSchema": "dbtext-ai-features-v1",
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
                    "featureSchema": "dbtext-ai-features-v1",
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
    def _repair_like_records(prefix: str, count: int) -> list[dict]:
        records: list[dict] = []
        current = "潰党韌800遵X500詢,階善褽菁"
        candidate = "检修洞800宽X500高,顶到梁底"
        for index in range(count):
            records.append(
                {
                    "schema": "dbtext-ai-candidates-v1",
                    "featureSchema": "dbtext-ai-features-v1",
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
