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

# Public, synthetic capability output in the runc1.3.4 Features schema. Hook
# NAMES describe supported hooks, not installed hook programs/configuration.
FEATURES_134 = {
    "ociVersionMin": "1.0.0", "ociVersionMax": "1.2.1",
    "hooks": ["prestart", "createRuntime", "createContainer", "startContainer", "poststart", "poststop"],
    "mountOptions": ["ro", "rw", "bind", "rbind"],
    "linux": {"namespaces": ["mount", "pid", "network"], "capabilities": ["CAP_CHOWN"],
        "cgroup": {"v1": True, "v2": True, "systemd": True, "systemdUser": True, "rdma": True},
        "seccomp": {"enabled": True, "actions": ["SCMP_ACT_ALLOW"], "operators": ["SCMP_CMP_EQ"],
            "archs": ["SCMP_ARCH_X86_64"], "knownFlags": [], "supportedFlags": []},
        "apparmor": {"enabled": True}, "selinux": {"enabled": True}, "intelRdt": {"enabled": True},
        "mountExtensions": {"idmap": {"enabled": True}}},
    "annotations": {"org.opencontainers.runc.version": "1.3.4"},
    "potentiallyUnsafeConfigAnnotations": ["bundle", "org.systemd.property.", "org.criu.config"],
}
OBSERVED_MASKS_29 = ["/proc/acpi", "/proc/asound", "/proc/interrupts", "/proc/kcore", "/proc/keys",
    "/proc/latency_stats", "/proc/sched_debug", "/proc/scsi", "/proc/timer_list", "/proc/timer_stats",
    "/sys/devices/virtual/powercap", "/sys/firmware"]


def docker29(case, args, result):
    """Engine/Compose/runc profile only; real archive/scan/guards remain in use."""
    if args[:2] == ["docker", "version"]:
        data = json.loads(result)
        data["Version"] = "29.1.3"
        data["Components"] = [{"Name": "runc", "Version": "1.3.4"}]
        return json.dumps(data).encode()
    if args == ["docker", "compose", "version", "--short"]:
        return b"2.40.3\n"
    if args[:2] == ["docker", "info"]:
        data = json.loads(result)
        data["Runtimes"]["runc"] = {"path": "runc", "status": {
            "org.opencontainers.runtime-spec.features": json.dumps(FEATURES_134)}}
        data["SecurityOptions"].append("name=apparmor")
        return json.dumps(data).encode()
    if args[:3] == ["docker", "container", "inspect"]:
        data = json.loads(result)
        data[0]["HostConfig"]["MaskedPaths"] = OBSERVED_MASKS_29.copy()
        return json.dumps(data).encode()
    return result


def symlink(value):
    return (b"", {"type": tarfile.SYMTYPE, "linkname": value})


def directory():
    return (b"", {"type": tarfile.DIRTYPE})


def bundle(entries=None, later=None, volumes=None, blob_layout=False):
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
    names = ["blobs/sha256/" + hashlib.sha256(layer).hexdigest() if blob_layout else str(i) + "/layer.tar"
             for i, layer in enumerate(layers)]
    config_name = "blobs/sha256/" + iid[7:] if blob_layout else iid[7:] + ".json"
    extra = {}
    if blob_layout:
        # Classic Moby29 wrapper: OCI descriptors plus Docker manifest,
        # config IID (NOT the OCI manifest/index digest), raw TarStream layers.
        manifest = json.dumps({"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json",
            "config": {"mediaType": "application/vnd.oci.image.config.v1+json", "digest": iid, "size": len(raw)},
            "layers": [{"mediaType": "application/vnd.oci.image.layer.v1.tar", "digest": "sha256:" + hashlib.sha256(layer).hexdigest(),
                        "size": len(layer)} for layer in layers]}).encode()
        manifest_id = "sha256:" + hashlib.sha256(manifest).hexdigest()
        extra = {"blobs/sha256/" + manifest_id[7:]: manifest,
            "oci-layout": b'{"imageLayoutVersion":"1.0.0"}',
            "index.json": json.dumps({"schemaVersion": 2, "manifests": [{"mediaType": "application/vnd.oci.image.manifest.v1+json",
                "digest": manifest_id, "size": len(manifest)}]}).encode()}
    archive = packed({**extra, **dict(zip(names, layers)), config_name: raw,
        "manifest.json": json.dumps([{"Config": config_name, "Layers": names}]).encode()})
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

    def execute(self, case, entries=None, later=None, volumes=None, build_only=False, observer=None, blob_layout=False):
        iid, archive = bundle(entries, later, volumes, blob_layout)
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


