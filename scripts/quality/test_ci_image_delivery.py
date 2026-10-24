"""Synthetic transport/transaction failures; real scanner and archive parsing.

Actual Docker29 import/Compose evidence comes from probe_docker29_delivery.py,
not from the Docker responses in these deterministic tests.
"""
from contextlib import ExitStack, redirect_stdout, redirect_stderr
import copy
import gzip
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

import test_docker_artifact as build_fixture
import test_imagesharp_deploy as deploy_fixture
from test_imagesharp_image import packed, SYNTHETIC_DLL, MARKER
from test_workflows import load, verify

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "staging"))
import image_delivery_identity as identity
import receive_ci_image as receiver
import transaction
import deliver_ci_image as sender
import prepare_ci_image as prepare

SHA = "a" * 40


def docker_archive():
    config = json.loads(deploy_fixture.IMAGE_CONFIG)
    config.update(os="linux", architecture="amd64")
    raw = json.dumps(config, separators=(",", ":")).encode()
    iid = identity.sha256(raw)
    payload = packed({iid[7:] + ".json": raw, "layer/layer.tar": deploy_fixture.LAYER,
                      "manifest.json": json.dumps([{"Config": iid[7:] + ".json", "RepoTags": None,
                                                     "Layers": ["layer/layer.tar"]}]).encode()})
    return iid, payload


def exported(payload, compressed=False):
    with tarfile.open(fileobj=io.BytesIO(payload)) as archive:
        saved = json.load(archive.extractfile("manifest.json"))[0]
        config = archive.extractfile(saved["Config"]).read()
        raw = archive.extractfile(saved["Layers"][0]).read()
    layer = gzip.compress(raw, mtime=0) if compressed else raw
    layer_desc = identity.descriptor(identity.DOCKER_LAYER + (".gzip" if compressed else ""), layer)
    manifest = json.dumps({"schemaVersion": 2, "mediaType": identity.DOCKER_MANIFEST,
                           "config": identity.descriptor(identity.DOCKER_CONFIG, config),
                           "layers": [layer_desc]}, separators=(",", ":")).encode()
    target = identity.descriptor(identity.DOCKER_MANIFEST, manifest)
    config_name, layer_name = "blobs/sha256/" + identity.sha256(config)[7:], "blobs/sha256/" + identity.sha256(layer)[7:]
    files = {config_name: config, layer_name: layer,
             "blobs/sha256/" + target["digest"][7:]: manifest,
             "manifest.json": json.dumps([{"Config": config_name, "RepoTags": None, "Layers": [layer_name]}]).encode(),
             "oci-layout": b'{"imageLayoutVersion":"1.0.0"}',
             "index.json": json.dumps({"schemaVersion": 2, "mediaType": identity.OCI_INDEX, "manifests": [target]}).encode()}
    return target["digest"], packed(files), files


class ArchiveIdentityTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "image.tar"
        self.iid, self.payload = docker_archive()
        self.path.write_bytes(self.payload)

    def test_legacy_and_oci_bind_same_config_and_ordered_diffids(self):
        source = identity.inspect_archive(self.path, self.iid)
        for compressed in (False, True):
            loaded, payload, _ = exported(self.payload, compressed)
            self.path.write_bytes(payload)
            proof = identity.inspect_archive(self.path, self.iid, loaded_id=loaded, expected_diff_ids=source.diff_ids)
            self.assertEqual(proof.config_id, source.config_id)
            self.assertEqual(proof.diff_ids, source.diff_ids)
            self.assertEqual(proof.imported_id, loaded)
        self.assertNotEqual(loaded, source.imported_id, "Compressed representation is a DIFFERENT manifest ID")

    def test_source_compression_wrong_config_or_loaded_manifest_are_rejected(self):
        loaded, payload, _ = exported(self.payload, True)
        self.path.write_bytes(payload)
        for options in ({}, {"loaded_id": "sha256:" + "b" * 64},
                        {"loaded_id": loaded, "expected_diff_ids": ("sha256:" + "c" * 64,)}):
            with self.assertRaises(ValueError):
                identity.inspect_archive(self.path, self.iid, **options)
        with self.assertRaises(ValueError):
            identity.inspect_archive(self.path, "sha256:" + "d" * 64, loaded_id=loaded)

    def test_alternate_oci_graph_tags_duplicates_and_multi_manifest_fail_closed(self):
        loaded, _, files = exported(self.payload)
        for mutation in ("config", "tag", "index", "duplicate", "alias"):
            changed = dict(files)
            if mutation == "config":
                key = "blobs/sha256/" + loaded[7:]
                manifest = json.loads(changed[key]); manifest["config"]["digest"] = "sha256:" + "e" * 64
                changed[key] = json.dumps(manifest).encode()
            elif mutation == "tag":
                manifest = json.loads(changed["manifest.json"]); manifest[0]["RepoTags"] = ["existing-backend:latest"]
                changed["manifest.json"] = json.dumps(manifest).encode()
            elif mutation == "index":
                index = json.loads(changed["index.json"]); index["manifests"] *= 2
                changed["index.json"] = json.dumps(index).encode()
            elif mutation == "alias":
                changed["./oci-layout"] = changed.pop("oci-layout")
            else:
                changed["oci-layout"] = b'{"imageLayoutVersion":"1.0.0","imageLayoutVersion":"1.0.0"}'
            self.path.write_bytes(packed(changed))
            with self.assertRaises(ValueError):
                identity.inspect_archive(self.path, self.iid)

    def test_native_inspect_drift_and_unsupported_store_are_rejected(self):
        proof = identity.inspect_archive(self.path, self.iid)
        data = [{"Id": proof.imported_id, "Descriptor": {"digest": proof.imported_id, "mediaType": identity.DOCKER_MANIFEST},
                 "RootFS": {"Type": "layers", "Layers": list(proof.diff_ids)}, "Os": "linux", "Architecture": "amd64"}]
        proof.verify_inspect(data)
        for field, value in (("Id", self.iid), ("RootFS", {"Type": "layers", "Layers": []}), ("Os", "windows")):
            changed = copy.deepcopy(data); changed[0][field] = value
            with self.assertRaises(ValueError):
                proof.verify_inspect(changed)


class ScannedArchiveTests(unittest.TestCase):
    def test_callback_receives_only_scanned_bytes_after_builder_cleanup_and_license_removal(self):
        case = build_fixture.DockerArtifactTests()
        case.setUp(); self.addCleanup(case.doCleanups)
        case.iid, case.payload = docker_archive()
        case.inspect_id = case.build_id = case.iid
        calls = []
        def consume(path, evidence):
            self.assertEqual(case.calls[-1][:3], ["docker", "buildx", "rm"])
            self.assertFalse((path.parent / "sixlabors.lic").exists())
            self.assertEqual(path.read_bytes(), case.payload)
            self.assertEqual(evidence["archiveSHA256"], hashlib.sha256(case.payload).hexdigest())
            private = prepare.preserve(path, evidence, Path(case.temp.name))
            self.assertEqual((private / "image.tar").read_bytes(), case.payload)
            self.assertEqual(json.loads((private / "verified.json").read_bytes()), evidence)
            calls.append(evidence)
        build_fixture.artifact.validate(case.root, case.private, SHA, MARKER, case.run_command, on_verified=consume)
        self.assertEqual(len(calls), 1)
        self.assertEqual(sum(args[:3] == ["docker", "buildx", "build"] for args in case.calls), 1)
        self.assertEqual(list(case.private.iterdir()), [])

    def test_scan_error_never_makes_an_archive_available(self):
        case = build_fixture.DockerArtifactTests(); case.setUp(); self.addCleanup(case.doCleanups)
        case.export_mode = "corrupt"
        with patch.object(prepare, "preserve") as preserve:
            with self.assertRaises(Exception):
                build_fixture.artifact.validate(case.root, case.private, SHA, MARKER, case.run_command,
                    on_verified=lambda path, evidence: preserve(path, evidence, case.private))
            preserve.assert_not_called()


class PrivateTransportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "image.tar"
        self.iid, payload = docker_archive(); self.path.write_bytes(payload)
        self.evidence = {"status": "PASS", "commit": SHA, "localImageId": self.iid,
                         "archiveSHA256": hashlib.sha256(payload).hexdigest(), "archiveSize": len(payload)}
        self.credentials = dict(zip(sender.NAMES, ("staging.example.test", "deploy", "SYNTHETIC PRIVATE KEY",
                                                  "staging.example.test ssh-ed25519 SYNTHETIC PUBLIC PIN")))
        self.calls = []
        self.result = {"status": "PASS", "commit": SHA, "configImageId": self.iid,
                       "archiveSHA256": self.evidence["archiveSHA256"], "importedImageId": "sha256:" + "b" * 64}

    def command(self, args, **kwargs):
        self.calls.append(args)
        self.assertNotIn("SYNTHETIC PRIVATE KEY", json.dumps(args))
        self.assertFalse(set(sender.NAMES) & set(kwargs["env"]))
        self.assertNotIn(build_fixture.artifact.SECRET, kwargs["env"])
        if args[0] == "ssh-keygen":
            return subprocess.CompletedProcess(args, 0, stdout=b"synthetic trusted pin")
        self.assertEqual(kwargs["stdin"].read(), self.path.read_bytes())
        self.assertIn("StrictHostKeyChecking=yes", args)
        self.assertIn("GlobalKnownHostsFile=/dev/null", args)
        self.assertIn("IdentityAgent=none", args)
        self.assertEqual(args[1:4], ["-F", "none", "-T"])
        self.assertIn(sender.RECEIVER, args)
        return subprocess.CompletedProcess(args, 0, stdout=json.dumps(self.result).encode())

    def test_pinned_fixed_command_transfers_exact_archive_without_secret_environment(self):
        with patch.object(sender.sys, "platform", "linux"):
            self.assertEqual(sender.transport(self.path, self.evidence, self.credentials, self.command), self.result)
        self.assertEqual([args[0] for args in self.calls], ["ssh-keygen", "ssh"])
        self.assertEqual([item.name for item in self.path.parent.iterdir()], ["image.tar"])

    def test_wrong_missing_hash_and_modified_archive_never_start_ssh(self):
        for key, value in (("archiveSHA256", ""), ("archiveSHA256", "d" * 64), ("archiveSize", 1), ("commit", "wrong")):
            bad = {**self.evidence, key: value}
            with patch.object(sender.sys, "platform", "linux"), self.assertRaises(ValueError):
                sender.transport(self.path, bad, self.credentials, self.command)
        self.assertEqual(self.calls, [])

    def test_missing_pin_host_mismatch_and_remote_rejection_are_fail_closed(self):
        for name in sender.NAMES:
            bad = {**self.credentials, name: ""}
            with patch.object(sender.sys, "platform", "linux"), self.assertRaises(ValueError):
                sender.transport(self.path, self.evidence, bad, self.command)
        def failed_pin(args, **kwargs):
            self.assertEqual(args[0], "ssh-keygen")
            raise subprocess.CalledProcessError(1, args, stderr=b"synthetic discarded pin error")
        with patch.object(sender.sys, "platform", "linux"), self.assertRaises(subprocess.CalledProcessError):
            sender.transport(self.path, self.evidence, self.credentials, failed_pin)
        def mismatch(args, **kwargs):
            if args[0] == "ssh":
                raise subprocess.CalledProcessError(255, args, stderr=b"SYNTHETIC PRIVATE KEY host key mismatch")
            return self.command(args, **kwargs)
        with patch.object(sender.sys, "platform", "linux"), self.assertRaises(subprocess.CalledProcessError):
            sender.transport(self.path, self.evidence, self.credentials, mismatch)

    def test_pr_task_push_and_missing_approval_never_build_transfer_or_log_secrets(self):
        for event, ref, approved in (("pull_request", "refs/pull/26/merge", SHA),
                                     ("push", "refs/heads/quality/ci-ssh-image-delivery", SHA),
                                     ("push", "refs/heads/develop", "")):
            stdout, stderr = io.StringIO(), io.StringIO()
            with patch.dict(os.environ, {**self.credentials, "GITHUB_EVENT_NAME": event, "GITHUB_REF": ref,
                "GITHUB_SHA": SHA, "GITHUB_REPOSITORY": "tracktorist237/HockeyPlanner.Backend",
                "HP92_APPROVED_HEAD": approved, "SIXLABORS_LICENSE_KEY": MARKER}), \
                patch.object(sender, "transport") as transport, redirect_stdout(stdout), redirect_stderr(stderr):
                self.assertEqual(sender.main(), 1)
                transport.assert_not_called()
                self.assertFalse(set(sender.NAMES) & set(os.environ))
                self.assertNotIn("SIXLABORS_LICENSE_KEY", os.environ)
            self.assertNotIn(MARKER, stdout.getvalue() + stderr.getvalue())
            self.assertNotIn("SYNTHETIC PRIVATE KEY", stdout.getvalue() + stderr.getvalue())


