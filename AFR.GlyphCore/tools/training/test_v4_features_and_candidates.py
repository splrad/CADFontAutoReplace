from __future__ import annotations

import json
import re
import sys
import tempfile
import unittest
from pathlib import Path

TOOL_ROOT = Path(__file__).resolve().parents[1]
TRAINING_ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(TOOL_ROOT))
sys.path.insert(0, str(TRAINING_ROOT))

from afr_glyphcore import FEATURE_COUNT, FEATURE_SCHEMA_VERSION  # noqa: E402
from afr_glyphcore.candidates import Candidate, build_candidates  # noqa: E402
from afr_glyphcore.features import FEATURE_NAMES, extract_features  # noqa: E402
from build_features import build_rows  # noqa: E402


class V4FeatureAndCandidateTests(unittest.TestCase):
    def test_hook_raw_candidate_is_prioritized_and_featured(self) -> None:
        context = {
            "currentText": "潰党韌800遵X500詢,階善褽菁",
            "layer": "消防_TEXT",
            "textStyleFileName": "txt.shx",
            "textStyleBigFontFileName": "tssdchn.shx",
            "hasNativeDecodeEvidence": True,
            "nativeDecodeFamilyMismatch": True,
            "nativeDecodeEvidenceScope": "object",
            "nativeDecodeSourceCodePageFamily": "gbk",
            "nativeDecodeAppliedCodePageFamily": "big5",
            "nativeDecodeHookHitType": "dbtext-raw-stream",
            "nativeDecodeObjectCorrelation": 1.0,
            "hasHookRawDecodeEvidence": True,
            "hookRawPayloadLength": 18,
            "hookPreferredDecodedText": "检修洞800宽X500高,顶到梁底",
            "hookRawCandidateSource": "hook-raw-stream+big5-carrier-to-gbk",
            "hookRawRoundTrip": True,
            "hookRawConfidence": 0.97,
        }

        candidates = build_candidates(context["currentText"], context)
        self.assertEqual("hook-raw-stream+big5-carrier-to-gbk", candidates[0].source)
        self.assertEqual("检修洞800宽X500高,顶到梁底", candidates[0].text)

        features = extract_features(context, candidates[0])
        self.assertEqual(FEATURE_COUNT, len(features))
        self.assertEqual(101, FEATURE_COUNT)
        self.assertEqual("dbtext-ai-features-v6", FEATURE_SCHEMA_VERSION)
        self.assertEqual(FEATURE_COUNT, len(FEATURE_NAMES))
        self.assertEqual(1.0, features[26])
        self.assertEqual(0.0, features[27])
        self.assertEqual(1.0, features[92])
        self.assertEqual(1.0, features[93])
        self.assertEqual(1.0, features[94])
        self.assertGreater(features[95], 0.9)
        self.assertGreater(features[79], features[78])

    def test_symbols_ascii_and_ripple_features_survive_feature_build(self) -> None:
        record = {
            "schema": "dbtext-ai-reviewed-label-v1",
            "featureSchema": FEATURE_SCHEMA_VERSION,
            "groupId": "ripple-1",
            "currentText": "DN100 潰党",
            "labelAction": "repair",
            "labelText": "DN100 给水管",
            "context": {
                "currentText": "DN100 潰党",
                "layer": "WATER_PIPE_TEXT",
                "ownerBlockName": "*Model_Space",
                "textStyleName": "HZTXT",
                "textStyleFileName": "txt.shx",
                "textStyleBigFontFileName": "tssdchn.shx",
                "nativeDecodeEvidence": {
                    "hasEvidence": True,
                    "familyMismatch": True,
                    "scope": "ripple",
                    "sourceCodePageFamily": "gbk",
                    "appliedCodePageFamily": "big5",
                    "hookHitType": "dbtext-raw-stream",
                    "clusterCorrelation": 0.6,
                    "rippleContextText": "DN100 给水管",
                    "rippleSeedCount": 3,
                    "rippleSeedQuality": 0.82,
                    "rippleDistanceRatio": 0.25,
                },
            },
            "candidates": [
                {
                    "text": "DN100 给水管",
                    "source": "big5-carrier-to-gbk",
                    "reason": "roundtrip-ok",
                    "isRoundTrip": True,
                    "targetScore": 1.0,
                },
                {
                    "text": "DN100 潰党",
                    "source": "current-noop",
                    "reason": "current text",
                    "isRoundTrip": True,
                    "targetScore": 0.0,
                },
            ],
        }
        with tempfile.TemporaryDirectory() as temp_dir:
            input_path = Path(temp_dir) / "reviewed.jsonl"
            input_path.write_text(json.dumps(record, ensure_ascii=False) + "\n", encoding="utf-8")
            rows = list(build_rows(input_path))

        self.assertEqual(2, len(rows))
        feature_columns = [key for key in rows[0] if re.match(r"^f\d+_", key)]
        self.assertEqual(FEATURE_COUNT, len(feature_columns))
        self.assertEqual(3, rows[0]["ripple_seed_count"])
        self.assertGreater(float(rows[0]["ripple_seed_quality"]), 0.8)
        self.assertGreater(float(rows[0]["f86_candidate_preserves_ascii_tokens"]), 0.0)
        self.assertGreater(float(rows[0]["f87_candidate_preserves_engineering_symbols"]), 0.0)

    def test_source_direction_features_distinguish_codepage_order(self) -> None:
        context = {"currentText": "騵", "textStyleBigFontFileName": "GBCBIG"}
        big5_to_gbk = extract_features(context, Candidate("耐", "big5-carrier-to-gbk", "fixture", True))
        gbk_to_big5 = extract_features(context, Candidate("矄", "gbk-carrier-to-big5", "fixture", True))

        self.assertEqual(1.0, big5_to_gbk[26])
        self.assertEqual(0.0, big5_to_gbk[27])
        self.assertEqual(0.0, gbk_to_big5[26])
        self.assertEqual(1.0, gbk_to_big5[27])

    def test_native_evidence_direction_prefers_matching_conversion(self) -> None:
        context = {
            "currentText": "GB50016-2014(2018\u5533)",
            "hasNativeDecodeEvidence": True,
            "nativeDecodeFamilyMismatch": True,
            "nativeDecodeEvidenceScope": "object",
            "nativeDecodeSourceCodePageFamily": "big5",
            "nativeDecodeAppliedCodePageFamily": "gbk",
        }

        candidates = build_candidates(context["currentText"], context)
        repair_candidates = [candidate for candidate in candidates if not candidate.is_noop]
        self.assertTrue(repair_candidates)
        self.assertIn("big5-carrier-to-gbk", repair_candidates[0].source)

        aligned = extract_features(context, Candidate("GB50016-2014(2018\u7248)", "big5-carrier-to-gbk", "fixture", True))
        reversed_direction = extract_features(context, Candidate("GB50016-2014(2018\u7248)", "gbk-carrier-to-big5", "fixture", True))
        self.assertEqual(1.0, aligned[77])
        self.assertEqual(0.0, reversed_direction[77])

    def test_feature_build_prefers_visible_candidate_over_manual_duplicate(self) -> None:
        record = {
            "schema": "dbtext-ai-reviewed-label-v1",
            "featureSchema": FEATURE_SCHEMA_VERSION,
            "groupId": "visible-label-1",
            "currentText": "机   隅 ",
            "labelAction": "repair",
            "labelText": "审   定",
            "context": {"currentText": "机   隅 "},
            "candidates": [
                {
                    "text": "审   定 ",
                    "source": "big5-carrier-to-gbk",
                    "reason": "roundtrip-ok",
                    "isRoundTrip": True,
                    "targetScore": 0.0,
                },
                {
                    "text": "审   定",
                    "source": "manual-review",
                    "reason": "human-reviewed-correction",
                    "isRoundTrip": True,
                    "targetScore": 1.0,
                },
            ],
        }
        with tempfile.TemporaryDirectory() as temp_dir:
            input_path = Path(temp_dir) / "reviewed.jsonl"
            input_path.write_text(json.dumps(record, ensure_ascii=False) + "\n", encoding="utf-8")
            rows = list(build_rows(input_path))

        self.assertEqual(1.0, rows[0]["target_score"])
        self.assertEqual(1, rows[0]["is_positive"])
        self.assertEqual(0.0, rows[1]["target_score"])
        self.assertEqual(0, rows[1]["is_positive"])

    def test_private_use_prefix_cleanup_requires_native_mismatch(self) -> None:
        current_text = "\ue4de\ue4de\u5bb9\u79ef\u4fee\u6b63\u7cfb\u6570\u3002"
        expected_text = "  \u5bb9\u79ef\u4fee\u6b63\u7cfb\u6570\u3002"
        context = {
            "currentText": current_text,
            "hasNativeDecodeEvidence": True,
            "nativeDecodeFamilyMismatch": True,
            "nativeDecodeEvidenceScope": "object",
        }

        candidates = build_candidates(current_text, context)
        cleanup = [candidate for candidate in candidates if candidate.text == expected_text]

        self.assertEqual(1, len(cleanup))
        self.assertIn("private-use-prefix-space-fill", cleanup[0].source)
        self.assertTrue(cleanup[0].is_roundtrip)
        self.assertNotIn(expected_text, [candidate.text for candidate in build_candidates(current_text, {})])

    def test_private_use_prefix_cleanup_does_not_mask_preferred_decode(self) -> None:
        current_text = "\uf708\u6781\u93e2\u9cf6"
        context = {
            "currentText": current_text,
            "hasNativeDecodeEvidence": True,
            "nativeDecodeFamilyMismatch": True,
            "nativeDecodeEvidenceScope": "object",
            "nativeDecodeSourceCodePageFamily": "big5",
            "nativeDecodeAppliedCodePageFamily": "gbk",
        }

        candidates = build_candidates(current_text, context)
        candidate_texts = [candidate.text for candidate in candidates]

        self.assertIn("\u6c14\u4f53\u706d\u706b", candidate_texts)
        self.assertNotIn(" \u6781\u93e2\u9cf6", candidate_texts)
        decoded = [candidate for candidate in candidates if candidate.text == "\u6c14\u4f53\u706d\u706b"]
        self.assertEqual(1, len(decoded))
        self.assertIn("big5-carrier-to-gbk", decoded[0].source)
        self.assertTrue(decoded[0].is_roundtrip)

    def test_private_use_punctuation_carryover_requires_native_mismatch(self) -> None:
        current_text = "\u56e5\u00b7\u8de4"
        decoded_text = "\u65bd\ue4d6\u7ed9"
        expected_text = "\u65bd\u00b7\u7ed9"
        context = {
            "currentText": current_text,
            "hasNativeDecodeEvidence": True,
            "nativeDecodeFamilyMismatch": True,
            "nativeDecodeEvidenceScope": "object",
            "hasHookRawDecodeEvidence": True,
            "hookPreferredDecodedText": decoded_text,
            "hookRawRoundTrip": True,
            "hookRawCandidateSource": "hook-raw-stream",
        }

        candidates = build_candidates(current_text, context)
        carryover = [
            candidate
            for candidate in candidates
            if candidate.text == expected_text and "private-use-punctuation-carryover" in candidate.source
        ]

        self.assertEqual(1, len(carryover))
        self.assertTrue(carryover[0].is_roundtrip)
        self.assertNotIn(
            "private-use-punctuation-carryover",
            "+".join(candidate.source for candidate in build_candidates(current_text, {"hookPreferredDecodedText": decoded_text})),
        )

    def test_private_use_prefixed_noop_is_not_visible_label_match(self) -> None:
        current_text = "\ue4de\ue4de\u5bb9\u79ef\u4fee\u6b63\u7cfb\u6570\u3002"
        label_text = "  \u5bb9\u79ef\u4fee\u6b63\u7cfb\u6570\u3002"
        record = {
            "schema": "dbtext-ai-reviewed-label-v1",
            "featureSchema": FEATURE_SCHEMA_VERSION,
            "groupId": "private-use-prefix-1",
            "currentText": current_text,
            "labelAction": "repair",
            "labelText": label_text,
            "context": {"currentText": current_text},
            "candidates": [
                {
                    "text": current_text,
                    "source": "current-noop",
                    "reason": "current text",
                    "isRoundTrip": True,
                    "targetScore": 1.0,
                },
                {
                    "text": label_text,
                    "source": "private-use-prefix-space-fill",
                    "reason": "native-evidence-leading-private-use-placeholder",
                    "isRoundTrip": True,
                    "targetScore": 0.0,
                },
            ],
        }
        with tempfile.TemporaryDirectory() as temp_dir:
            input_path = Path(temp_dir) / "reviewed.jsonl"
            input_path.write_text(json.dumps(record, ensure_ascii=False) + "\n", encoding="utf-8")
            rows = list(build_rows(input_path))

        self.assertEqual(0.0, rows[0]["target_score"])
        self.assertEqual(0, rows[0]["is_positive"])
        self.assertEqual(1.0, rows[1]["target_score"])
        self.assertEqual(1, rows[1]["is_positive"])


if __name__ == "__main__":
    unittest.main()
