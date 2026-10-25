
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import unittest
import tempfile
_fixture_root = Path(tempfile.mkdtemp(prefix="auto-solidworks-final-review-"))

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("final_review", ROOT / "plugins/auto-solidworks/scripts/summarize-final-review.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class FinalReviewTests(unittest.TestCase):
    def setUp(self):
        folder = _fixture_root / self.id().split(".")[-1]
        folder.mkdir(parents=True, exist_ok=False)
        self.folder = folder
        native = folder / "model.SLDPRT"
        native.write_bytes(b"test native bytes")
        digest = module.sha256(native)
        report_path = folder / "review.json"
        preview = folder / "model.isometric.bmp"
        preview.write_bytes(b"preview fixture; no image rendering in these identity tests")
        verification = {"passed": True, "checks": [{"id": "hole", "passed": True, "status": "passed"}]}
        self.saved = {
            "success": True, "input_path": str(native), "model_sha256": digest,
            "model_reopened": True, "rebuild_succeeded": True, "capture_complete": True,
            "geometry_status": "passed", "parametric_status": "review_required",
            "body_validity": {"complete": True, "passed": True, "fault_count": 0},
            "verification": verification, "features": [
                {"name": "under_defined", "sketch_quality": {"status": "review_required"}}]
        }
        self.report = {"saved_model_readback": self.saved}
        self.payload = {
            "success": True, "status": "completed", "native_path": str(native),
            "geometry_status": "passed", "verification": copy.deepcopy(verification),
            "evidence": [
                {"stage": "saved_model_readback", "passed": True, "data": {"sha256": digest}},
                {"stage": "review_report", "passed": True, "data": {"path": str(report_path)}},
                {"stage": "preview", "passed": True, "data": {"view": "isometric", "path": str(preview)}}]
        }
        self.receipt = folder / "receipt.json"

    def evaluate(self, views=None, reason="isometric covers this simple source"):
        (self.folder / "review.json").write_text(json.dumps(self.report), encoding="utf-8")
        self.receipt.write_text(json.dumps({"payload": self.payload}), encoding="utf-8")
        return module.summarize(str(self.receipt), module.sha256(self.receipt),
                                views or ["isometric"], reason)

    def test_success_preserves_parametric_and_visual_limits(self):
        result = self.evaluate()
        self.assertEqual("review_required", result["parametric_status"])
        self.assertEqual(["under_defined"], result["sketches_requiring_review"])
        self.assertEqual("pending_actual_image_review", result["visual_status"])
        self.assertFalse(result["full_drawing_equivalence"])

    def test_background_execution_envelope_supported(self):
        execution = copy.deepcopy(self.payload)
        self.payload = {"execution": execution, "pending": False, "outcome_unknown": False}
        result = self.evaluate()
        self.assertTrue(result["saved_reopened_rebuilt"])
        self.assertEqual("review_required", result["parametric_status"])

    def test_pending_background_envelope_rejected(self):
        execution = copy.deepcopy(self.payload)
        self.payload = {"execution": execution, "pending": True, "outcome_unknown": False}
        with self.assertRaisesRegex(ValueError, "Pending or unknown"):
            self.evaluate()

    def test_unknown_background_envelope_rejected(self):
        execution = copy.deepcopy(self.payload)
        self.payload = {"execution": execution, "pending": False, "outcome_unknown": True}
        with self.assertRaisesRegex(ValueError, "Pending or unknown"):
            self.evaluate()

    def test_receipt_hash_mismatch(self):
        self.evaluate()
        with self.assertRaisesRegex(ValueError, "Receipt hash"):
            module.summarize(str(self.receipt), "0" * 64, ["isometric"], "coverage")

    def test_model_changed(self):
        Path(self.payload["native_path"]).write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "Model changed"):
            self.evaluate()

    def test_pending_build(self):
        self.payload["pending"] = True
        with self.assertRaisesRegex(ValueError, "Pending"):
            self.evaluate()

    def test_unknown_build(self):
        self.payload["outcome_unknown"] = True
        with self.assertRaisesRegex(ValueError, "unknown"):
            self.evaluate()

    def test_failed_declared_check(self):
        self.payload["verification"]["checks"][0]["passed"] = False
        with self.assertRaisesRegex(ValueError, "verification failed"):
            self.evaluate()

    def test_failed_saved_check(self):
        self.saved["verification"]["checks"][0]["passed"] = False
        with self.assertRaisesRegex(ValueError, "verification failed"):
            self.evaluate()

    def test_missing_readback(self):
        self.payload["evidence"].pop(0)
        with self.assertRaisesRegex(ValueError, "readback"):
            self.evaluate()

    def test_incomplete_body_validity(self):
        self.saved["body_validity"]["complete"] = False
        with self.assertRaisesRegex(ValueError, "body validity"):
            self.evaluate()

    def test_missing_preview(self):
        with self.assertRaisesRegex(ValueError, "preview is not"):
            self.evaluate(["isometric", "left"])

    def test_missing_scope_reason(self):
        with self.assertRaisesRegex(ValueError, "selected views"):
            self.evaluate(reason="")

    def test_source_changed(self):
        self.evaluate()
        source = self.folder / "source.png"
        source.write_bytes(b"changed source")
        with self.assertRaisesRegex(ValueError, "Source changed"):
            module.summarize(str(self.receipt), module.sha256(self.receipt), ["isometric"],
                             "coverage", str(source), "0" * 64)

    def test_saved_inspection_wrong_model(self):
        self.saved["input_path"] = str(self.folder / "another.SLDPRT")
        with self.assertRaisesRegex(ValueError, "different model"):
            self.evaluate()

    def test_unknown_check_status(self):
        self.saved["verification"]["checks"][0]["passed"] = None
        with self.assertRaisesRegex(ValueError, "verification failed"):
            self.evaluate()


if __name__ == "__main__":
    unittest.main(verbosity=2)


