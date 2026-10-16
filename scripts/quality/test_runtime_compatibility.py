"""Deterministic admission regressions; native evidence is the mandatory CI probe."""
from contextlib import redirect_stderr
import copy
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import test_ci_image_delivery as delivery
import test_docker_artifact as build_fixture
import probe_docker29_delivery as probe

deploy = delivery.receiver.deploy
COMPOSE = "2.40.3+ds1-0ubuntu1~24.04.1"
PACKAGES = (b"docker.io=29.1.3-0ubuntu3~24.04.2\n"
            b"containerd=2.2.1-0ubuntu1~24.04.3\n"
            b"runc=1.3.4-0ubuntu1~24.04.1\n"
            b"docker-compose-v2=2.40.3+ds1-0ubuntu1~24.04.1\n")


def ubuntu():
    # Reduced public metadata observed from checksum-verified Ubuntu .debs.
    # Kernel/build timestamps are not binary attestations or version aliases.
    server = {"Os": "linux", "Arch": "amd64", "Version": "29.1.3",
              "GitCommit": "29.1.3-0ubuntu3~24.04.2", "Components": [
        {"Name": "Engine", "Version": "29.1.3", "Details": {"GitCommit": "29.1.3-0ubuntu3~24.04.2"}},
        {"Name": "containerd", "Version": "2.2.1", "Details": {"GitCommit": ""}},
        {"Name": "runc", "Version": "1.3.4-0ubuntu1~24.04.1", "Details": {"GitCommit": ""}},
        {"Name": "docker-init", "Version": "0.19.0", "Details": {"GitCommit": ""}}]}
    info = {"OSType": "linux", "DefaultRuntime": "runc", "Driver": "overlayfs",
            "DriverStatus": [["driver-type", "io.containerd.snapshotter.v1"]],
            "Runtimes": {"runc": {"path": "runc", "runtimeArgs": []}},
            "SecurityOptions": ["name=seccomp,profile=builtin"]}
    return server, COMPOSE, info, PACKAGES


def admit(server, compose, info, packages, delivered=True):
    return deploy.runtime_profile(server, compose, info, delivered_image=delivered, packages=packages)


