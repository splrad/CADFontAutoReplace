from __future__ import annotations

import json
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

TRAINING_ROOT = Path(__file__).resolve().parent
TOOL_ROOT = TRAINING_ROOT.parent
sys.path.insert(0, str(TRAINING_ROOT))
sys.path.insert(0, str(TOOL_ROOT))

from generate_high_value_data import (  # noqa: E402
    FEATURE_SCHEMA_VERSION,
    REVIEWED_SCHEMA,
    generate_augmented_dataset,
    load_source_corpus,
    write_generation_outputs,
)


class HighValueDataGeneratorTests(unittest.TestCase):
    def test_generation_is_deterministic_and_quota_balanced(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            dataset_root = self._create_fixture_dataset(Path(temp_dir))
            corpus = load_source_corpus(dataset_root)
            base_utc = datetime(2026, 5, 14, tzinfo=timezone.utc)

            first = generate_augmented_dataset(corpus, dataset_root, "unit_high_value", 250, 7, "unit", base_utc)
            second = generate_augmented_dataset(corpus, dataset_root, "unit_high_value", 250, 7, "unit", base_utc)

            self.assertEqual(
                json.dumps(first.training_records, ensure_ascii=False, sort_keys=True),
                json.dumps(second.training_records, ensure_ascii=False, sort_keys=True),
            )
            self.assertEqual(
                {"keep": 120, "repair": 80, "unknown": 40, "unsafe": 5, "glyph-issue": 5},
                self._action_counts(first.training_records),
            )
            self.assertTrue(first.report["validation"]["ok"], first.report["validation"])

    def test_records_have_schema_targets_manual_repairs_and_context_variety(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            dataset_root = self._create_fixture_dataset(Path(temp_dir))
            corpus = load_source_corpus(dataset_root)
            result = generate_augmented_dataset(
                corpus,
                dataset_root,
                "unit_high_value",
                500,
                13,
                "unit",
                datetime(2026, 5, 14, tzinfo=timezone.utc),
            )
            write_generation_outputs(result, build_features=True)

            self.assertEqual(500, len(result.training_records))
            self.assertTrue(result.features_path.exists())
            self.assertEqual(500, len(result.audit_rows))
            self.assertEqual(501, len(result.audit_path.read_text(encoding="utf-8-sig").splitlines()))
            layers = {record["context"].get("layer") for record in result.training_records}
            styles = {record["context"].get("textStyleName") for record in result.training_records}
            self.assertGreater(len(layers), 1)
            self.assertGreater(len(styles), 1)

            manual_positive_repairs = 0
            irreversible_unknowns = 0
            for record in result.training_records:
                self.assertEqual(REVIEWED_SCHEMA, record.get("schema"))
                self.assertEqual(FEATURE_SCHEMA_VERSION, record.get("featureSchema"))
                positives = [
                    candidate for candidate in record.get("candidates", [])
                    if float(candidate.get("targetScore", 0.0)) >= 1.0
                ]
                self.assertEqual(1, len(positives), record.get("groupId"))
                if record.get("labelAction") == "repair" and "manual-review" in positives[0].get("source", ""):
                    manual_positive_repairs += 1
                if record.get("labelAction") == "unknown" and record.get("roundtripStatus") == "irreversible":
                    irreversible_unknowns += 1

            self.assertGreater(manual_positive_repairs, 0)
            self.assertGreater(irreversible_unknowns, 0)

    def _create_fixture_dataset(self, root: Path) -> Path:
        dataset_root = root / "datasets"
        package_root = dataset_root / "ExtractedCandidates" / "pkg1"
        training_root = dataset_root / "TrainingSets"
        package_root.mkdir(parents=True)
        training_root.mkdir(parents=True)
        (package_root / "manifest.json").write_text(
            json.dumps({"exportId": "pkg1", "schema": "dbtext-ai-export-package-v1"}, ensure_ascii=False),
            encoding="utf-8",
        )

        records = [
            self._record("r1", "潰党韌800遵X500詢,階善褽菁", "检修洞800宽X500高,顶到梁底", "repair", "PUB_TEXT", "_HZTXT"),
            self._record("r2", "GB50016-2014(2018唳)", "GB50016-2014(2018版)", "repair", "0", "HZTXT"),
            self._record("r3", "塑料排水管的安装均详赣97S202。", "塑料排水管的安装均详见97S202。", "repair", "设计说明", "gpshz"),
            self._record("k1", "DN100 消防管", "DN100 消防管", "keep", "TEXT_喷淋", "_TWT_PIPEDN"),
            self._record("k2", "A", "A", "keep", "0", "HZTXT"),
            self._record("k3", "EL+3.500", "EL+3.500", "keep", "DIM_给水", "_TCH_DIM"),
        ]

        with (package_root / "candidate_groups.jsonl").open("w", encoding="utf-8", newline="\n") as writer:
            for record in records:
                candidate_record = dict(record)
                candidate_record["schema"] = "dbtext-ai-candidate-group-v1"
                for key in ["labelAction", "labelText", "selectedCandidateIndex", "reviewer", "reviewedUtc"]:
                    candidate_record.pop(key, None)
                writer.write(json.dumps(candidate_record, ensure_ascii=False, separators=(",", ":")))
                writer.write("\n")

        with (training_root / "pkg1_training_dataset.jsonl").open("w", encoding="utf-8", newline="\n") as writer:
            for record in records:
                writer.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
                writer.write("\n")
        return dataset_root

    def _record(
        self,
        group_id: str,
        current: str,
        label: str,
        action: str,
        layer: str,
        style: str,
    ) -> dict:
        candidates = [
            {
                "index": 0,
                "text": label,
                "source": "manual-review" if action == "repair" else "current-noop",
                "reason": "human-reviewed-correction" if action == "repair" else "当前文本",
                "isRoundTrip": action != "repair",
                "isNoOp": action != "repair",
                "targetScore": 1.0,
            },
            {
                "index": 1,
                "text": current,
                "source": "current-noop",
                "reason": "当前文本",
                "isRoundTrip": True,
                "isNoOp": True,
                "targetScore": 0.0 if action == "repair" else 1.0,
            },
        ]
        return {
            "schema": "dbtext-ai-reviewed-label-v1",
            "featureSchema": "dbtext-ai-features-v4",
            "exportId": "pkg1",
            "groupId": group_id,
            "drawing": {"fileName": "fixture.dwg"},
            "context": {
                "drawingFileName": "fixture.dwg",
                "entityType": "DBText",
                "objectId": f"({group_id})",
                "handle": group_id.upper(),
                "layer": layer,
                "ownerBlockName": "*Model_Space",
                "textStyleName": style,
                "textStyleFileName": "txt.shx",
                "textStyleBigFontFileName": "tssdchn.shx",
                "textStyleTypeFace": "",
                "currentText": current,
                "isFromExternalReference": False,
            },
            "geometry": {"position": {"x": 0.0, "y": 0.0, "z": 0.0}, "height": 750.0},
            "currentText": current,
            "problemGate": {"hasProblem": action == "repair", "reason": "fixture"},
            "risk": {"highRisk": action == "repair", "candidateConflict": True},
            "candidates": candidates,
            "labelAction": action,
            "labelText": label,
            "selectedCandidateIndex": 0,
            "reviewer": "unit",
            "reviewedUtc": "2026-05-14T00:00:00Z",
        }

    def _action_counts(self, records: list[dict]) -> dict[str, int]:
        result: dict[str, int] = {}
        for record in records:
            key = str(record.get("labelAction") or "")
            result[key] = result.get(key, 0) + 1
        return result


if __name__ == "__main__":
    unittest.main()