class ReceiptTests(unittest.TestCase):
    def test_verified_receipt_truncation_extra_bytes_hash_and_collision(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "received.tar"
            for data, size, digest in ((b"abc", 4, hashlib.sha256(b"abc").hexdigest()),
                                      (b"abcd", 3, hashlib.sha256(b"abc").hexdigest()),
                                      (b"abc", 3, "d" * 64), (b"abc", 3, "")):
                with self.assertRaises(ValueError):
                    receiver.receive(io.BytesIO(data), path, size, digest)
                if path.exists():
                    os.chmod(path, 0o600); path.unlink()
            receiver.receive(io.BytesIO(b"abc"), path, 3, hashlib.sha256(b"abc").hexdigest())
            self.assertEqual(path.read_bytes(), b"abc")
            with self.assertRaises(FileExistsError):
                receiver.receive(io.BytesIO(b"def"), path, 3, hashlib.sha256(b"def").hexdigest())
            self.assertEqual(path.read_bytes(), b"abc")


class ReceivedTransactionTests(unittest.TestCase):
    def setUp(self):
        self.case = deploy_fixture.DeployTransactionTests(); self.case.setUp(); self.addCleanup(self.case.doCleanups)
        self.iid, self.payload = docker_archive()
        self.loaded_id, self.reexport, _ = exported(self.payload, compressed=True)
        self.case.config["services"]["backend"].pop("build")
        self.case.config["services"]["backend"]["environment"]["APP_COMMIT"] = SHA
        self.case.config.pop("secrets")
        (self.case.root / "compose.ci-image.yml").write_text("synthetic image-only override")
        os.chmod(self.case.root / "compose.ci-image.yml", 0o600)
        os.chmod(self.case.lock.parent, 0o700); os.chmod(self.case.lock, 0o600)
        self.approved = self.case.lock.parent / "hp92-operator-approval.json"
        self.approved.write_text(json.dumps({"protocol": receiver.PROTOCOL, "sourceSha": SHA,
            "allWritersCooperate": True, "frontendCooperates": True, "ownerApprovedTransferLoadDeploy": True}))
        os.chmod(self.approved, 0o600)
        self.fd = os.open(self.case.lock, os.O_RDWR); self.addCleanup(os.close, self.fd)
        self.scenario = "success"

    def command(self, args, cwd=None):
        case = self.case
        if args[0] == "git":
            self.assertEqual(args[:len(receiver.GIT)], receiver.GIT)
            self.assertIn("core.fsmonitor=false", args)
            args = ["git", *args[len(receiver.GIT):]]
        if args[:3] == ["docker", "image", "load"]:
            case.calls.append(args); case.built = True
            if self.scenario == "cancel":
                raise transaction.Cancelled()
            if self.scenario == "configuration-drift":
                (case.root / "compose.ci-image.yml").write_text("drift during import")
            return ("Loaded image ID: " + self.loaded_id).encode()
        if args[:3] == ["docker", "image", "save"]:
            case.calls.append(args)
            Path(args[args.index("--output") + 1]).write_bytes(self.reexport)
            return b""
        if args[:3] == ["docker", "image", "inspect"]:
            case.calls.append(args)
            with tarfile.open(fileobj=io.BytesIO(self.payload)) as archive:
                saved = json.load(archive.extractfile("manifest.json"))[0]
                config = json.load(archive.extractfile(saved["Config"]))
            return json.dumps([{"Id": self.loaded_id, "Descriptor": {"digest": self.loaded_id, "mediaType": identity.DOCKER_MANIFEST},
                "Config": config["config"], "Os": "linux", "Architecture": "amd64",
                "RootFS": {"Type": "layers", "Layers": config["rootfs"]["diff_ids"]}}]).encode()
        if args[:2] == ["docker", "compose"] and "up" in args:
            case.calls.append(args); case.up = True
            config = json.loads(Path(args[args.index("-f") + 1]).read_bytes())
            self.assertEqual(config["services"]["backend"]["image"], self.loaded_id)
            self.assertIn("--no-build", args); self.assertEqual(args[-3:], ["--pull", "never", "backend"])
            return b""
        result = case.command(args, cwd)
        if args[:2] == ["docker", "version"]:
            data = json.loads(result); data["Version"] = "29.1.3"
            data["Components"] = [{"Name": "runc", "Version": "1.3.4"}, {"Name": "containerd", "Version": "v2.2.0"}]
            return json.dumps(data).encode()
        if args == ["docker", "compose", "version", "--short"]:
            return b"2.40.3"
        if args[:2] == ["docker", "info"]:
            data = json.loads(result); data["Driver"] = "overlayfs"
            data["DriverStatus"] = [["driver-type", "io.containerd.snapshotter.v1"]]
            if self.scenario == "unsupported-store":
                data["Driver"] = "unknown"
            return json.dumps(data).encode()
        if args[:3] == ["docker", "container", "inspect"]:
            data = json.loads(result)
            data[0]["HostConfig"]["MaskedPaths"] = list(receiver.deploy.MASKED_PATHS_29)
            if self.scenario == "init-null":
                data[0]["HostConfig"]["Init"] = None
            if case.up:
                data[0]["Image"] = "sha256:" + "e" * 64 if self.scenario == "post-wrong-id" else self.loaded_id
                data[0]["Config"]["Image"] = self.loaded_id
            return json.dumps(data).encode()
        if args[:3] == ["git", "rev-parse", "HEAD"] and self.scenario == "wrong-source":
            return b"b" * 40
        return result

    def invoke(self, payload=None, digest=None):
        stream = io.BytesIO(self.payload if payload is None else payload)
        with ExitStack() as stack:
            stack.enter_context(patch.object(receiver.ReadOnly, "run", lambda owner, args, cwd=None: self.command(args, cwd)))
            stack.enter_context(patch.object(transaction.Transaction, "run", lambda owner, args, cwd=None, **kw: self.command(args, cwd)))
            if os.name == "nt":
                # Existing native lifetime tests delegate real POSIX lock/process
                # semantics to WSL; these cases model metadata on Windows only.
                stack.enter_context(patch.object(receiver.deploy, "protected", lambda path, mode: receiver.require(path.exists() and not path.is_symlink())))
                stack.enter_context(patch.object(receiver.deploy, "held_lock", lambda fd, path: receiver.require(path.exists())))
            return receiver.deliver_locked(stream, SHA, digest or hashlib.sha256(self.payload).hexdigest(),
                                          len(self.payload), self.iid, self.fd, self.case.root, self.case.lock)

    def test_scanned_content_survives_different_containerd_manifest_and_compose_binding(self):
        result = self.invoke()
        self.assertEqual(result["configImageId"], self.iid)
        self.assertEqual(result["importedImageId"], self.loaded_id)
        self.assertNotEqual(self.iid, self.loaded_id)
        self.assertTrue(self.case.up)
        self.assertFalse((self.case.lock.parent / "recovery-required").exists())
        self.assertFalse(any("buildx" in args for args in self.case.calls))

    def test_preflight_init_null_unsupported_store_wrong_source_and_missing_lock(self):
        for scenario in ("init-null", "unsupported-store", "wrong-source"):
            self.scenario = scenario; self.case.calls.clear()
            with self.assertRaises(ValueError):
                self.invoke()
            self.assertFalse(any(args[:3] == ["docker", "image", "load"] for args in self.case.calls))
            self.assertFalse((self.case.lock.parent / "recovery-required").exists())
        # Use an absent path without unlinking an inode held by another writer.
        with patch.object(receiver.deploy, "held_lock", side_effect=FileNotFoundError("synthetic missing lock")):
            with self.assertRaises((ValueError, OSError)):
                self.invoke()

    def test_all_writers_frontend_and_owner_approval_are_mandatory_before_receipt(self):
        approved = json.loads(self.approved.read_text())
        for field in ("allWritersCooperate", "frontendCooperates", "ownerApprovedTransferLoadDeploy"):
            for invalid in (False, 1, "true", None):
                self.approved.write_text(json.dumps({**approved, field: invalid}))
                with self.assertRaises(ValueError):
                    self.invoke()
                self.assertFalse(self.case.up)
        self.approved.unlink()
        with self.assertRaises((ValueError, OSError)):
            self.invoke()

    def test_truncated_or_wrong_hash_never_loads_and_retains_uncertain_receipt(self):
        with self.assertRaises(ValueError):
            self.invoke(payload=self.payload[:-1])
        self.assertTrue((self.case.lock.parent / "recovery-required").exists())
        self.assertFalse(any(args[:3] == ["docker", "image", "load"] for args in self.case.calls))

    def test_unexpected_import_id_blocks_before_up_and_retains_recovery(self):
        self.loaded_id = "sha256:" + "e" * 64
        with self.assertRaises(ValueError):
            self.invoke()
        self.assertFalse(self.case.up)
        self.assertTrue((self.case.lock.parent / "recovery-required").exists())

    def test_configuration_drift_and_container_conflict_block_before_replacement(self):
        self.scenario = "configuration-drift"
        with self.assertRaises(ValueError):
            self.invoke()
        self.assertFalse(self.case.up)
        self.scenario = "success"
        self.case.built = False
        self.case.scenario = "container-change"
        with self.assertRaises(ValueError):
            self.invoke()
        self.assertFalse(self.case.up)

    def test_cancellation_and_unverified_postconditions_never_remove_recovery(self):
        self.scenario = "cancel"
        with self.assertRaises(transaction.Cancelled):
            self.invoke()
        self.assertTrue((self.case.lock.parent / "recovery-required").exists())

    def test_wrong_image_after_up_is_reported_without_automatic_rollback(self):
        self.scenario = "post-wrong-id"
        with self.assertRaises(ValueError):
            self.invoke()
        self.assertTrue(self.case.up)
        self.assertTrue((self.case.lock.parent / "recovery-required").exists())
        self.assertFalse(any("stop" in args or "rm" in args for args in self.case.calls))


if __name__ == "__main__":
    unittest.main()