class RuntimeCompatibilityTests(unittest.TestCase):
    def test_original_upstream_profile_and_exact_ubuntu_profile_pass(self):
        actual = admit(*ubuntu())
        self.assertEqual(actual.name, "ubuntu-noble-docker29")
        server, _, info, _ = ubuntu()
        server["Components"][1]["Version"] = "v2.2.0"
        server["Components"][2]["Version"] = "1.3.4"
        original = admit(server, "2.40.3", info, None)
        self.assertEqual(original.name, "docker29")
        self.assertEqual(actual.masked_paths, original.masked_paths)
        with self.assertRaises(ValueError):
            admit(*ubuntu(), delivered=False)  # No new VPS-local build path.

    def test_unknown_versions_and_cross_combinations_are_rejected(self):
        for field, values in (("engine", ("29.1.4", "29.2.0", "v29.1.3")),
                              ("containerd", ("v2.2.1", "2.2.0", "2.2.2", "2.2.1-custom")),
                              ("runc", ("1.3.4", "1.3.5", "1.3.4-0ubuntu1~24.04.2")),
                              ("compose", ("2.40.3", "v" + COMPOSE, COMPOSE + "-custom", "2.40.4"))):
            for value in values:
                with self.subTest(field=field, value=value):
                    server, compose, info, packages = ubuntu()
                    if field == "engine": server["Version"] = value
                    elif field == "compose": compose = value
                    else: next(c for c in server["Components"] if c["Name"] == field)["Version"] = value
                    with self.assertRaises(ValueError): admit(server, compose, info, packages)

    def test_exact_package_revisions_and_engine_commit_are_mandatory(self):
        for packages in (None, b"", PACKAGES.replace(b"24.04.3", b"24.04.4"),
                         PACKAGES + PACKAGES, PACKAGES.replace(b"docker.io=", b"docker-ce=")):
            server, compose, info, _ = ubuntu()
            with self.assertRaises(ValueError): admit(server, compose, info, packages)
        for mutation in ("commit", "component-commit", "missing-engine", "nonempty-revision", "arch"):
            server, compose, info, packages = ubuntu()
            if mutation == "commit": server.pop("GitCommit")
            elif mutation == "component-commit": server["Components"][0]["Details"]["GitCommit"] = "unknown"
            elif mutation == "missing-engine": server["Components"].pop(0)
            elif mutation == "nonempty-revision": server["Components"][1]["Details"]["GitCommit"] = "unknown"
            else: server["Arch"] = "arm64"
            with self.assertRaises(ValueError): admit(server, compose, info, packages)

    def test_missing_malformed_and_duplicate_components_are_rejected(self):
        for components in (None, {}, [], [None], [{"Name": "runc"}],
                           [{"Name": "runc", "Version": None}]):
            server, compose, info, packages = ubuntu(); server["Components"] = components
            with self.assertRaises(ValueError): admit(server, compose, info, packages)
        server, compose, info, packages = ubuntu()
        server["Components"].append(copy.deepcopy(server["Components"][1]))
        with self.assertRaises(ValueError): admit(server, compose, info, packages)

    def test_unknown_store_and_active_runtime_are_rejected(self):
        for field, value in (("Driver", "overlay2"), ("DriverStatus", []),
                             ("DefaultRuntime", "custom"), ("SecurityOptions", ["name=rootless"]),
                             ("Runtimes", {"runc": {"path": "/custom/runc"}}),
                             ("Runtimes", {"runc": {"path": "runc", "runtimeArgs": ["--custom"]}}),
                             ("Runtimes", {"runc": {"path": "runc", "status": {"hooks": []}}})):
            server, compose, info, packages = ubuntu(); info[field] = value
            with self.assertRaises(ValueError): admit(server, compose, info, packages)

    def test_only_explicit_init_false_is_allowed(self):
        for init in (None, True, 0, 1, "false", False):
            container = {"HostConfig": {"Init": init, "Runtime": "runc", "Privileged": False}}
            if init is False:
                deploy.container_runtime(container, admit(*ubuntu()))
            else:
                with self.assertRaises(ValueError): deploy.container_runtime(container, admit(*ubuntu()))

    def test_forged_compatibility_evidence_does_not_authorize_unknown_runtime(self):
        server, compose, info, packages = ubuntu()
        server["Components"][1]["Version"] = "2.2.2"
        server["compatibilityProof"] = info["compatibilityProof"] = "PASS"
        with patch.dict(os.environ, {"HP92_RUNTIME_COMPATIBLE": "true"}), self.assertRaises(ValueError):
            admit(server, compose, info, packages)

    def test_receiver_allows_only_the_fixed_read_only_package_query(self):
        reader = delivery.receiver.ReadOnly(123)
        with patch.object(delivery.receiver.subprocess, "run", return_value=subprocess.CompletedProcess(
                deploy.UBUNTU_PACKAGE_QUERY, 0, stdout=PACKAGES)) as run:
            self.assertEqual(reader.run(deploy.UBUNTU_PACKAGE_QUERY), PACKAGES)
            self.assertEqual(run.call_args.args[0], deploy.UBUNTU_PACKAGE_QUERY)
            for command in (["dpkg-query", "--show", "*"], deploy.UBUNTU_PACKAGE_QUERY + ["openssh-server"]):
                with self.assertRaises(ValueError): reader.run(command)
            self.assertEqual(run.call_count, 1)

    def test_ubuntu_transaction_rechecks_identity_init_and_runtime_drift(self):
        for scenario in ("success", "init-null", "init-true", "wrong-import", "runtime-after-up"):
            with self.subTest(scenario=scenario):
                case = delivery.ReceivedTransactionTests(); case.setUp(); self.addCleanup(case.doCleanups)
                original = case.command
                if scenario == "init-null": case.scenario = "init-null"
                if scenario == "wrong-import": case.loaded_id = "sha256:" + "e" * 64
                def command(args, cwd=None):
                    if args == deploy.UBUNTU_PACKAGE_QUERY: return PACKAGES
                    result = original(args, cwd)
                    if args[:2] == ["docker", "version"]:
                        server = ubuntu()[0]
                        if scenario == "runtime-after-up" and case.case.up:
                            server["Components"][1]["Version"] = "2.2.2"
                        return json.dumps(server).encode()
                    if args == ["docker", "compose", "version", "--short"]: return COMPOSE.encode()
                    if args[:3] == ["docker", "container", "inspect"] and scenario == "init-true":
                        data = json.loads(result); data[0]["HostConfig"]["Init"] = True
                        return json.dumps(data).encode()
                    return result
                case.command = command
                if scenario == "success":
                    self.assertEqual(case.invoke()["importedImageId"], case.loaded_id)
                else:
                    with self.assertRaises(ValueError): case.invoke()
                self.assertEqual(case.case.up, scenario in ("success", "runtime-after-up"))
                if scenario in ("init-null", "init-true"):
                    self.assertFalse(any(args[:3] == ["docker", "image", "load"] for args in case.case.calls))
                if scenario in ("wrong-import", "runtime-after-up"):
                    self.assertTrue((case.case.lock.parent / "recovery-required").exists())


