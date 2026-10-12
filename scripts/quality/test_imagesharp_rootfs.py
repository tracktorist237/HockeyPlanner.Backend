"""Rootfs-aware mount regressions through the assembled deployment transaction.

Only Git/Docker transport and license ownership are simulated by the existing
fixture. Tar parsing, DiffIDs/IID, DLL pinning, final reconstruction, Compose
mount validation and deployment/post-up decisions execute the production code.
"""
from dataclasses import replace
import ctypes
import errno
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import test_imagesharp_deploy as fixture
from test_imagesharp_image import packed


def symlink(value):
    return (b"", {"type": tarfile.SYMTYPE, "linkname": value})


def directory():
    return (b"", {"type": tarfile.DIRTYPE})


def bundle(entries=None, later=None, volumes=None):
    with tarfile.open(fileobj=io.BytesIO(fixture.LAYER)) as base:
        members = {member.name: base.extractfile(member).read() for member in base if member.isfile()}
    members.update(entries or {})
    layers = [packed(members)]
    if later is not None:
        layers.append(packed(later))
    config = {"config": {"WorkingDir": "/app", "Env": [], "Volumes": volumes or {}}, "rootfs": {
        "type": "layers", "diff_ids": ["sha256:" + hashlib.sha256(layer).hexdigest() for layer in layers]}}
    raw = json.dumps(config).encode()
    iid = "sha256:" + hashlib.sha256(raw).hexdigest()
    names = [str(i) + "/layer.tar" for i in range(len(layers))]
    archive = packed({**dict(zip(names, layers)), iid[7:] + ".json": raw,
        "manifest.json": json.dumps([{"Config": iid[7:] + ".json", "Layers": names}]).encode()})
    return iid, archive


