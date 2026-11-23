"""Standalone Python regression for the optional CPU converter boundary.

Run using the audited development Python containing torch; no model is needed.
Fixtures are owned, unique, and removed on every result.
"""
import hashlib
import importlib.util
import math
import os
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("openvoice_worker", ROOT / "src/Tanayomi.Desktop/Neural/openvoice_worker.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class WorkerBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="neural-boundary-", dir=ROOT / ".local/tmp")
        self.root = Path(self.temporary.name).resolve()

    def tearDown(self):
        self.temporary.cleanup()

    def test_rejects_escape_and_alternate_stream(self):
        for path in ("../outside.wav", "clip.wav:secret", str(ROOT / "outside.wav")):
            with self.subTest(path=path), self.assertRaises(worker.WorkerError):
                worker.contained(self.root, path, existing=False)

    def test_model_hash_rejects_changed_content(self):
        path = self.root / "model"
        path.write_bytes(b"accepted")
        digest = hashlib.sha256(b"accepted").hexdigest()
        worker.verify_file(path, digest, 20)
        path.write_bytes(b"rejected")
        with self.assertRaises(worker.WorkerError):
            worker.verify_file(path, digest, 20)

    def test_missing_input_and_empty_path(self):
        for path in ("", "missing.wav", None):
            with self.subTest(path=path), self.assertRaises(worker.WorkerError):
                worker.contained(self.root, path)

    def test_resample_preserves_independently_counted_tone(self):
        import torch
        source = torch.tensor([0.5 * math.sin(2 * math.pi * 220 * i / 48000) for i in range(48000)])
        result = worker.resample(source, 48000, torch)
        self.assertEqual(len(result), 22050)
        # Independent time-domain crossing count, not an FFT-based assertion.
        values = result.tolist()
        crossings = sum(a < 0 <= b for a, b in zip(values, values[1:]))
        self.assertLessEqual(abs(crossings - 220), 1)
        self.assertAlmostEqual(float(result.abs().max()), 0.5, places=3)


if __name__ == "__main__":
    try:
        suite = unittest.defaultTestLoader.loadTestsFromTestCase(WorkerBoundaryTests)
        result = unittest.TextTestRunner(verbosity=1).run(suite)
        sys.exit(0 if result.wasSuccessful() else 1)
    except Exception as error:
        print("NEURAL_TEST_FAILED", type(error).__name__)
        sys.exit(1)
