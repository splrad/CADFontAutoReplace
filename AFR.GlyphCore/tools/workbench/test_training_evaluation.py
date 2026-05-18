from __future__ import annotations

import sys
import unittest
from pathlib import Path

try:
    import pandas as pd
except ImportError:  # pragma: no cover - developer environments should install requirements.txt
    pd = None

TRAINING_ROOT = Path(__file__).resolve().parents[1] / "training"
sys.path.insert(0, str(TRAINING_ROOT))

from train_lightgbm import (  # noqa: E402
    evaluate_acceptance,
    evaluate_groups,
    evaluate_overfitting,
    full_simulation_slice,
    namespace_duplicate_group_ids,
    split_groups_by_text_pattern,
)


@unittest.skipIf(pd is None, "pandas is required for training evaluation tests")
class TrainingEvaluationTests(unittest.TestCase):
    def test_text_pattern_split_keeps_duplicate_patterns_in_one_split(self) -> None:
        rows = []
        for index in range(12):
            pattern = index % 4
            rows.append(self._feature_row(f"g{index}", f"乱码{pattern}", f"修正{pattern}", "big5-carrier-to-gbk"))
            rows.append(self._feature_row(f"g{index}", f"乱码{pattern}", f"乱码{pattern}", "current-noop"))

        frame = pd.DataFrame(rows)
        split = split_groups_by_text_pattern(frame, seed=7)
        group_splits = {
            group_id: split_name
            for split_name, group_ids in split["groupIds"].items()
            for group_id in group_ids
        }

        for pattern in range(4):
            owners = {
                group_splits[f"g{index}"]
                for index in range(12)
                if index % 4 == pattern
            }
            self.assertEqual(1, len(owners), f"pattern {pattern} leaked across splits")

        summary = split["summary"]
        self.assertEqual(0, summary["leakageCheck"]["groupOverlap"])
        self.assertEqual(0, summary["leakageCheck"]["duplicatePatternAcrossSplits"])
        self.assertGreater(summary["blindGroups"], 0)

    def test_evaluate_groups_reports_false_and_missed_errors(self) -> None:
        rows = [
            self._scored_row("false", "keep", "原文", "错误修复", "原文", "big5-carrier-to-gbk", 0.99, False, True),
            self._scored_row("false", "keep", "原文", "原文", "原文", "current-noop", 0.10, True, True),
            self._scored_row("missed", "repair", "乱码", "正确", "正确", "big5-carrier-to-gbk", 0.40, False, True),
            self._scored_row("missed", "repair", "乱码", "乱码", "正确", "current-noop", 0.30, True, True),
        ]

        report = evaluate_groups(pd.DataFrame(rows))
        summary = report["summary"]
        severities = {item["groupId"]: item["severity"] for item in report["details"]}

        self.assertEqual(1, summary["falseRepairs"])
        self.assertEqual(1, summary["missedRepairs"])
        self.assertEqual("false-repair", severities["false"])
        self.assertEqual("missed-repair", severities["missed"])

    def test_evaluate_groups_treats_visible_equal_text_as_match(self) -> None:
        rows = [
            self._scored_row("normalized", "repair", "概口抑 1.40MPa", "阀前压力\u200b1.40ＭＰａ", "阀前压力1.40MPa", "big5-carrier-to-gbk", 0.99, False, True),
            self._scored_row("normalized", "repair", "概口抑 1.40MPa", "概口抑 1.40MPa", "阀前压力1.40MPa", "current-noop", 0.10, True, True),
        ]

        report = evaluate_groups(pd.DataFrame(rows))
        summary = report["summary"]
        detail = report["details"][0]

        self.assertEqual(1, summary["correctRepairs"])
        self.assertEqual(0, summary["falseRepairs"])
        self.assertEqual("ok", detail["severity"])

    def test_evaluate_groups_treats_shx_number_sign_alias_as_visible_match(self) -> None:
        rows = [
            self._scored_row("alias", "repair", "FL-井1", "FL-井1", "FL-#1", "current-noop", 0.99, True, True),
            self._scored_row("alias", "repair", "FL-井1", "FL-#1", "FL-#1", "gbk-carrier-to-big5", 0.20, False, True),
        ]

        report = evaluate_groups(pd.DataFrame(rows))
        summary = report["summary"]
        detail = report["details"][0]

        self.assertTrue(detail["correct"])
        self.assertEqual(1, summary["correctRepairs"])
        self.assertEqual(0, summary["missedRepairs"])
        self.assertEqual("ok", detail["severity"])

    def test_evaluate_groups_applies_runtime_context_ripple_from_nearby_seed(self) -> None:
        rows = [
            self._scored_row("seed", "repair", "錯A", "正确", "正确", "gbk-carrier-to-big5", 0.99, False, True, x=0, y=0, native=True),
            self._scored_row("seed", "repair", "錯A", "錯A", "正确", "current-noop", 0.10, True, True, x=0, y=0, native=True),
            self._scored_row("target", "repair", "錯B", "錯B", "正确", "current-noop", 0.55, True, True, x=10, y=0),
            self._scored_row("target", "repair", "錯B", "正确", "正确", "gbk-carrier-to-big5", 0.45, False, True, x=10, y=0),
        ]

        report = evaluate_groups(pd.DataFrame(rows))
        summary = report["summary"]
        details = {item["groupId"]: item for item in report["details"]}

        self.assertEqual(2, summary["correctRepairs"])
        self.assertEqual(1, summary["runtimeContextRippleRepairs"])
        self.assertTrue(details["target"]["runtimeContextRipple"])
        self.assertEqual("ok", details["target"]["severity"])

    def test_evaluate_groups_repairs_confirmed_single_character_label(self) -> None:
        rows = [
            self._scored_row("confirmed", "repair", "棒", "堵", "堵", "gbk-carrier-to-big5", 0.99, False, True),
            self._scored_row("confirmed", "repair", "棒", "次", "堵", "big5-carrier-to-gbk", 0.20, False, True),
            self._scored_row("confirmed", "repair", "棒", "棒", "堵", "current-noop", 0.10, True, True),
        ]

        report = evaluate_groups(pd.DataFrame(rows))
        summary = report["summary"]
        detail = report["details"][0]

        self.assertEqual("repair", detail["decision"])
        self.assertEqual("ok", detail["severity"])
        self.assertEqual(1, summary["correctRepairs"])
        self.assertEqual(0, summary["falseRepairs"])

    def test_duplicate_same_output_candidates_do_not_reduce_margin(self) -> None:
        rows = [
            self._scored_row("duplicate", "repair", "翋猁", "主要设备及材料表", "主要设备及材料表", "big5-carrier-to-gbk", 0.99, False, True),
            self._scored_row("duplicate", "repair", "翋猁", "主要设备及材料表", "主要设备及材料表", "big5-carrier-to-gbk", 0.985, False, True),
            self._scored_row("duplicate", "repair", "翋猁", "翋猁", "主要设备及材料表", "current-noop", 0.20, True, True),
        ]

        report = evaluate_groups(pd.DataFrame(rows))
        summary = report["summary"]
        detail = report["details"][0]

        self.assertEqual("repair", detail["decision"])
        self.assertGreater(detail["margin"], 0.7)
        self.assertEqual(1, summary["correctRepairs"])
        self.assertEqual(0, summary["missedRepairs"])

    def test_duplicate_group_ids_are_namespaced_by_package(self) -> None:
        frame = pd.DataFrame(
            [
                {**self._feature_row("dup", "错A", "正A", "big5-carrier-to-gbk"), "training_package_id": "pkg-a"},
                {**self._feature_row("dup", "错A", "错A", "current-noop"), "training_package_id": "pkg-a"},
                {**self._feature_row("dup", "错B", "正B", "big5-carrier-to-gbk"), "training_package_id": "pkg-b"},
                {**self._feature_row("unique", "错C", "正C", "big5-carrier-to-gbk"), "training_package_id": "pkg-a"},
            ]
        )

        namespaced = namespace_duplicate_group_ids(frame)

        self.assertEqual({"pkg-a::dup", "pkg-b::dup", "unique"}, set(namespaced["group_id"]))

    def test_full_simulation_uses_every_group(self) -> None:
        rows = [
            self._feature_row("g1", "乱码1", "修正1", "big5-carrier-to-gbk"),
            self._feature_row("g1", "乱码1", "乱码1", "current-noop"),
            self._feature_row("g2", "乱码2", "修正2", "big5-carrier-to-gbk"),
        ]

        frame, simulation = full_simulation_slice(pd.DataFrame(rows), min_groups=1)

        self.assertEqual(3, len(frame))
        self.assertEqual("full-dataset-v1", simulation["strategy"])
        self.assertEqual(2, simulation["sampledGroups"])
        self.assertEqual(2, simulation["availableGroups"])

    def test_overfitting_report_flags_severe_gap(self) -> None:
        reports = {
            "train": {"summary": {"repairRecall": 0.98, "decisionAccuracy": 0.99}},
            "valid": {"summary": {"repairRecall": 0.92, "decisionAccuracy": 0.95}},
            "blind": {"summary": {"repairRecall": 0.55, "decisionAccuracy": 0.70, "expectedRepairs": 20}},
        }

        result = evaluate_overfitting(reports)

        self.assertEqual("severe", result["status"])
        self.assertTrue(result["severe"])
        self.assertIn("train-recall-much-higher-than-blind", result["reasons"])

    def test_acceptance_blocks_severe_overfit_even_without_false_repairs(self) -> None:
        blind_report = {"summary": {"groups": 100, "falseRepairs": 0}}
        overfitting = {"severe": True}

        acceptance = evaluate_acceptance(blind_report, overfitting)

        self.assertEqual("overfit", acceptance["status"])
        self.assertFalse(acceptance["canPublish"])

    def _feature_row(self, group_id: str, current: str, candidate: str, source: str) -> dict:
        return {
            "group_id": group_id,
            "current_text": current,
            "candidate_text": candidate,
            "source": source,
        }

    def _scored_row(
        self,
        group_id: str,
        label_action: str,
        current: str,
        candidate: str,
        label: str,
        source: str,
        prediction: float,
        is_noop: bool,
        is_roundtrip: bool,
        x: float | None = None,
        y: float | None = None,
        native: bool = False,
    ) -> dict:
        row = {
            "group_id": group_id,
            "label_action": label_action,
            "current_text": current,
            "candidate_text": candidate,
            "label_text": label,
            "source": source,
            "prediction": prediction,
            "is_noop": 1 if is_noop else 0,
            "is_roundtrip": 1 if is_roundtrip else 0,
            "is_from_xref": 0,
            "training_package_id": "pkg",
            "training_export_id": "export",
            "drawing_file_name": "drawing.dwg",
            "layer": "TEXT",
            "owner_block_name": "Model",
            "text_style_name": "Standard",
        }
        if x is not None and y is not None:
            row["position_x"] = x
            row["position_y"] = y
            row["height"] = 1.0
        if native:
            row["native_decode_evidence"] = 1
            row["native_decode_source_family"] = "gbk"
            row["native_decode_applied_family"] = "big5"
            row["native_decode_hook_hit"] = "dbtext-dwgin-fields"
        for column in [
            "f09_current_control_ratio",
            "f10_candidate_control_ratio",
            "f11_current_replacement_ratio",
            "f12_candidate_replacement_ratio",
            "f37_candidate_has_suspicious_unicode",
            "f38_current_has_suspicious_unicode",
            "f62_native_decode_evidence_present",
            "f75_ripple_seed_count_norm",
            "f92_hook_raw_payload_present",
        ]:
            row[column] = 0.0
        return row


if __name__ == "__main__":
    unittest.main()
