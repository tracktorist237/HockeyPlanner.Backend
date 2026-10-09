"""Execute the deploy transaction against synthetic Git/Docker responses only."""
import copy
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tarfile
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/staging"))
import deploy_imagesharp as deploy
import check_imagesharp_build as guard

SHA = "a" * 40
IID = "sha256:" + "b" * 64


def archive():
    result = io.BytesIO()
    with tarfile.open(fileobj=result, mode="w") as output:
        item = tarfile.TarInfo(guard.DOCKERFILE)
        data = b"synthetic reviewed source"
        item.size = len(data)
        output.addfile(item, io.BytesIO(data))
    return result.getvalue()


class DeployTransactionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "staging"
        self.root.mkdir()
        (self.root / "backend-src").mkdir()
        (self.root / "docker-compose.yml").write_text("synthetic base")
        (self.root / "compose.imagesharp-license.yml").write_text("synthetic override")
        self.lock = Path(self.temp.name) / "private/deploy.lock"
        self.lock.parent.mkdir()
        self.lock.write_text("x")
        self.license = Path(self.temp.name) / "license/sixlabors.lic"
        self.license.parent.mkdir()
        self.license.write_text("synthetic-private-value")
        self.config = {"name": "hockeyplanner-staging", "services": {"backend": {"container_name": "existing-backend",
            "environment": {"JWT_SECRET": "synthetic-$$private"}, "build": {
                "context": str(self.root / "backend-src"), "dockerfile": guard.DOCKERFILE,
                "secrets": [{"source": "sixlabors_license"}]}}},
            "secrets": {"sixlabors_license": {"file": str(self.license)}}}
        self.calls, self.built, self.scenario = [], False, "success"

    def command(self, args, cwd=None):
        self.calls.append(args)
        if args[:3] == ["git", "rev-parse", "HEAD"]:
            return (("c" * 40 if self.built and self.scenario == "checkout" else SHA) + "\n").encode()
        if args[:2] == ["git", "status"]:
            return b""
        if args[:2] == ["git", "archive"]:
            return archive()
        if args[:3] == ["docker", "buildx", "build"]:
            if self.scenario == "build":
                raise subprocess.CalledProcessError(1, ["synthetic build"])
            self.built = True
            Path(args[args.index("--iidfile") + 1]).write_text(IID)
            if self.scenario == "compose":
                (self.root / "compose.imagesharp-license.yml").write_text("changed after preflight")
            if self.scenario == "source":
                source = Path(args[-1]) / guard.DOCKERFILE
                os.chmod(source, 0o600)
                source.write_text("changed build source")
            return b""
        if args[:3] == ["docker", "image", "inspect"]:
            if self.scenario == "image-missing" and args[-1] == IID:
                raise subprocess.CalledProcessError(1, ["synthetic image inspect"])
            value = "sha256:" + "d" * 64 if self.scenario == "tag" and args[-1] != IID else IID
            return json.dumps([{"Id": value}]).encode()
        if args[:3] == ["docker", "ps", "-aq"]:
            return b"existing-container"
        if args[:3] == ["docker", "container", "inspect"]:
            wrong = self.scenario == "container-project" or (self.built and self.scenario == "container-change")
            return json.dumps([{"Id": "changed" if wrong else "existing-container", "Image": "old-immutable-image",
                "Name": "/existing-backend", "Config": {"Labels": {
                    "com.docker.compose.project": "other-project" if self.scenario == "container-project" else "hockeyplanner-staging",
                    "com.docker.compose.service": "backend", "com.docker.compose.container-number": "1",
                    "com.docker.compose.project.working_dir": str(self.root)}},
                "HostConfig": {"synthetic": True}, "State": {"Running": True, "StartedAt": "unchanged"},
                "RestartCount": 0, "NetworkSettings": {"Networks": {}}}]).encode()
        if args[:2] == ["docker", "compose"]:
            file = Path(args[args.index("-f") + 1])
            if args[-3:] == ["config", "--format", "json"]:
                if file.name == "compose.json":
                    config = json.loads(file.read_bytes())
                    return json.dumps(config).encode()
                return json.dumps(self.config).encode()
            if "up" in args:
                self.assertEqual(json.loads(file.read_text())["services"]["backend"]["image"], IID)
                self.assertIn("--no-build", args)
                self.assertEqual(args[args.index("--pull") + 1], "never")
                self.assertEqual(file.stat().st_mode & 0o777, 0o400 if os.name != "nt" else 0o444)
            return b""
        raise AssertionError("Unexpected stubbed command")

    def validate(self, config, root, license_file, source_context=None):
        original = Path.stat
        def metadata(path, *a, **kw):
            result = original(path, *a, **kw)
            if path in (license_file, license_file.parent):
                return SimpleNamespace(st_mode=(stat.S_IFREG | 0o600) if path == license_file else (stat.S_IFDIR | 0o700),
                                       st_uid=1234, st_size=result.st_size)
            return result
        with patch.object(Path, "stat", metadata), patch.object(guard.os, "geteuid", return_value=1234, create=True):
            guard.validate(config, root, license_file, source_context)

    def held(self, fd, path):
        if self.scenario in ("lock-unavailable", "concurrent") or not path.is_file():
            raise OSError("synthetic lock failure")

    def execute(self, scenario):
        self.scenario = scenario
        if scenario == "lock-missing":
            self.lock.unlink()
        if scenario == "license":
            self.license.unlink()
        with patch.object(deploy, "command", self.command), patch.object(deploy, "held_lock", self.held), \
                patch.object(deploy, "protected"), patch.object(deploy, "validate", self.validate):
            deploy.deploy(SHA, 9, self.root, self.license, self.lock)

    def test_success_uses_private_cleaned_snapshot_and_immutable_image(self):
        self.execute("success")
        up = next(call for call in self.calls if "up" in call)
        self.assertEqual(up[up.index("--project-name") + 1], "hockeyplanner-staging")
        snapshot = Path(up[up.index("-f") + 1])
        self.assertFalse(snapshot.exists())
        self.assertEqual(list(self.lock.parent.iterdir()), [self.lock])
        build = next(call for call in self.calls if call[:3] == ["docker", "buildx", "build"])
        self.assertIn("--no-cache", build)
        self.assertIn("--iidfile", build)
        self.assertNotIn("synthetic-private-value", " ".join(build))

    def test_build_only_never_invokes_up(self):
        with patch.object(deploy, "command", self.command), patch.object(deploy, "held_lock", self.held), \
                patch.object(deploy, "protected"), patch.object(deploy, "validate", self.validate):
            self.assertEqual(deploy.deploy(SHA, 9, self.root, self.license, self.lock, build_only=True), IID)
        self.assertFalse(any("up" in call for call in self.calls))
        self.assertEqual(list(self.lock.parent.iterdir()), [self.lock])

    def test_all_dangerous_mutations_abort_before_up(self):
        for scenario in ("compose", "checkout", "source", "tag", "image-missing", "concurrent", "lock-missing",
                         "lock-unavailable", "license", "build", "container-project", "container-change"):
            with self.subTest(scenario=scenario):
                self.calls, self.built = [], False
                self.lock.write_text("x")
                self.license.write_text("synthetic-private-value")
                with self.assertRaises((ValueError, OSError, subprocess.SubprocessError)):
                    self.execute(scenario)
                self.assertFalse(any("up" in call for call in self.calls))
                self.assertFalse(any("stop" in call or "rm" in call for call in self.calls))
                self.assertEqual(list(self.lock.parent.iterdir()), [self.lock] if self.lock.exists() else [])

    def test_real_host_lock_excludes_a_second_open_descriptor(self):
        # Linux uses real flock. Windows models the same primitive with native
        # byte-range locking so this deterministic regression has no skipped path.
        if os.name == "nt":
            import msvcrt
            def flock(fd, flags):
                os.lseek(fd, 0, 0)
                msvcrt.locking(fd, msvcrt.LK_NBLCK, 1)
            provider = SimpleNamespace(flock=flock, LOCK_EX=1, LOCK_NB=2)
            context = patch.dict(sys.modules, {"fcntl": provider})
        else:
            context = patch.dict(sys.modules, {})
        first = os.open(self.lock, os.O_RDWR)
        second = os.open(self.lock, os.O_RDWR)
        try:
            with context, patch.object(deploy, "protected"):
                deploy.held_lock(first, self.lock)
                with self.assertRaises(OSError):
                    deploy.held_lock(second, self.lock)
        finally:
            os.close(second)
            os.close(first)


if __name__ == "__main__":
    unittest.main()