class RootfsDeploymentTests(unittest.TestCase):
    def case(self, targets=(), category="bind", file=False):
        case = fixture.DeployTransactionTests()
        case.setUp()
        self.addCleanup(case.doCleanups)
        public = case.root / "public"
        public.mkdir()
        regular = public / "unrelated.file"
        regular.write_text("unrelated synthetic content")
        service = case.config["services"]["backend"]
        for destination in targets:
            if category in ("secrets", "configs"):
                case.config.setdefault(category, {})["other"] = {"file": str(regular)}
                service.setdefault(category, []).append({"source": "other", "target": destination})
            elif category == "volume":
                name = "hockeyplanner-staging_data"
                case.config["volumes"] = {"data": {"name": name, "driver": "local"}}
                case.volume_state[name] = {"Name": name, "Driver": "local", "Scope": "local",
                    "Options": {}, "Mountpoint": str(public)}
                service.setdefault("volumes", []).append({"type": "volume", "source": "data", "target": destination})
            elif category == "tmpfs":
                service.setdefault("volumes", []).append({"type": "tmpfs", "target": destination})
            else:
                service.setdefault("volumes", []).append({"type": "bind", "source": str(regular if file else public),
                    "target": destination})
        return case

    def execute(self, case, entries=None, later=None, volumes=None, build_only=False, observer=None):
        iid, archive = bundle(entries, later, volumes)
        original_command = case.command
        def command(args, cwd=None):
            result = original_command(args, cwd)
            if args[:3] == ["docker", "image", "inspect"]:
                data = json.loads(result)
                data[0]["Config"]["Volumes"] = volumes or {}
                result = json.dumps(data).encode()
            if observer:
                result = observer(case, args, result)
            return result
        with patch.dict(fixture.__dict__, {"IID": iid, "IMAGE_TAR": archive}), \
                patch.object(fixture.deploy, "command", command), patch.object(fixture.deploy, "held_lock", case.held), \
                patch.object(fixture.deploy, "protected"), patch.object(fixture.deploy, "validate", case.validate), \
                patch.object(fixture.deploy, "scan_image", case.scan):
            return fixture.deploy.deploy(fixture.SHA, 9, case.root, case.license, case.lock, build_only=build_only)

    def blocked(self, entries, destinations, category="bind", later=None, volumes=None, file=False):
        case = self.case(destinations, category, file)
        with self.assertRaises(ValueError):
            self.execute(case, entries, later, volumes)
        self.assertTrue(case.scanned)
        self.assertTrue(case.built)
        self.assertFalse(case.up)
        self.assertTrue(any(args[:3] == ["docker", "image", "save"] for args in case.calls))
        # Export finished and no up was invoked: determinate rejection safely
        # cleans the barrier. An unsafe started transaction may never do so.
        self.assertEqual(list(case.lock.parent.iterdir()), [case.lock])
        return case

    def test_round4_alias_counterexample_blocks_before_up_after_real_exact_iid_scan(self):
        case = self.blocked({"safe-alias": symlink("app")}, ["/safe-alias"])
        iid, _ = bundle({"safe-alias": symlink("app")})
        self.assertTrue(any(args == ["docker", "image", "inspect", iid] for args in case.calls))
        output, errors = io.StringIO(), io.StringIO()
        # CLI failure never reports PASS; the assembled transaction above
        # already exercised the actual scanner/rootfs decision, without mocks.
        with patch.object(sys, "argv", ["deploy", "--expected-sha", fixture.SHA, "--lock-fd", "9"]), \
                patch.object(fixture.deploy, "deploy", side_effect=ValueError("unsafe rootfs")), \
                fixture.contextlib.redirect_stdout(output), fixture.contextlib.redirect_stderr(errors):
            self.assertEqual(fixture.deploy.main(), 1)
        self.assertEqual(output.getvalue(), "")
        self.assertNotIn("PASS", errors.getvalue())

    def test_dangerous_chains_ancestors_final_components_and_file_aliases_block(self):
        variants = [
            ({"alias": symlink("/app")}, "/alias"),
            ({"alias": symlink("app")}, "/alias"),
            ({"first": symlink("/second"), "second": symlink("/app")}, "/first"),
            ({"private/alias": symlink("../app")}, "/private/alias"),
            ({"outer": symlink("/nested"), "nested/link": symlink("../app")}, "/outer/link"),
            ({"alias": symlink("/app")}, "/alias/SixLabors.ImageSharp.dll"),
            ({"alias": symlink("/app")}, "/alias/HockeyPlanner.Backend.WebAPI.deps.json"),
            ({"file-alias": symlink("app/SixLabors.ImageSharp.dll")}, "/file-alias"),
            ({"file-alias": symlink("app/HockeyPlanner.Backend.WebAPI.deps.json")}, "/file-alias"),
            ({"alias": symlink("/../../app")}, "/alias"),
            # Dotdot must follow preceding symlink expansion, not pre-clean it.
            ({"jump": symlink("/app/child"), "app/child": directory(), "alias": symlink("jump/..")}, "/alias"),
        ]
        for entries, destination in variants:
            with self.subTest(destination=destination, entries=entries):
                self.blocked(entries, [destination])

    def test_cycles_dangling_depth_hardlinks_and_type_conflicts_fail_closed(self):
        variants = [
            ({"a": symlink("b"), "b": symlink("a")}, "/a"),
            ({"a": symlink("missing")}, "/a"),
            ({"a": symlink("/missing/../app")}, "/a"),
            ({"a": b"ordinary file"}, "/a/child"),
            ({"a": symlink("plain/../safe"), "plain": b"file"}, "/a"),
            ({"a": (b"", {"type": tarfile.LNKTYPE, "linkname": fixture.image.DLL})}, "/a"),
            ({**{"l" + str(i): symlink("l" + str(i + 1)) for i in range(41)}, "l41": directory()}, "/l0"),
            ({"a": symlink("x" * 4097)}, "/a"),
            ({"a": b"existing file"}, "/a"),  # directory bind over ordinary file
        ]
        for entries, destination in variants:
            with self.subTest(destination=destination):
                self.blocked(entries, [destination])
        self.blocked({"a": directory()}, ["/a"], file=True)

    def test_image_volume_secret_config_tmpfs_and_named_volume_aliases_block(self):
        for category in ("bind", "volume", "tmpfs", "secrets", "configs"):
            with self.subTest(category=category):
                self.blocked({"alias": symlink("/app")}, ["/alias"], category)
        self.blocked({"alias": symlink("/app")}, [], volumes={"/alias": {}})

    def test_final_whiteouts_and_replacements_determine_mount_safety(self):
        final_safe = [
            ({"alias": symlink("app")}, {".wh.alias": b""}),
            ({"alias": symlink("app")}, {"alias": directory()}),
            ({"alias": symlink("app"), "data": directory()}, {"alias": symlink("data")}),
            ({"folder/alias": symlink("../app")}, {"folder/.wh..wh..opq": b""}),
        ]
        for entries, later in final_safe:
            destination = "/folder/alias" if "folder/alias" in entries else "/alias"
            with self.subTest(later=later):
                case = self.case([destination])
                self.execute(case, entries, later)
                self.assertTrue(case.up)
                self.assertEqual(list(case.lock.parent.iterdir()), [case.lock])
        self.blocked({"alias": directory()}, ["/alias"], later={"alias": symlink("app")})
        self.blocked({"alias": symlink("data"), "data": directory()}, ["/alias"], later={"alias": symlink("app")})

    def test_safe_aliases_resources_volumes_and_no_mounts_pass(self):
        for category in ("bind", "volume", "tmpfs", "secrets", "configs"):
            with self.subTest(category=category):
                file = category in ("secrets", "configs")
                entries = {"data": b"ordinary file" if file else directory(), "alias": symlink("/data")}
                case = self.case(["/alias"], category)
                self.execute(case, entries)
                self.assertTrue(case.up)
        case = self.case(["/ordinary"])
        self.execute(case, {"ordinary": directory(), "unrelated-cycle": symlink("unrelated-cycle")})
        self.assertTrue(case.up)  # Unrelated image symlinks are not blanket-rejected.
        case = self.case()
        self.execute(case)
        self.assertTrue(case.up)
        case = self.case(["/alias"])
        self.execute(case, {"alias": symlink("data"), "data": directory()}, volumes={"/alias": {}})
        self.assertTrue(case.up)

    def test_overlapping_mounts_and_symlink_lookup_interactions_block(self):
        entries = {"holder/link": symlink("/safe"), "safe": directory(), "a": symlink("/data"), "data": directory()}
        for targets in (["/holder", "/holder/link"], ["/a", "/data"], ["/a", "/data/child"],
                        ["/parent/child", "/parent"]):
            for category in ("bind", "volume", "tmpfs", "secrets", "configs"):
                with self.subTest(targets=targets, category=category):
                    self.blocked(entries, targets, category)
        self.blocked(entries, ["/holder/link"], volumes={"/holder": {}})

    def test_implicit_runtime_trees_cannot_change_custom_lookup_or_cover_app(self):
        self.blocked({"alias": symlink("/dev")}, ["/alias"])
        self.blocked({"dev": symlink("/app")}, [])
        self.blocked({"alias": symlink("/etc/hosts")}, ["/alias"])
        self.blocked({"dev": b"file"}, [])
        self.blocked({"etc/hosts": directory()}, [])

    def test_safe_canonical_post_up_destination_reuses_proof_and_mismatch_keeps_barrier(self):
        def canonical(case, args, result):
            if case.up and args[:3] == ["docker", "container", "inspect"]:
                data = json.loads(result)
                data[0]["Mounts"][0]["Destination"] = "/data"
                return json.dumps(data).encode()
            return result
        case = self.case(["/alias"])
        self.execute(case, {"alias": symlink("/data"), "data": directory()}, observer=canonical)
        self.assertTrue(case.up)
        self.assertEqual(list(case.lock.parent.iterdir()), [case.lock])
        def mismatch(case, args, result):
            if case.up and args[:3] == ["docker", "container", "inspect"]:
                data = json.loads(result)
                data[0]["Mounts"][0]["Destination"] = "/unverified"
                return json.dumps(data).encode()
            return result
        case = self.case(["/alias"])
        with self.assertRaises(ValueError):
            self.execute(case, {"alias": symlink("/data"), "data": directory(), "unverified": symlink("/app")}, observer=mismatch)
        self.assertTrue(case.up)
        self.assertTrue((case.lock.parent / "recovery-required").exists())
        self.assertTrue(list(case.lock.parent.glob("transaction-*")))
        def duplicate_tmpfs(case, args, result):
            if case.up and args[:3] == ["docker", "container", "inspect"]:
                data = json.loads(result)
                data[0]["HostConfig"]["Tmpfs"] = {"/alias": "", "/data": ""}
                return json.dumps(data).encode()
            return result
        case = self.case(["/alias"], "tmpfs")
        with self.assertRaises(ValueError):
            self.execute(case, {"alias": symlink("/data"), "data": directory()}, observer=duplicate_tmpfs)
        self.assertTrue(case.up)
        self.assertTrue((case.lock.parent / "recovery-required").exists())

    def test_scan_evidence_cannot_authorize_wrong_iid_destinations_or_layer_count(self):
        for mutation in (lambda result: replace(result, image_id="sha256:" + "0" * 64),
                         lambda result: replace(result, destinations=("/other",)), lambda result: result.layers):
            with self.subTest(mutation=mutation):
                case = self.case(["/safe"])
                original = case.scan
                case.scan = lambda *args: mutation(original(*args))
                with self.assertRaises(ValueError):
                    self.execute(case)
                self.assertTrue(case.scanned)
                self.assertFalse(case.up)
        case = self.case(["/alias"])
        with self.assertRaises(ValueError):
            self.execute(case, {"alias": symlink("app")}, build_only=True)
        self.assertTrue(case.scanned)
        self.assertFalse(case.up)


