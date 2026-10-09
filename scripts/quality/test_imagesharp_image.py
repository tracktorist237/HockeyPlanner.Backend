"""Offline scanner regressions with synthetic Docker-save archives only."""
import importlib.util
import hashlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/staging/check_imagesharp_image.py"
spec = importlib.util.spec_from_file_location("image_guard", SCRIPT)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
SYNTHETIC_DLL = b"MZ synthetic trusted test artifact; no executed code"
MARKER = "synthetic-private-payload-0123456789abcdef"


def packed(members):
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w") as archive:
        for name, value in members.items():
            member = tarfile.TarInfo(name)
            if isinstance(value, tuple):
                value, metadata = value
                for field, setting in metadata.items():
                    setattr(member, field, setting)
            member.size = len(value)
            archive.addfile(member, io.BytesIO(value))
    return output.getvalue()


class ImageLicenseScannerTests(unittest.TestCase):
    def setUp(self):
        self.assertEqual(guard.TRUSTED_DLL_SHA256, "80bbde81578be31ca7864f303200b0ef188be1b3048a657546bda04afdf394d3")
        trusted = patch.object(guard, "TRUSTED_DLL_SHA256", hashlib.sha256(SYNTHETIC_DLL).hexdigest())
        trusted.start()
        self.addCleanup(trusted.stop)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.license = self.root / "private.lic"
        self.license.write_text(MARKER)

    def image(self, files=None, version="4.1.2", config=None, later=None):
        key = "SixLabors.ImageSharp/" + version
        deps = json.dumps({"libraries": {key: {}}, "targets": {"net10": {key:
            {"runtime": {"lib/net8.0/SixLabors.ImageSharp.dll": {}}}}}}).encode()
        base = {"app/file.dll": b"synthetic-safe", guard.DLL: SYNTHETIC_DLL, guard.DEPS: deps}
        if files is not None:
            base.update(files)
        layer1 = packed(base)
        layer2 = packed(later if later is not None else {"app/.wh.file.dll": b""})
        image = self.root / "image.tar"
        config = config or {"config": {"Env": ["ASPNETCORE_URLS=http://+:8080"], "WorkingDir": "/app"}}
        config["rootfs"] = {"type": "layers", "diff_ids": ["sha256:" + hashlib.sha256(layer).hexdigest()
            for layer in (layer1, layer2)]}
        config_bytes = json.dumps(config).encode()
        config_name = hashlib.sha256(config_bytes).hexdigest() + ".json"
        image.write_bytes(packed({"manifest.json": json.dumps([{"Config": config_name, "Layers": ["1/layer.tar", "2/layer.tar"]}]).encode(),
            config_name: config_bytes, "1/layer.tar": layer1, "2/layer.tar": layer2}))
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

    def test_missing_invalid_or_incorrect_dll_never_passes(self):
        for later in ({"app/.wh.SixLabors.ImageSharp.dll": b""}, {guard.DLL: b"MZ wrong DLL"},
                      {guard.DLL: b"invalid"}):
            with self.assertRaises(ValueError):
                guard.inspect(self.image(later=later), self.license)

    def test_whiteouts_opaque_deletion_and_overwrite_remove_final_evidence(self):
        for later in ({"app/.wh.HockeyPlanner.Backend.WebAPI.deps.json": b""},
                      {"app/.wh..wh..opq": b""}, {".wh.app": b""},
                      {guard.DEPS: b'{"libraries": {}, "targets": {}}'}, {"app": b"replaced directory"}):
            with self.assertRaises(ValueError):
                guard.inspect(self.image(later=later), self.license)
        # Opaque marker after new files must retain the current layer's new files.
        deps = json.dumps({"libraries": {"SixLabors.ImageSharp/4.1.2": {}}, "targets": {"net10": {
            "SixLabors.ImageSharp/4.1.2": {"runtime": {"lib/net8.0/SixLabors.ImageSharp.dll": {}}}}}}).encode()
        self.assertEqual(guard.inspect(self.image(later={guard.DEPS: deps, guard.DLL: SYNTHETIC_DLL,
            "app/.wh..wh..opq": b""}), self.license), 2)

    def test_tar_metadata_disclosure_and_corrupt_archive_block(self):
        needles = guard.patterns(MARKER.encode())
        for field in ("name", "linkname", "uname", "gname"):
            member = tarfile.TarInfo("synthetic")
            setattr(member, field, MARKER)
            with self.assertRaises(ValueError):
                guard.metadata_clean(member, needles)
        member = tarfile.TarInfo("synthetic")
        member.uname = MARKER[:32]
        with self.assertRaises(ValueError):
            guard.metadata_clean(member, needles)
        member = tarfile.TarInfo("synthetic")
        member.pax_headers = {"comment": MARKER}
        with self.assertRaises(ValueError):
            guard.metadata_clean(member, needles)
        image = self.root / "corrupt.tar"
        image.write_bytes(b"not a Docker archive")
        with self.assertRaises(tarfile.TarError):
            guard.inspect(image, self.license)

    def test_saved_image_metadata_and_content_identity_corruption_fail(self):
        for metadata in ({"uname": MARKER}, {"gname": MARKER}, {"pax_headers": {"comment": MARKER}},
                         {"type": tarfile.SYMTYPE, "linkname": MARKER}):
            with self.assertRaises(ValueError):
                guard.inspect(self.image({"app/file.dll": (b"", metadata)}), self.license)
        with self.assertRaises(ValueError):
            guard.inspect(self.image({"app/" + MARKER: b""}), self.license)
        image = self.image()
        with tarfile.open(image) as archive:
            members = {member.name: archive.extractfile(member).read() for member in archive if member.isfile()}
        members["1/layer.tar"] += b"corrupt-diff-id"
        image.write_bytes(packed(members))
        with self.assertRaises(ValueError):
            guard.inspect(image, self.license)

    def test_config_payload_and_unavailable_archive_are_sanitized(self):
        for image in (self.image(config={"history": [{"created_by": MARKER}]}), self.root / "missing.tar"):
            result = subprocess.run([sys.executable, str(SCRIPT), "--image-tar", str(image), "--license-file", str(self.license),
                "--expected-image-id", "sha256:" + "b" * 64],
                capture_output=True, text=True)
            self.assertEqual(result.returncode, 1)
            self.assertEqual(result.stdout, "")
            self.assertEqual(result.stderr, "BLOCKED: private image/layer verification failed.\n")


if __name__ == "__main__":
    unittest.main()
