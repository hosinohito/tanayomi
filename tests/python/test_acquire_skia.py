"""Local archive identity regression tests; no network or native build."""
import gzip
import hashlib
import importlib.util
import io
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest.mock import patch


WORKSPACE = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("acquire_skia", WORKSPACE / "native/acquire-skia.py")
acquire = importlib.util.module_from_spec(spec)
spec.loader.exec_module(acquire)


def archive_bytes(content=b"fixed source\n", timestamp=1, extra=False, duplicate=False, mode=0o644):
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w") as archive:
        for name, data in [("source.c", content)] + ([("extra.c", b"extra")] if extra else []) + ([("source.c", content)] if duplicate else []):
            entry = tarfile.TarInfo(name)
            entry.size = len(data)
            entry.mtime = timestamp
            entry.uid = timestamp
            entry.uname = str(timestamp)
            entry.mode = mode
            archive.addfile(entry, io.BytesIO(data))
    return gzip.compress(raw.getvalue(), mtime=timestamp)


class ArchiveIdentityTests(unittest.TestCase):
    def setUp(self):
        temporary = WORKSPACE / ".local/tmp"
        temporary.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="skia-archive-test-", dir=temporary)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root / "source.tar.gz"
        self.original = archive_bytes()
        self.archive.write_bytes(self.original)
        self.pin = acquire.archive_content_sha256(self.archive)

    def matches(self, data):
        self.archive.write_bytes(data)
        with patch.dict(acquire.ARCHIVE_SHA256, {self.archive.name: hashlib.sha256(self.original).hexdigest()}), patch.dict(acquire.ARCHIVE_CONTENT_SHA256, {self.archive.name: self.pin}):
            return acquire.archive_matches(self.archive, self.archive.name, hashlib.sha256(data).hexdigest())

    def test_regenerated_metadata_is_same_source(self):
        regenerated = archive_bytes(timestamp=200)
        self.assertNotEqual(self.original, regenerated)
        self.assertTrue(self.matches(regenerated))

    def test_changed_file_is_rejected(self):
        self.assertFalse(self.matches(archive_bytes(content=b"changed source\n")))

    def test_added_file_is_rejected(self):
        self.assertFalse(self.matches(archive_bytes(extra=True)))

    def test_changed_mode_is_rejected(self):
        self.assertFalse(self.matches(archive_bytes(mode=0o755)))

    def test_duplicate_entry_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "duplicate archive entry"):
            self.matches(archive_bytes(duplicate=True))

    def test_expansion_budget_is_enforced(self):
        with patch.object(acquire, "LIMIT_EXPANDED", 1):
            with self.assertRaisesRegex(ValueError, "expanded budget"):
                acquire.archive_content_sha256(self.archive)

    def test_unpinned_archive_does_not_gain_fallback(self):
        self.assertFalse(acquire.archive_matches(self.archive, "unknown.tar.gz", "wrong", "expected"))

    def test_git_blob_check_still_rejects_different_source(self):
        class Expected:
            revision = "fixed"

            def blob(self, path):
                return "0" * 40

        with patch.object(acquire, "ROOT", self.root), patch.object(acquire, "state_path", self.root / "acquisition.json"), patch.object(acquire, "state", {"expandedBytes": 0, "sources": {}}):
            with self.assertRaisesRegex(ValueError, "source blob mismatch"):
                acquire.extract("fixture", self.archive, self.root / "source", False, Expected())
            self.assertFalse((self.root / "source").exists())
            self.assertFalse(list(self.root.glob(".extract-*")))


if __name__ == "__main__":
    unittest.main()
