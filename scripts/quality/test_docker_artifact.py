"""HP-90 assembled build/export/real scanner decisions with synthetic IO only."""
import contextlib
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import validate_docker_artifact as artifact
from test_imagesharp_image import SYNTHETIC_DLL, MARKER
from test_imagesharp_rootfs import bundle
from test_imagesharp_license import PR_REFERENCE
from test_workflows import load

SHA = "a" * 40
BASE = "quality/security-imagesharp-remediation"


def verify_workflow(workflow):
    assert workflow.get("on", workflow.get(True)) == {"pull_request": {"branches": [BASE]}}
    assert workflow["permissions"] == {"contents": "read"}
    assert set(workflow["jobs"]) == {"artifact"}
    job = workflow["jobs"]["artifact"]
    assert set(job) == {"name", "runs-on", "timeout-minutes", "steps"}
    assert job["runs-on"] == "ubuntu-latest" and job["timeout-minutes"] == 40
    steps = job["steps"]
    assert len(steps) == 3
    assert steps[0] == {"uses": "actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803",
                        "with": {"persist-credentials": False}}
    assert steps[1] == {"uses": "actions/setup-python@ece7cb06caefa5fff74198d8649806c4678c61a1",
                        "with": {"python-version": "3.12"}}
    assert set(steps[2]) == {"name", "env", "run"}
    assert steps[2]["env"] == {artifact.SECRET: PR_REFERENCE}
    assert steps[2]["run"] == "python scripts/quality/validate_docker_artifact.py"
    assert "env" not in workflow


class DockerArtifactTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "checkout"
        self.private = Path(self.temp.name) / "runner-temp"
        self.root.mkdir()
        self.private.mkdir()
        self.iid, self.payload = bundle()
        self.calls = []
        self.export_mode = "valid"
        self.inspect_id = self.iid
        self.build_id = self.iid
        pin = patch.object(artifact.scanner, "TRUSTED_DLL_SHA256", hashlib.sha256(SYNTHETIC_DLL).hexdigest())
        pin.start()
        self.addCleanup(pin.stop)

    def run_command(self, args, *, cwd, env):
        self.calls.append(args)
        self.assertEqual(cwd, self.root)
        self.assertNotIn(MARKER, json.dumps(args))
        self.assertNotIn(artifact.SECRET, env)
        if args == ["git", "rev-parse", "HEAD"]:
            return SHA.encode()
        config = Path(env["DOCKER_CONFIG"])
        self.assertFalse(list(config.iterdir()), "No registry authentication configuration")
        if args[:3] == ["docker", "buildx", "create"]:
            self.assertEqual(args[5:], ["--driver", "docker-container"])
        elif args[:3] == ["docker", "buildx", "build"]:
            secret = Path(args[args.index("--secret") + 1].split("src=", 1)[1])
            self.assertEqual(secret.read_text(), MARKER)
            self.assertFalse(secret.is_relative_to(self.root))
            if os.name == "posix":
                self.assertEqual(stat.S_IMODE(secret.stat().st_mode), 0o600)
                self.assertEqual(stat.S_IMODE(secret.parent.stat().st_mode), 0o700)
            iid_file = Path(args[args.index("--iidfile") + 1])
            if self.build_id is not None:
                iid_file.write_text(self.build_id)
            self.assertIn("--load", args)
            self.assertIn("--no-cache", args)
            for forbidden in ("--push", "--output", "--build-arg", "--tag", "--cache-to", "--cache-from"):
                self.assertNotIn(forbidden, args)
            self.assertEqual(args[args.index("--file") + 1], "HockeyPlanner.Backend.WebAPI/Dockerfile.staging")
            self.assertEqual(args[-1], ".")
        elif args[:3] == ["docker", "image", "inspect"]:
            self.assertEqual(args[3], self.build_id)
            return json.dumps([{"Id": self.inspect_id}]).encode()
        elif args[:3] == ["docker", "image", "save"]:
            self.assertEqual(args[-1], self.build_id)
            archive = Path(args[4])
            self.assertFalse(archive.is_relative_to(self.root))
            if self.export_mode != "missing":
                archive.write_bytes(self.payload if self.export_mode == "valid" else b"corrupt")
        elif args[:3] != ["docker", "buildx", "rm"]:
            self.fail("Unsupported or publishing command")
        return b"synthetic discarded diagnostics"

    def validate(self):
        try:
            return artifact.validate(self.root, self.private, SHA, MARKER, self.run_command)
        finally:
            self.assertEqual(list(self.private.iterdir()), [], "Private inputs/exports must be cleaned")

    def test_local_only_exact_iid_export_real_scan_and_cleanup(self):
        with patch.object(artifact.scanner, "inspect", wraps=artifact.scanner.inspect) as scan:
            evidence = self.validate()
        self.assertEqual(evidence, {"status": "PASS", "commit": SHA, "localImageId": self.iid, "layers": 1})
        self.assertEqual(scan.call_args.kwargs, {"expected_image_id": self.iid})
        self.assertEqual([args[:3] for args in self.calls[1:]], [
            ["docker", "buildx", "create"], ["docker", "buildx", "build"],
            ["docker", "image", "inspect"], ["docker", "image", "save"], ["docker", "buildx", "rm"]])
        self.assertNotIn(MARKER, json.dumps(evidence))

    def test_missing_license_and_context_temp_fail_before_docker(self):
        for secret, folder in (("", self.private), (" \n", self.private), (MARKER, self.root)):
            with self.assertRaises(ValueError):
                artifact.validate(self.root, folder, SHA, secret, self.run_command)
        self.assertEqual(self.calls, [])

    def test_missing_malformed_wrong_iid_and_loaded_identity_fail(self):
        for built, observed in ((None, self.iid), ("mutable-tag", self.iid),
                                ("sha256:" + "b" * 64, self.iid), (self.iid, "sha256:" + "c" * 64)):
            with self.subTest(built=built, observed=observed):
                self.calls.clear()
                self.build_id, self.inspect_id = built, observed
                with self.assertRaises((ValueError, OSError)):
                    self.validate()
                self.assertFalse(any(args[:3] == ["docker", "image", "save"] for args in self.calls))

    def test_archive_image_a_cannot_authorize_image_b(self):
        self.build_id = self.inspect_id = "sha256:" + "b" * 64
        with self.assertRaises(ValueError):
            self.validate()  # Export is still actual image A, identity requires B.

    def test_missing_and_corrupt_exports_block_and_cleanup(self):
        for mode in ("missing", "corrupt"):
            self.export_mode = mode
            with self.assertRaises(Exception):
                self.validate()

    def test_real_scanner_rejects_historical_leak_and_wrong_dll(self):
        for entries, later in (({"historical": MARKER.encode()}, {".wh.historical": b""}),
                               ({artifact.scanner.DLL: b"incorrect DLL"}, None)):
            self.iid, self.payload = bundle(entries, later)
            self.build_id = self.inspect_id = self.iid
            with self.assertRaises(ValueError):
                self.validate()

    def test_scanner_exception_and_command_failure_do_not_report_pass(self):
        with patch.object(artifact.scanner, "inspect", side_effect=RuntimeError(MARKER)):
            with self.assertRaises(RuntimeError):
                self.validate()
        def failed(args, **kwargs):
            if args[:3] == ["docker", "buildx", "build"]:
                raise subprocess.CalledProcessError(1, args, output=MARKER, stderr=MARKER)
            return self.run_command(args, **kwargs)
        with self.assertRaises(subprocess.CalledProcessError):
            artifact.validate(self.root, self.private, SHA, MARKER, failed)
        self.assertEqual(list(self.private.iterdir()), [])

    def test_cli_sanitizes_errors_and_only_allowlisted_evidence_is_written(self):
        summary = Path(self.temp.name) / "summary"
        output, errors = io.StringIO(), io.StringIO()
        env = {artifact.SECRET: MARKER, "RUNNER_TEMP": str(self.private), "GITHUB_SHA": SHA,
               "GITHUB_STEP_SUMMARY": str(summary)}
        for failing in (False, True):
            with patch.dict(os.environ, env), patch.object(artifact, "validate") as check, \
                    contextlib.redirect_stdout(output), contextlib.redirect_stderr(errors):
                if failing:
                    check.side_effect = RuntimeError(MARKER)
                else:
                    check.return_value = {"status": "PASS", "commit": SHA, "localImageId": self.iid, "layers": 1}
                self.assertEqual(artifact.main(), int(failing))
                self.assertNotIn(artifact.SECRET, os.environ)
        self.assertEqual(errors.getvalue(), "BLOCKED: licensed local Docker artifact validation failed.\n")
        self.assertNotIn(MARKER, output.getvalue() + errors.getvalue() + summary.read_text())
        self.assertEqual(summary.read_text().count('"status": "PASS"'), 1)


