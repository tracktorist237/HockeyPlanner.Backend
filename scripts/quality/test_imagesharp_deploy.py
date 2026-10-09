"""Execute the deploy transaction against synthetic Git/Docker responses only."""
import copy
import hashlib
import io
import contextlib
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
import check_imagesharp_image as image
from test_imagesharp_image import packed, SYNTHETIC_DLL

SHA = "a" * 40
LAYER = packed({image.DLL: SYNTHETIC_DLL, image.DEPS: json.dumps({"libraries": {"SixLabors.ImageSharp/4.1.2": {}},
    "targets": {"net10": {"SixLabors.ImageSharp/4.1.2": {"runtime": {"lib/net8.0/SixLabors.ImageSharp.dll": {}}}}}}).encode()})
IMAGE_CONFIG = json.dumps({"config": {"WorkingDir": "/app", "Env": []},
    "rootfs": {"type": "layers", "diff_ids": ["sha256:" + hashlib.sha256(LAYER).hexdigest()]}}).encode()
IID = "sha256:" + hashlib.sha256(IMAGE_CONFIG).hexdigest()
IMAGE_TAR = packed({IID[7:] + ".json": IMAGE_CONFIG, "1/layer.tar": LAYER,
    "manifest.json": json.dumps([{"Config": IID[7:] + ".json", "Layers": ["1/layer.tar"]}]).encode()})


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
        self.up, self.scanned = False, False
        self.volume_state = {}
        pin = patch.object(image, "TRUSTED_DLL_SHA256", hashlib.sha256(SYNTHETIC_DLL).hexdigest())
        pin.start()
        self.addCleanup(pin.stop)

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
            return json.dumps([{"Id": value, "Config": {"Volumes": {}, "WorkingDir": "/app"}}]).encode()
        if args[:3] == ["docker", "image", "save"]:
            self.assertEqual(args[-1], IID)
            exported = Path(args[args.index("-o") + 1])
            self.assertNotIn("source", exported.parts[-2:])
            if self.scenario != "export-missing":
                exported.write_bytes(b"corrupt" if self.scenario == "export-corrupt" else IMAGE_TAR)
            return b""
        if args[:3] == ["docker", "volume", "inspect"]:
            return json.dumps([self.volume_state[args[-1]]]).encode()
        if args[:3] == ["docker", "ps", "-aq"]:
            return b"existing-container"
        if args[:3] == ["docker", "container", "inspect"]:
            wrong = self.scenario == "container-project" or (self.built and self.scenario == "container-change")
            mounts = []
            if self.up:
                for destination, (kind, source) in deploy.configured_mounts(self.config, "backend").items():
                    mounts.append({"Type": kind, "Destination": destination, "Source": source,
                                   "Name": source if kind == "volume" else ""})
                if self.scenario == "post-shadow":
                    mounts.append({"Type": "tmpfs", "Destination": "/app"})
            actual_iid = IID if self.up else "old-immutable-image"
            if self.up and self.scenario == "post-wrong-iid":
                actual_iid = "sha256:" + "0" * 64
            return json.dumps([{"Id": "changed" if wrong else "existing-container", "Image": actual_iid,
                "Name": "/existing-backend", "Config": {"WorkingDir": "/app", "Labels": {
                    "com.docker.compose.project": "other-project" if self.scenario == "container-project" else "hockeyplanner-staging",
                    "com.docker.compose.service": "backend", "com.docker.compose.container-number": "1",
                    "com.docker.compose.project.working_dir": str(self.root)}},
                "HostConfig": {"synthetic": True, "Tmpfs": None}, "Mounts": mounts, "State": {"Running": True, "StartedAt": "unchanged"},
                "RestartCount": 0, "NetworkSettings": {"Networks": {}}}]).encode()
        if args[:2] == ["docker", "compose"]:
            file = Path(args[args.index("-f") + 1])
            if args[-3:] == ["config", "--format", "json"]:
                if file.name == "compose.json":
                    config = json.loads(file.read_bytes())
                    return json.dumps(config).encode()
                return json.dumps(self.config).encode()
            if "up" in args:
                self.assertTrue(self.scanned)
                self.up = True
                self.assertEqual(json.loads(file.read_text())["services"]["backend"]["image"], IID)
                self.assertIn("--no-build", args)
                self.assertEqual(args[args.index("--pull") + 1], "never")
                self.assertEqual(file.stat().st_mode & 0o777, 0o400 if os.name != "nt" else 0o444)
            return b""
        raise AssertionError("Unexpected stubbed command")

    def validate(self, config, root, license_file, source_context=None, volume_inspector=None):
        original = Path.stat
        def metadata(path, *a, **kw):
            result = original(path, *a, **kw)
            if path in (license_file, license_file.parent):
                return SimpleNamespace(st_mode=(stat.S_IFREG | 0o600) if path == license_file else (stat.S_IFDIR | 0o700),
                                       st_uid=1234, st_size=result.st_size, st_dev=result.st_dev, st_ino=result.st_ino)
            return result
        with patch.object(Path, "stat", metadata), patch.object(guard.os, "geteuid", return_value=1234, create=True):
            guard.validate(config, root, license_file, source_context, volume_inspector)

    def scan(self, archive_path, license_file, expected):
        self.assertEqual(expected, IID)
        self.scanned = True
        if self.scenario == "scan-fails":
            raise ValueError("synthetic scanner rejected image")
        if self.scenario == "scan-exception":
            raise RuntimeError("synthetic scanner crash")
        result = image.inspect(archive_path, license_file,
            "sha256:" + "0" * 64 if self.scenario == "wrong-export-iid" else expected)
        if self.scenario == "after-scan-config":
            (self.root / "compose.imagesharp-license.yml").write_text("drift after scan")
        if self.scenario == "after-scan-source":
            source = archive_path.parent / "source" / guard.DOCKERFILE
            os.chmod(source, 0o600); source.write_text("drift after scan")
        if self.scenario == "after-scan-container":
            self.scenario = "container-change"
        return result

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
                patch.object(deploy, "protected"), patch.object(deploy, "validate", self.validate), patch.object(deploy, "scan_image", self.scan):
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
                patch.object(deploy, "protected"), patch.object(deploy, "validate", self.validate), patch.object(deploy, "scan_image", self.scan):
            self.assertEqual(deploy.deploy(SHA, 9, self.root, self.license, self.lock, build_only=True), IID)
        self.assertFalse(any("up" in call for call in self.calls))
        self.assertTrue(self.scanned)
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
                if scenario == "build":
                    self.assertTrue((self.lock.parent / "recovery-required").exists())
                    from transaction import CURRENT
                    self.assertIsNone(CURRENT.get())
                    # Synthetic recovery only: no Docker operation is actually running.
                    for p in self.lock.parent.glob("transaction-*"):
                        for d in [p, *p.rglob("*")]:
                            os.chmod(d, 0o700 if d.is_dir() else 0o600)
                        import shutil
                        shutil.rmtree(p)
                    (self.lock.parent / "recovery-required").unlink()
                self.assertEqual(list(self.lock.parent.iterdir()), [self.lock] if self.lock.exists() else [])

    def test_review_counterexample_scanner_failure_now_prevents_up(self):
        with self.assertRaises(ValueError):
            self.execute("scan-fails")
        self.assertTrue(self.scanned)
        self.assertFalse(self.up)
        self.assertEqual(list(self.lock.parent.iterdir()), [self.lock])

    def test_previous_build_only_scan_cannot_authorize_next_unscanned_build(self):
        self.test_build_only_never_invokes_up()
        self.calls, self.scanned, self.built = [], False, False
        # B is a distinct current build with an actual historical disclosure.
        # Real scanner accepts A, then rejects B; no cached authorization from A.
        bad_layer = packed({image.DLL: SYNTHETIC_DLL, image.DEPS: json.dumps({"libraries": {
            "SixLabors.ImageSharp/4.1.2": {}}, "targets": {"net10": {"SixLabors.ImageSharp/4.1.2": {
            "runtime": {"lib/net8.0/SixLabors.ImageSharp.dll": {}}}}}}).encode(),
            "run/private.bin": self.license.read_bytes()})
        config_b = json.dumps({"config": {"WorkingDir": "/app", "Env": []}, "rootfs": {
            "type": "layers", "diff_ids": ["sha256:" + hashlib.sha256(bad_layer).hexdigest()]}}).encode()
        iid_b = "sha256:" + hashlib.sha256(config_b).hexdigest()
        self.assertNotEqual(iid_b, IID)
        tar_b = packed({iid_b[7:] + ".json": config_b, "1/layer.tar": bad_layer,
            "manifest.json": json.dumps([{"Config": iid_b[7:] + ".json", "Layers": ["1/layer.tar"]}]).encode()})
        with patch.dict(globals(), {"IID": iid_b, "IMAGE_TAR": tar_b}), self.assertRaises(ValueError):
            self.execute("success")
        self.assertTrue(self.scanned)
        self.assertFalse(self.up)

    def test_license_named_volume_and_directory_inode_exposures_block_deployment(self):
        ordinary = self.root / "ordinary-volume"; ordinary.mkdir()
        unrelated = self.root / "public-dir"; unrelated.mkdir()
        os.link(self.license, unrelated / "alias.bin")
        original = copy.deepcopy(self.config)
        volume = {"name": "hockeyplanner-staging_data", "driver": "local"}
        dangerous = [
            ({"type": "volume", "source": "data", "target": "/data"}, {**volume, "driver_opts":
                {"type": "none", "o": "bind", "device": str(self.license.parent)}}),
            ({"type": "bind", "source": str(unrelated), "target": "/data"}, None),
            ({"type": "volume", "source": "data", "target": "/data"}, {"external": True, "name": volume["name"]}),
            ({"type": "volume", "source": "data", "target": "/data"}, {}),
            ({"type": "volume", "source": "data", "target": "/data"}, {**volume, "driver_opts": {"o": "unknown"}}),
        ]
        for mount, definition in dangerous:
            for dependency in (False, True):
                with self.subTest(mount=mount["type"], dependency=dependency):
                    self.config = copy.deepcopy(original)
                    service = self.config["services"]["backend"]
                    if dependency:
                        service["depends_on"] = {"db": {}}
                        service = self.config["services"].setdefault("db", {})
                    service["volumes"] = [mount]
                    self.config["volumes"] = {"data": definition} if definition is not None else {}
                    self.calls, self.up = [], False
                    with self.assertRaises((ValueError, KeyError, OSError)):
                        self.execute("success")
                    self.assertFalse(self.up)
        self.config = original
        self.config["services"]["backend"]["volumes"] = [{"type": "volume", "source": "data", "target": "/data"}]
        self.config["volumes"] = {"data": volume}
        self.volume_state[volume["name"]] = {"Name": volume["name"], "Driver": "local", "Scope": "local",
            "Options": {}, "Mountpoint": str(ordinary)}
        self.execute("success")  # Provably inspected ordinary named volume supported.
        self.assertTrue(self.up)

    def test_all_shadowing_and_unsupported_runtime_mounts_prevent_actual_up(self):
        safe = self.root / "safe.file"; safe.write_text("synthetic unrelated")
        original = copy.deepcopy(self.config)
        mutations = [
            {"volumes": [{"type": "bind", "source": str(safe), "target": destination}]}
                for destination in ("/app/SixLabors.ImageSharp.dll", "/app/HockeyPlanner.Backend.WebAPI.deps.json", "/", "/app/../app")]
        mutations += [
            {"volumes": [{"type": "volume", "source": "data", "target": "/app"}]},
            {"volumes": [{"type": "tmpfs", "target": "/app"}]}, {"tmpfs": ["/app"]},
            {"secrets": [{"source": "other", "target": "/app/SixLabors.ImageSharp.dll"}]},
            {"configs": [{"source": "other", "target": "/app/HockeyPlanner.Backend.WebAPI.deps.json"}]},
            {"volumes_from": ["db:ro"]}, {"volumes_from": ["container:external"]},
            {"volumes": [{"type": "unsupported", "target": "/safe"}]},
            {"volumes": [{"type": "tmpfs", "target": ["/safe"]}]},
            {"tmpfs": {"/safe": "unmodeled"}},
        ]
        for mutation in mutations:
            with self.subTest(mutation=mutation):
                self.config = copy.deepcopy(original)
                self.config["services"]["backend"].update(mutation)
                self.config["configs"] = {"other": {"file": str(safe)}}
                self.config["secrets"]["other"] = {"file": str(safe)}
                self.calls, self.up = [], False
                with self.assertRaises((ValueError, KeyError, TypeError)):
                    self.execute("success")
                self.assertFalse(self.up)
                self.assertFalse(any(call[:3] == ["docker", "buildx", "build"] for call in self.calls))
        self.config = copy.deepcopy(original)
        self.config["services"]["backend"]["volumes"] = [{"type": "bind", "source": str(safe), "target": "/safe/file"}]
        self.execute("success")
        self.assertTrue(self.up)

    def test_unexpected_post_up_mount_or_image_retains_recovery_barrier(self):
        for scenario in ("post-shadow", "post-wrong-iid"):
            with self.subTest(scenario=scenario):
                # Each independent failure needs its own recovery boundary.
                test = DeployTransactionTests(); test.setUp()
                try:
                    with self.assertRaises(ValueError):
                        test.execute(scenario)
                    self.assertTrue(test.up)
                    self.assertTrue((test.lock.parent / "recovery-required").exists())
                    self.assertTrue(list(test.lock.parent.glob("transaction-*")))
                finally:
                    test.doCleanups()

    def test_pending_recovery_prevents_any_command_or_mutation(self):
        (self.lock.parent / "recovery-required").write_text("synthetic uncertain operation")
        with self.assertRaises(ValueError):
            self.execute("success")
        self.assertEqual(self.calls, [])

    def test_volume_engine_state_and_backing_alias_drift_fail_closed(self):
        location = self.root / "volume"; location.mkdir()
        self.config["services"]["backend"]["volumes"] = [{"type": "volume", "source": "data", "target": "/data"}]
        definition = {"name": "hockeyplanner-staging_data", "driver": "local"}
        self.config["volumes"] = {"data": definition}
        state = {"Name": definition["name"], "Driver": "local", "Scope": "local", "Options": {}, "Mountpoint": str(location)}
        for change in ({"Driver": "unknown"}, {"Options": {"device": str(self.license.parent)}},
                       {"Options": []}, {"Mountpoint": str(self.license.parent)}, {"Scope": "global"}):
            with self.subTest(change=change):
                self.volume_state[definition["name"]] = {**state, **change}
                self.calls, self.up = [], False
                with self.assertRaises(ValueError):
                    self.execute("success")
                self.assertFalse(self.up)
        definition["driver_opts"] = {"type": "none", "o": "bind", "device": str(location)}
        self.volume_state[definition["name"]] = {**state, "Options": definition["driver_opts"]}
        self.execute("success")  # Safe local bind-backed definition AND actual state.
        self.assertTrue(self.up)

    def test_build_only_also_rejects_scan_failure_and_has_no_skip_option(self):
        with patch.object(deploy, "command", self.command), patch.object(deploy, "held_lock", self.held), \
                patch.object(deploy, "protected"), patch.object(deploy, "validate", self.validate), \
                patch.object(deploy, "scan_image", side_effect=ValueError("synthetic scan failed")):
            with self.assertRaises(ValueError):
                deploy.deploy(SHA, 9, self.root, self.license, self.lock, build_only=True)
        self.assertFalse(self.up)
        self.assertEqual(list(self.lock.parent.iterdir()), [self.lock])
        errors = io.StringIO()
        with patch.object(sys, "argv", ["deploy", "--expected-sha", SHA, "--lock-fd", "9", "--skip-image-scan"]), \
                contextlib.redirect_stderr(errors), self.assertRaises(SystemExit) as result:
            deploy.main()
        self.assertEqual(result.exception.code, 2)

    def test_success_chain_scan_and_revalidation_precede_up(self):
        events = []
        def validate(*args, **kwargs):
            events.append("validate")
            return self.validate(*args, **kwargs)
        def command(args, cwd=None):
            if args[:3] == ["docker", "buildx", "build"]:
                events.append("build")
            if args[:3] == ["docker", "image", "save"]:
                events.append("export:" + args[-1])
            if "up" in args:
                events.append("up")
            return self.command(args, cwd)
        def scan(*args):
            events.append("scan:" + args[-1])
            return self.scan(*args)
        with patch.object(deploy, "command", command), patch.object(deploy, "held_lock", self.held), \
                patch.object(deploy, "protected"), patch.object(deploy, "validate", validate), patch.object(deploy, "scan_image", scan):
            deploy.deploy(SHA, 9, self.root, self.license, self.lock)
        sequence = [events.index("build"), events.index("export:" + IID), events.index("scan:" + IID),
                    len(events) - 1 - events[::-1].index("validate"), events.index("up")]
        self.assertEqual(sequence, sorted(sequence))

    def test_unexpected_scanner_error_is_sanitized_at_cli_boundary(self):
        output, errors = io.StringIO(), io.StringIO()
        with patch.object(sys, "argv", ["deploy", "--expected-sha", SHA, "--lock-fd", "9"]), \
                patch.object(deploy, "deploy", side_effect=RuntimeError("synthetic-private-value")), \
                contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
            self.assertEqual(deploy.main(), 1)
        self.assertEqual(output.getvalue(), "")
        self.assertEqual(errors.getvalue(), "BLOCKED: staging deployment transaction failed.\n")

    def test_scan_export_identity_and_after_scan_drift_block_up(self):
        for scenario in ("scan-exception", "wrong-export-iid", "export-missing", "export-corrupt",
                         "after-scan-config", "after-scan-source", "after-scan-container"):
            with self.subTest(scenario=scenario):
                self.calls, self.built, self.scanned, self.up = [], False, False, False
                with self.assertRaises((ValueError, OSError, RuntimeError, tarfile.TarError)):
                    self.execute(scenario)
                self.assertFalse(self.up)
                self.assertEqual(list(self.lock.parent.iterdir()), [self.lock])

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