def linux_resolution():
    """Real unprivileged Linux IN_ROOT lookup; no mount/daemon/VPS operations."""
    class OpenHow(ctypes.Structure):
        _fields_ = [("flags", ctypes.c_ulonglong), ("mode", ctypes.c_ulonglong), ("resolve", ctypes.c_ulonglong)]
    libc = ctypes.CDLL(None, use_errno=True)
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        (root / "app").mkdir()
        (root / "app/dll").write_text("synthetic trusted")
        (root / "private").mkdir()
        filesystem = {"app": ("dir", None, None), "app/dll": ("file", None, None), "private": ("dir", None, None)}
        for path, link in (("relative", "app"), ("absolute", "/app"), ("first", "second"),
                           ("second", "app"), ("private/nested", "../app"), ("cycle", "cycle"), ("dangling", "absent")):
            (root / path).symlink_to(link)
            filesystem[path] = ("symlink", link, None)
        fd = os.open(root, os.O_PATH | os.O_DIRECTORY)
        try:
            for path in ("relative/dll", "absolute/dll", "first/dll", "private/nested/dll"):
                how = OpenHow(os.O_PATH, 0, 0x10)  # RESOLVE_IN_ROOT: absolute links restart at rootfd.
                opened = libc.syscall(437, fd, ctypes.c_char_p(path.encode()), ctypes.byref(how), ctypes.sizeof(how))
                if opened < 0:
                    raise OSError(ctypes.get_errno(), "Linux IN_ROOT lookup failed")
                try:
                    assert os.fstat(opened).st_ino == (root / "app/dll").stat().st_ino
                    assert fixture.image.resolve_mount(filesystem, "/" + path).effective == "/app/dll"
                finally:
                    os.close(opened)
            for path, error in (("cycle", errno.ELOOP), ("dangling", errno.ENOENT), ("app/dll/child", errno.ENOTDIR)):
                how = OpenHow(os.O_PATH, 0, 0x10)
                opened = libc.syscall(437, fd, ctypes.c_char_p(path.encode()), ctypes.byref(how), ctypes.sizeof(how))
                assert opened == -1 and ctypes.get_errno() == error
                try:
                    fixture.image.resolve_mount(filesystem, "/" + path)
                except ValueError:
                    pass
                else:
                    raise AssertionError("Model accepted a kernel-rejected lookup")
        finally:
            os.close(fd)


class LinuxRootResolutionTests(unittest.TestCase):
    def test_real_linux_container_root_relative_absolute_nested_chain_and_invalid_lookup(self):
        if sys.platform == "linux":
            linux_resolution()
        else:
            require_windows = sys.platform == "win32"
            self.assertTrue(require_windows, "Required Linux semantics need Linux or configured Windows WSL")
            script = Path(__file__).resolve()
            linux_path = "/mnt/" + script.drive[0].lower() + script.as_posix()[2:]
            result = subprocess.run(["wsl", "-d", "Ubuntu-24.04", "--", "python3", linux_path, "--linux-resolution"],
                                    capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, "Required real Linux root lookup failed")


if __name__ == "__main__":
    if sys.argv[1:] == ["--linux-resolution"]:
        linux_resolution()
    else:
        unittest.main()
