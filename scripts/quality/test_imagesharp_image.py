"""Offline scanner regressions with synthetic Docker-save archives only."""
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/staging/check_imagesharp_image.py"
spec = importlib.util.spec_from_file_location("image_guard", SCRIPT)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
MARKER = "synthetic-private-payload-0123456789abcdef"


def packed(members):
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w") as archive:
        for name, value in members.items():
            member = tarfile.TarInfo(name)
            member.size = len(value)
            archive.addfile(member, io.BytesIO(value))
    return output.getvalue()


class ImageLicenseScannerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.license = self.root / "private.lic"
        self.license.write_text(MARKER)

    def image(self, files=None, version="4.1.2", config=None):
        deps = json.dumps({"libraries": {"SixLabors.ImageSharp/" + version: {}}}).encode()
        layer1 = packed(files or {"app/file.dll": b"synthetic-safe-dll"})
        # Later layer whiteouts must not hide disclosures in an earlier layer.
        layer2 = packed({"app/.wh.file.dll": b"", "app/HockeyPlanner.Backend.WebAPI.deps.json": deps})
        image = self.root / "image.tar"
        image.write_bytes(packed({"manifest.json": json.dumps([{"Config": "config.json", "Layers": ["1/layer.tar", "2/layer.tar"]}]).encode(),
            "config.json": json.dumps(config or {"config": {"Env": ["ASPNETCORE_URLS=http://+:8080"]}}).encode(),
            "1/layer.tar": layer1, "2/layer.tar": layer2}))
        return image

    def test_clean_image_and_exact_published_version_pass(self):
        self.assertEqual(guard.inspect(self.image(), self.license), 2)

    def test_deleted_or_encoded_private_material_in_any_layer_fails(self):
        for value in (MARKER.encode(), MARKER.encode("utf-16-le"), MARKER.encode("utf-16-be")):
            with self.assertRaises(ValueError):
                guard.inspect(self.image({"app/file.dll": value}), self.license)
        with self.assertRaises(ValueError):
            guard.clean(io.BytesIO(b"a" * (1024 * 1024 - 10) + MARKER.encode()), guard.patterns(MARKER.encode()))

    def test_secret_filename_or_runtime_environment_fails(self):
        for name in ("run/secrets/sixlabors_license", "src/sixlabors.lic"):
            with self.assertRaises(ValueError):
                guard.inspect(self.image({name: b"empty-marker"}), self.license)
        with self.assertRaises(ValueError):
            guard.inspect(self.image(config={"config": {"Env": ["SIXLABORS_LICENSE_KEY=synthetic"]}}), self.license)

    def test_wrong_published_version_and_empty_license_fail(self):
        with self.assertRaises(ValueError):
            guard.inspect(self.image(version="3.1.12"), self.license)
        self.license.write_text("")
        with self.assertRaises(ValueError):
            guard.inspect(self.image(), self.license)

    def test_config_payload_and_unavailable_archive_are_sanitized(self):
        for image in (self.image(config={"history": [{"created_by": MARKER}]}), self.root / "missing.tar"):
            result = subprocess.run([sys.executable, str(SCRIPT), "--image-tar", str(image), "--license-file", str(self.license)],
                capture_output=True, text=True)
            self.assertEqual(result.returncode, 1)
            self.assertEqual(result.stdout, "")
            self.assertEqual(result.stderr, "BLOCKED: private image/layer verification failed.\n")


if __name__ == "__main__":
    unittest.main()