class NativeProofFailureTests(unittest.TestCase):
    def test_native_import_and_export_failures_propagate_and_clean_only_owned_sandbox(self):
        cid, loaded = "a" * 64, "sha256:" + "b" * 64
        server, short, info, _ = ubuntu()
        for failure in ("load", "save"):
            calls = []
            def run(args):
                calls.append(args)
                if args[:2] == ["docker", "run"]: return cid.encode()
                if args[:2] == ["docker", "cp"] or args[:2] == ["docker", "rm"]: return b""
                command = args[3:]
                if command[:2] == ["docker", "version"]: return json.dumps(server).encode()
                if command == ["docker", "compose", "version", "--short"]: return short.encode()
                if command[:2] == ["docker", "info"]: return json.dumps(info).encode()
                if command == deploy.UBUNTU_PACKAGE_QUERY: return PACKAGES
                if command == ["runc", "features"]:
                    return b'{"ociVersionMin":"1.0.0","ociVersionMax":"1.2.1"}'
                if command[0] == "sha256sum": return b"hash /image.tar"
                if command[:3] == ["docker", "image", failure]:
                    raise subprocess.CalledProcessError(1, args)
                if command[:3] == ["docker", "image", "load"]: return ("Loaded image ID: " + loaded).encode()
                self.fail("Unexpected native command: " + str(command))
            with patch.object(probe, "run", run), self.assertRaises(subprocess.CalledProcessError):
                probe.prove_store(Path("synthetic"), None, Path("private"), None, "isolated-image",
                    ["docker", "compose"], "ubuntu-noble-docker29", False, "hash")
            self.assertEqual(calls[-1], ["docker", "rm", "--force", "--volumes", cid])
            self.assertFalse(any("create" in call for call in calls))

    def test_missing_or_corrupt_export_cannot_start_compatibility_proof(self):
        for mode in ("missing", "corrupt"):
            case = build_fixture.DockerArtifactTests(); case.setUp(); self.addCleanup(case.doCleanups)
            case.export_mode = mode
            with patch.object(probe, "prove") as prove, self.assertRaises(Exception):
                build_fixture.artifact.validate(case.root, case.private, delivery.SHA,
                    build_fixture.MARKER, case.run_command, on_verified=prove)
            prove.assert_not_called()

    def test_bad_download_cannot_build_a_test_daemon(self):
        with tempfile.TemporaryDirectory() as folder:
            with patch.object(probe.urllib.request, "urlopen", return_value=io.BytesIO(b"altered package")):
                with self.assertRaises(ValueError):
                    probe.download("https://example.test/package", Path(folder) / "bad.deb", "a" * 64, 15)
            with patch.object(probe, "download", side_effect=ValueError("bad checksum")), \
                    patch.object(probe, "run") as run, self.assertRaises(ValueError):
                probe.ubuntu_image(Path(folder))
            run.assert_not_called()

    def test_missing_real_proofs_cannot_emit_pass_even_with_claimed_ci_evidence(self):
        stderr = io.StringIO()
        with patch.dict(os.environ, {"RUNNER_TEMP": "synthetic", "GITHUB_SHA": delivery.SHA,
                "SIXLABORS_LICENSE_KEY": "SYNTHETIC", "HP92_RUNTIME_COMPATIBLE": "PASS"}), \
                patch.object(probe.artifact, "validate", return_value={"status": "PASS"}), \
                redirect_stderr(stderr):
            self.assertEqual(probe.main(), 1)
        self.assertNotIn("SYNTHETIC", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