class InitDeploymentTests(unittest.TestCase):
    # Keep the existing F1-F4 suite as-is; these tests use its assembled transaction
    # and actual scanner/resolver. No Docker deployment or private inputs.
    case = RootfsDeploymentTests.case
    execute = RootfsDeploymentTests.execute

    def init_entries(self, required, chain=False):
        if chain:
            return {"sbin/docker-init": symlink("../init-link"), "init-link": symlink("/" + required)}
        return {"sbin/docker-init": symlink("/" + required)}

    def test_init_true_with_unsafe_file_alias_or_chain_blocks_before_build_and_up(self):
        for required in (fixture.image.DLL, fixture.image.DEPS):
            for chain in (False, True):
                with self.subTest(required=required, chain=chain):
                    case = self.case()
                    case.config["services"]["backend"]["init"] = True
                    with self.assertRaises(ValueError):
                        self.execute(case, self.init_entries(required, chain))
                    self.assertFalse(case.built)
                    self.assertFalse(case.up)
                    self.assertEqual(list(case.lock.parent.iterdir()), [case.lock])

    def test_even_safe_init_enabled_image_is_outside_supported_model(self):
        case = self.case()
        case.config["services"]["backend"]["init"] = True
        with self.assertRaises(ValueError):
            self.execute(case, {"sbin/docker-init": b"synthetic ordinary file"})
        self.assertFalse(case.up)

    def test_omitted_null_and_non_boolean_init_block_even_with_daemon_default_enabled(self):
        def enabled_default(case, args, result):
            if args[:3] == ["docker", "container", "inspect"]:
                data = json.loads(result)
                data[0]["HostConfig"]["Init"] = None  # Moby falls back to daemon Init=true.
                return json.dumps(data).encode()
            return result
        for value in ("omitted", None, 0, "false"):
            with self.subTest(init=value):
                case = self.case()
                if value == "omitted":
                    del case.config["services"]["backend"]["init"]
                else:
                    case.config["services"]["backend"]["init"] = value
                with self.assertRaises(ValueError):
                    self.execute(case, self.init_entries(fixture.image.DLL), observer=enabled_default)
                self.assertFalse(case.built)
                self.assertFalse(case.up)

    def test_explicit_false_passes_exact_iid_scan_inert_aliases_and_ordinary_image(self):
        # Explicit false short-circuits daemon Init=true as well as Init=false;
        # the daemon default is deliberately not assumed or inferred from Mounts.
        for entries in ({}, self.init_entries(fixture.image.DLL), self.init_entries(fixture.image.DEPS, True)):
            with self.subTest(entries=entries):
                case = self.case()
                iid = self.execute(case, entries)
                self.assertEqual(iid, bundle(entries)[0])
                self.assertTrue(case.scanned)
                self.assertTrue(case.up)
                self.assertEqual(list(case.lock.parent.iterdir()), [case.lock])

    def test_existing_backend_requires_explicit_false_hostconfig_without_inspect_mount(self):
        for value in (True, None, "missing", 0):
            with self.subTest(init=value):
                case = self.case()
                def changed(case, args, result):
                    if args[:3] == ["docker", "container", "inspect"]:
                        data = json.loads(result)
                        self.assertEqual(data[0]["Mounts"], [])
                        if value == "missing":
                            del data[0]["HostConfig"]["Init"]
                        else:
                            data[0]["HostConfig"]["Init"] = value
                        return json.dumps(data).encode()
                    return result
                with self.assertRaises(ValueError):
                    self.execute(case, self.init_entries(fixture.image.DLL), observer=changed)
                self.assertFalse(case.built)
                self.assertFalse(case.up)

    def test_post_up_implicit_init_absent_from_mounts_never_passes_and_keeps_barrier(self):
        for value in (True, None, "missing"):
            with self.subTest(init=value):
                case = self.case()
                def changed(case, args, result):
                    if case.up and args[:3] == ["docker", "container", "inspect"]:
                        data = json.loads(result)
                        self.assertEqual(data[0]["Mounts"], [])  # No implicit OCI mount disclosure.
                        if value == "missing":
                            del data[0]["HostConfig"]["Init"]
                        else:
                            data[0]["HostConfig"]["Init"] = value
                        return json.dumps(data).encode()
                    return result
                with self.assertRaises(ValueError):
                    self.execute(case, self.init_entries(fixture.image.DEPS, True), observer=changed)
                self.assertTrue(case.scanned)
                self.assertTrue(case.up)
                self.assertTrue((case.lock.parent / "recovery-required").exists())
                self.assertTrue(list(case.lock.parent.glob("transaction-*")))
                count = len(case.calls)
                with self.assertRaises(ValueError):
                    self.execute(case)  # Uncertain outcome must bar the next transaction.
                self.assertEqual(len(case.calls), count)
                self.assertFalse(any("ps" == call[-1] for call in case.calls))

    def test_unsupported_compose_runtime_gpu_privileged_and_hooks_block_before_up(self):
        for fields in ({"runtime": "custom"}, {"privileged": True}, {"security_opt": ["systempaths=unconfined"]},
                       {"gpus": "all"}, {"deploy": {"resources": {"reservations": {"devices": [{"capabilities": ["gpu"]}]}}}}):
            with self.subTest(fields=fields):
                case = self.case()
                case.config["services"]["backend"].update(fields)
                with self.assertRaises(ValueError):
                    self.execute(case)
                self.assertFalse(case.built)
                self.assertFalse(case.up)

    def test_unknown_daemon_runtime_and_drift_block_pre_up_or_retain_post_up_barrier(self):
        for stage in ("preflight", "pre-up", "post-up"):
            for mutation in ("engine", "runc", "compose", "default", "args", "path", "rootless", "unknown-field"):
                with self.subTest(stage=stage, mutation=mutation):
                    case = self.case()
                    def changed(case, args, result):
                        active = stage == "preflight" or (stage == "pre-up" and case.scanned) or (stage == "post-up" and case.up)
                        if not active:
                            return result
                        if args[:2] == ["docker", "version"]:
                            data = json.loads(result)
                            if mutation == "engine": data["Version"] = "unknown"
                            if mutation == "runc": data["Components"] = []
                            return json.dumps(data).encode()
                        if args == ["docker", "compose", "version", "--short"] and mutation == "compose":
                            return b"unknown"
                        if args[:2] == ["docker", "info"]:
                            data = json.loads(result)
                            if mutation == "default": data["DefaultRuntime"] = "custom"
                            if mutation == "args": data["Runtimes"]["runc"]["runtimeArgs"] = ["--custom"]
                            if mutation == "path": data["Runtimes"]["runc"]["path"] = "/custom/runc"
                            if mutation == "rootless": data["SecurityOptions"].append("name=rootless")
                            if mutation == "unknown-field": data["Runtimes"]["runc"]["options"] = {"hooks": "custom"}
                            return json.dumps(data).encode()
                        return result
                    with self.assertRaises(ValueError):
                        self.execute(case, observer=changed)
                    self.assertEqual(case.up, stage == "post-up")
                    self.assertEqual((case.lock.parent / "recovery-required").exists(), stage == "post-up")

    def test_unmodeled_actual_oci_mount_options_reject_even_when_mounts_empty(self):
        for field, value in (("Runtime", "custom"), ("Privileged", True), ("SecurityOpt", ["custom"]),
                             ("Devices", [{"PathInContainer": "/app"}]), ("DeviceRequests", [{"Driver": "nvidia"}]),
                             ("MaskedPaths", ["/app/SixLabors.ImageSharp.dll"]), ("ReadonlyPaths", ["/alias"]),
                             ("MaskedPaths", ["/proc/self/root/app/SixLabors.ImageSharp.dll"]),
                             ("MaskedPaths", "unknown")):
            for post in (False, True):
                with self.subTest(field=field, post=post):
                    case = self.case()
                    def changed(case, args, result):
                        if case.up == post and args[:3] == ["docker", "container", "inspect"]:
                            data = json.loads(result)
                            data[0]["HostConfig"][field] = value
                            return json.dumps(data).encode()
                        return result
                    with self.assertRaises(ValueError):
                        self.execute(case, observer=changed)
                    self.assertEqual(case.up, post)
                    self.assertEqual((case.lock.parent / "recovery-required").exists(), post)

    def test_pinned_docker29_and_legacy_profiles_complete_real_scanned_transaction(self):
        for observer in (None, docker29):
            with self.subTest(profile="legacy" if observer is None else "docker29"):
                case = self.case()
                self.execute(case, observer=observer, blob_layout=observer is docker29)
                self.assertTrue(case.scanned and case.up)
                self.assertEqual(list(case.lock.parent.iterdir()), [case.lock])
        case = self.case(["/alias"], "secrets")
        self.execute(case, {"alias": symlink("/safe"), "safe": b"ordinary file"}, observer=docker29)
        self.assertTrue(case.up)
        case = self.case(["/data"], "volume")
        self.execute(case, observer=docker29, blob_layout=True)
        self.assertTrue(case.up)
        case = self.case(["/safe-alias"])
        with self.assertRaises(ValueError):
            self.execute(case, {"safe-alias": symlink("app")}, observer=docker29, blob_layout=True)
        self.assertTrue(case.scanned)
        self.assertFalse(case.up)

    def test_version_cross_combinations_unknowns_and_profile_drift_fail_closed(self):
        versions = [(engine, compose, runc) for engine in ("23.0.5", "29.1.3")
                    for compose in ("2.17.3", "2.40.3") for runc in ("1.1.5", "1.3.4")
                    if (engine, compose, runc) not in fixture.deploy.RUNTIME_PROFILES]
        versions += [("29.1.4", "2.40.3", "1.3.4"), ("29.1.3", "2.40.4", "1.3.4"),
                     ("29.1.3", "2.40.3", "1.3.5"), ("v29.1.3", "2.40.3", "1.3.4")]
        for engine, compose, runc in versions:
            with self.subTest(versions=(engine, compose, runc)):
                case = self.case()
                def changed(case, args, result):
                    result = docker29(case, args, result)
                    if args[:2] == ["docker", "version"]:
                        data = json.loads(result); data["Version"] = engine; data["Components"][0]["Version"] = runc
                        return json.dumps(data).encode()
                    if args == ["docker", "compose", "version", "--short"]:
                        return compose.encode()
                    return result
                with self.assertRaises(ValueError):
                    self.execute(case, observer=changed)
                self.assertFalse(case.built or case.up)
        for post in (False, True):
            case = self.case()
            def switch(case, args, result):
                return result if (case.up if post else case.scanned) else docker29(case, args, result)
            with self.assertRaises(ValueError):
                self.execute(case, observer=switch)
            self.assertEqual(case.up, post)
            self.assertEqual((case.lock.parent / "recovery-required").exists(), post)

    def test_docker29_capability_status_is_passive_but_closed_and_typed(self):
        key = "org.opencontainers.runtime-spec.features"
        invalid = [None, [], {"hooks": "custom"}, {key: {}}, {key: "bad JSON"},
            {key: '{"ociVersionMin":"1.0.0","ociVersionMin":"1.2.1","ociVersionMax":"1.2.1"}'},
            {key: json.dumps({**FEATURES_134, "hookPrograms": ["custom"]})},
            {key: json.dumps({**FEATURES_134, "hooks": {"prestart": ["custom"]}})},
            {key: json.dumps({**FEATURES_134, "linux": {"runtimeArgs": ["custom"]}})},
            {key: json.dumps({**FEATURES_134, "linux": {"apparmor": {"enabled": "true"}}})},
            {key: json.dumps({**FEATURES_134, "linux": {"mountExtensions": {"unknown": {}}}})},
            {key: json.dumps({**FEATURES_134, "ociVersionMin": ""})},
            {key: json.dumps({**FEATURES_134, "hooks": ["custom"]})}]
        for status in invalid:
            with self.subTest(status=status):
                case = self.case()
                def changed(case, args, result):
                    result = docker29(case, args, result)
                    if args[:2] == ["docker", "info"]:
                        data = json.loads(result); data["Runtimes"]["runc"]["status"] = status
                        return json.dumps(data).encode()
                    return result
                with self.assertRaises(ValueError):
                    self.execute(case, observer=changed)
                self.assertFalse(case.built or case.up)

    def test_docker29_unknown_execution_settings_and_nonclassic_store_block_before_build(self):
        changes = [{"Runtimes": {"runc": {"path": "runc", "runtimeArgs": ["--custom"]}}},
            {"Runtimes": {"runc": {"path": "runc", "options": {"hooks": "custom"}}}},
            {"Runtimes": {"runc": {"path": "/custom/runc"}}}, {"DefaultRuntime": "custom"},
            {"SecurityOptions": ["name=rootless"]}, {"SecurityOptions": ["name=seccomp,profile=custom"]},
            {"Driver": "overlayfs", "DriverStatus": [["driver-type", "io.containerd.snapshotter.v1"]]},
            {"Driver": "overlay2", "DriverStatus": [["driver-type", "io.containerd.snapshotter.v1"]]},
            {"Driver": "unknown"}, {"DriverStatus": None}, {"DriverStatus": [["ambiguous"]]}]
        for change in changes:
            with self.subTest(change=change):
                case = self.case()
                def changed(case, args, result):
                    result = docker29(case, args, result)
                    if args[:2] == ["docker", "info"]:
                        data = json.loads(result); data.update(change)
                        return json.dumps(data).encode()
                    return result
                with self.assertRaises(ValueError):
                    self.execute(case, observer=changed)
                self.assertFalse(case.built or case.up)

    def test_docker29_exact_masks_optional_cpu_paths_and_nil_defaults_pass(self):
        for masks, readonly in ((OBSERVED_MASKS_29, list(fixture.deploy.READONLY_PATHS)),
                               (OBSERVED_MASKS_29 + ["/sys/devices/system/cpu/cpu0/thermal_throttle",
                                "/sys/devices/system/cpu/cpu127/thermal_throttle"], list(fixture.deploy.READONLY_PATHS)),
                               (None, None)):
            with self.subTest(masks=masks):
                case = self.case()
                def changed(case, args, result):
                    result = docker29(case, args, result)
                    if args[:3] == ["docker", "container", "inspect"]:
                        data = json.loads(result); data[0]["HostConfig"].update(MaskedPaths=masks, ReadonlyPaths=readonly)
                        return json.dumps(data).encode()
                    return result
                self.execute(case, observer=changed)
                self.assertTrue(case.scanned and case.up)

    def test_docker29_custom_ambiguous_masks_or_readonly_fail_before_up_and_keep_post_barrier(self):
        invalid = [("MaskedPaths", OBSERVED_MASKS_29 + [path]) for path in
            ("/app", "/proc/self/root/app", "/proc/custom", "/sys/custom",
             "/sys/devices/system/cpu/cpu00/thermal_throttle", "/sys/devices/system/cpu/cpu-1/thermal_throttle",
             "/sys/devices/system/cpu/cpu0/thermal_throttle/../app", "/sys//devices/system/cpu/cpu0/thermal_throttle")]
        invalid += [("MaskedPaths", OBSERVED_MASKS_29 + [OBSERVED_MASKS_29[0]]),
                    ("MaskedPaths", OBSERVED_MASKS_29 + ["/sys/devices/system/cpu/cpu0/thermal_throttle"] * 2),
                    ("MaskedPaths", OBSERVED_MASKS_29[:-1]), ("MaskedPaths", [1]),
                    ("ReadonlyPaths", list(fixture.deploy.READONLY_PATHS) + ["/sys/custom"]),
                    ("ReadonlyPaths", list(fixture.deploy.READONLY_PATHS) + [fixture.deploy.READONLY_PATHS[0]]),
                    ("ReadonlyPaths", ["/proc/self/root"]), ("ReadonlyPaths", []), ("ReadonlyPaths", "bad")]
        for field, value in invalid:
            for post in (False, True):
                with self.subTest(field=field, post=post, value=value):
                    case = self.case()
                    def changed(case, args, result):
                        result = docker29(case, args, result)
                        if case.up == post and args[:3] == ["docker", "container", "inspect"]:
                            data = json.loads(result); data[0]["HostConfig"][field] = value
                            return json.dumps(data).encode()
                        return result
                    with self.assertRaises(ValueError):
                        self.execute(case, observer=changed)
                    self.assertEqual(case.up, post)
                    self.assertEqual((case.lock.parent / "recovery-required").exists(), post)

    def test_docker29_init_null_and_unexpected_post_runtime_remain_blockers(self):
        for value in (None, True, "missing"):
            for post in (False, True):
                case = self.case()
                def changed(case, args, result):
                    result = docker29(case, args, result)
                    if case.up == post and args[:3] == ["docker", "container", "inspect"]:
                        data = json.loads(result)
                        if value == "missing": del data[0]["HostConfig"]["Init"]
                        else: data[0]["HostConfig"]["Init"] = value
                        return json.dumps(data).encode()
                    return result
                with self.assertRaises(ValueError):
                    self.execute(case, observer=changed)
                self.assertEqual(case.up, post)
                self.assertEqual((case.lock.parent / "recovery-required").exists(), post)
        for value in (None, True, "missing"):
            case = self.case()
            if value == "missing": del case.config["services"]["backend"]["init"]
            else: case.config["services"]["backend"]["init"] = value
            with self.assertRaises(ValueError):
                self.execute(case, observer=docker29)
            self.assertFalse(case.built or case.up)
        case = self.case()
        def changed_runtime(case, args, result):
            result = docker29(case, args, result)
            if case.up and args[:3] == ["docker", "container", "inspect"]:
                data = json.loads(result); data[0]["HostConfig"]["Runtime"] = "custom"
                return json.dumps(data).encode()
            return result
        with self.assertRaises(ValueError):
            self.execute(case, observer=changed_runtime)
        self.assertTrue(case.up and (case.lock.parent / "recovery-required").exists())

    def test_docker29_scan_failure_and_distinct_image_cannot_reuse_build_only_authorization(self):
        for scenario in ("scan-fails", "scan-exception", "wrong-export-iid"):
            with self.subTest(scenario=scenario):
                case = self.case(); case.scenario = scenario
                with self.assertRaises((ValueError, RuntimeError)):
                    self.execute(case, observer=docker29)
                self.assertTrue(case.scanned and case.built)
                self.assertFalse(case.up)
                self.assertEqual(list(case.lock.parent.iterdir()), [case.lock])
        case = self.case()
        iid_a = self.execute(case, build_only=True, observer=docker29, blob_layout=True)
        self.assertTrue(case.scanned)
        self.assertFalse(case.up)
        # Fresh image B really contains deleted historical synthetic disclosure;
        # accepting A does not bypass B's current export/scan/IID verification.
        entries = {"historical.bin": case.license.read_bytes()}
        later = {".wh.historical.bin": b""}
        iid_b, _ = bundle(entries, later, blob_layout=True)
        self.assertNotEqual(iid_a, iid_b)
        case.calls, case.scanned, case.built = [], False, False
        with self.assertRaises(ValueError):
            self.execute(case, entries, later, observer=docker29, blob_layout=True)
        self.assertTrue(case.scanned and case.built)
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
        (root / "sbin").mkdir()
        filesystem = {"app": ("dir", None, None), "app/dll": ("file", None, None), "private": ("dir", None, None)}
        for path, link in (("relative", "app"), ("absolute", "/app"), ("first", "second"),
                           ("second", "app"), ("private/nested", "../app"), ("cycle", "cycle"), ("dangling", "absent")):
            (root / path).symlink_to(link)
            filesystem[path] = ("symlink", link, None)
        fd = os.open(root, os.O_PATH | os.O_DIRECTORY)
        try:
            # The exact init mountpoint (including a relative symlink chain)
            # resolves to the protected file in runc's container-root namespace.
            for required in (fixture.image.DLL, fixture.image.DEPS):
                (root / required).write_text("synthetic protected file")
                filesystem[required] = ("file", None, None)
                for path, link in (("sbin/docker-init", "../init-link"), ("init-link", "/" + required)):
                    (root / path).symlink_to(link)
                    filesystem[path] = ("symlink", link, None)
                how = OpenHow(os.O_PATH, 0, 0x10)
                opened = libc.syscall(437, fd, ctypes.c_char_p(b"sbin/docker-init"), ctypes.byref(how), ctypes.sizeof(how))
                if opened < 0:
                    raise OSError(ctypes.get_errno(), "Linux init mountpoint lookup failed")
                try:
                    assert os.fstat(opened).st_ino == (root / required).stat().st_ino
                    with unittest.TestCase().assertRaises(ValueError):
                        fixture.image.resolve_mount(filesystem, "/sbin/docker-init")
                finally:
                    os.close(opened)
                (root / "sbin/docker-init").unlink()
                (root / "init-link").unlink()
                del filesystem["sbin/docker-init"], filesystem["init-link"]
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
