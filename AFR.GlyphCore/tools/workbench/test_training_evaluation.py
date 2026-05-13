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
        }
        for column in [
            "f09_current_control_ratio",
            "f10_candidate_control_ratio",
            "f11_current_replacement_ratio",
            "f12_candidate_replacement_ratio",
            "f37_candidate_has_suspicious_unicode",
            "f38_current_has_suspicious_unicode",
        ]:
            row[column] = 0.0
        return row


if __name__ == "__main__":
    unittest.main()
