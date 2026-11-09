"""只在临时夹具内验证筛选安全性，不接触真实外盘。"""
import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

from audit_external_assets import audit, digest


class AuditSafetyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="town-asset-audit-")
        self.root = Path(self.temp.name) / "sources"
        self.root.mkdir()
        for name, data in {
            "a/link.url": b"same link", "b/link.url": b"same link",
            "c/unique.url": b"different", "a/source.unitypackage": b"same link",
            "b/source.unitypackage": b"same link", "b/link.url.downloading": b"same link",
            "a/插件及使用方法图示.zip": b"same helper", "b/插件及使用方法图示.zip": b"same helper",
            "a/unknown.zip": b"same helper", "b/unknown.zip": b"same helper",
        }.items():
            path = self.root / name
            path.parent.mkdir(exist_ok=True)
            path.write_bytes(data)

    def tearDown(self):
        self.temp.cleanup()

    def run_audit(self, name, quarantine=False):
        output = Path(self.temp.name) / name
        with contextlib.redirect_stdout(io.StringIO()):
            audit(self.root, output, quarantine)
        return json.loads(output.read_text())

    def test_default_only_reports_and_refuses_overwrite(self):
        before = {str(p.relative_to(self.root)): digest(p) for p in self.root.rglob("*") if p.is_file()}
        report = self.run_audit("read.json")
        self.assertEqual(2, len(report["duplicate_helpers"]))
        self.assertEqual([], report["quarantined"])
        self.assertEqual(before, {str(p.relative_to(self.root)): digest(p) for p in self.root.rglob("*") if p.is_file()})
        with self.assertRaises(FileExistsError):
            self.run_audit("read.json", True)

    def test_quarantine_only_exact_duplicate_helpers_and_does_not_repeat(self):
        report = self.run_audit("move.json", True)
        self.assertEqual(2, len(report["quarantined"]))
        for row in report["quarantined"]:
            self.assertFalse((self.root / row["original"]).exists())
            self.assertEqual(row["sha256"], digest(self.root / row["backup"]))
        for group in report["duplicate_helpers"]:
            self.assertEqual(group["sha256"], digest(self.root / group["canonical"]))
        for name in ["a/source.unitypackage", "b/source.unitypackage", "b/link.url.downloading", "a/unknown.zip", "b/unknown.zip"]:
            self.assertTrue((self.root / name).is_file(), name)
        again = self.run_audit("again.json", True)
        self.assertEqual([], again["duplicate_helpers"])
        self.assertEqual([], again["quarantined"])


if __name__ == "__main__":
    unittest.main()