class DockerWorkflowTests(unittest.TestCase):
    def test_ci_only_secret_boundary_and_no_publication(self):
        verify_workflow(load("ghcr-artifact-validation.yml"))

    def test_publishing_deploy_secret_and_skip_mutations_are_rejected(self):
        original = load("ghcr-artifact-validation.yml")
        for mutate in (
                lambda w: w.update(permissions={"contents": "read", "packages": "write"}),
                lambda w: w.get("on", w.get(True)).update(workflow_dispatch=None),
                lambda w: w["jobs"]["artifact"].update({"if": "false"}),
                lambda w: w["jobs"]["artifact"]["steps"].append({"uses": "docker/login-action@v3"}),
                lambda w: w["jobs"]["artifact"]["steps"][-1].update(run="ssh staging deploy"),
                lambda w: w["jobs"]["artifact"]["steps"][-1].update({"continue-on-error": True}),
                lambda w: w["jobs"]["artifact"]["steps"][-1].update(env={artifact.SECRET: "${{ secrets.SIXLABORS_LICENSE_KEY }}"})):
            changed = copy.deepcopy(original)
            mutate(changed)
            with self.assertRaises(AssertionError):
                verify_workflow(changed)

    def test_stacked_and_previous_quality_triggers_keep_mandatory_validation(self):
        quality = load("backend-pr-checks.yml")
        self.assertEqual(quality.get("on", quality.get(True)), {"pull_request": {"branches": ["master", "develop", BASE]}})
        self.assertEqual(quality["jobs"]["validation"]["uses"], "./.github/workflows/validate.yml")
        self.assertNotIn("if", quality["jobs"]["validation"])
        commands = "\n".join(step.get("run", "") for step in load("validate.yml")["jobs"]["validate"]["steps"])
        for required in ("unittest discover", "MigrationReadinessTests", "compare_contract.py", "--logger", "check_test_results.py", "-c Release"):
            self.assertIn(required, commands)


if __name__ == "__main__":
    unittest.main()
