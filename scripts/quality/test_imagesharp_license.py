"""Narrow build-secret boundary regressions; no real license or remote IO."""
import copy
import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest
import xml.etree.ElementTree as ET

import yaml

ROOT = Path(__file__).resolve().parents[2]
SECRET = "SIXLABORS_LICENSE_KEY"
REFERENCE = "${{ secrets.SIXLABORS_LICENSE_KEY }}"
PR_REFERENCE = "${{ github.event.pull_request.head.repo.full_name == github.repository && secrets.SIXLABORS_LICENSE_KEY || '' }}"


def load(name):
    return yaml.safe_load((ROOT / ".github/workflows" / name).read_text(encoding="utf-8"))


def verify(validation, pr, staging):
    events = validation.get("on", validation.get(True))
    assert events["workflow_call"]["secrets"] == {SECRET: {"required": True}}
    for caller in (pr, staging):
        assert "pull_request_target" not in caller.get("on", caller.get(True))
        assert caller["jobs"]["validation"]["uses"] == "./.github/workflows/validate.yml"
        assert set(caller["jobs"]["validation"]["secrets"]) == {SECRET}
        assert caller["permissions"] == {"contents": "read"}
    assert pr["jobs"]["validation"]["secrets"][SECRET] == PR_REFERENCE
    assert staging["jobs"]["validation"]["secrets"][SECRET] == REFERENCE
    # A fork must still run mandatory checks and fail closed when no license exists.
    assert "if" not in pr["jobs"]["validation"]
    assert "if" not in validation["jobs"]["validate"]
    assert "env" not in validation and "env" not in validation["jobs"]["validate"]
    steps = validation["jobs"]["validate"]["steps"]
    release = next(s for s in steps if s.get("name") == "Release build")
    assert release["env"] == {SECRET: REFERENCE}
    assert "if" not in release and not release.get("continue-on-error", False)
    assert 'if [ -z "$SIXLABORS_LICENSE_KEY" ]; then' in release["run"]
    assert "exit 1" in release["run"]
    assert release["run"].rstrip().endswith("dotnet build HockeyPlanner.Backend.sln -c Release --no-restore")
    assert "-p:" not in release["run"] and "/p:" not in release["run"]
    assert "set -x" not in release["run"] and "binlog" not in json.dumps(validation).lower()
    for step in steps:
        if step is not release:
            assert "secrets." not in json.dumps(step)
            assert SECRET not in json.dumps(step)
    # Passing a build license must not also expose it to staging SSH, smoke or artifacts.
    for job_name, job in staging["jobs"].items():
        if job_name != "validation":
            assert SECRET not in json.dumps(job)
    assert "secrets." not in json.dumps(load("security-audit.yml"))


class ImageSharpLicenseBoundaryTests(unittest.TestCase):
    def test_only_release_gets_explicit_secret_and_forks_are_not_bypassed(self):
        verify(load("validate.yml"), load("backend-pr-checks.yml"), load("deploy-staging-vps.yml"))

    def test_secret_expansion_and_bypass_mutations_are_rejected(self):
        original = load("validate.yml"), load("backend-pr-checks.yml"), load("deploy-staging-vps.yml")
        mutations = [
            lambda v, p, s: p["jobs"]["validation"].update(secrets="inherit"),
            lambda v, p, s: p["jobs"]["validation"]["secrets"].update(VPS_SSH_KEY="${{ secrets.VPS_SSH_KEY }}"),
            lambda v, p, s: p["jobs"]["validation"]["secrets"].update(SIXLABORS_LICENSE_KEY=REFERENCE),
            lambda v, p, s: p["jobs"]["validation"].update({"if": "github.event.pull_request.head.repo.full_name == github.repository"}),
            lambda v, p, s: v["jobs"]["validate"].update(env={SECRET: REFERENCE}),
            lambda v, p, s: v["jobs"]["validate"]["steps"][0].update(env={SECRET: REFERENCE}),
            lambda v, p, s: next(step for step in v["jobs"]["validate"]["steps"] if step.get("name") == "Release build").update({"continue-on-error": True}),
            lambda v, p, s: s["jobs"]["deploy"].update(env={SECRET: REFERENCE}),
            lambda v, p, s: p.get("on", p.get(True)).update(pull_request_target={}),
        ]
        for mutate in mutations:
            v, p, s = copy.deepcopy(original)
            mutate(v, p, s)
            with self.assertRaises(AssertionError):
                verify(v, p, s)

    def test_empty_license_fails_before_invoking_build(self):
        shell = shutil.which("bash")
        if os.name == "nt":
            # Windows' bash.exe may select WSL rather than the installed Git shell.
            shell = str(Path(shutil.which("git")).parent.parent / "bin/bash.exe")
        self.assertIsNotNone(shell, "bash is needed to execute the CI missing-license path")
        step = next(s for s in load("validate.yml")["jobs"]["validate"]["steps"] if s.get("name") == "Release build")
        env = dict(os.environ)
        env.pop(SECRET, None)
        result = subprocess.run([shell, "-c", step["run"]], env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 1)
        self.assertIn("::error::Release validation requires a Six Labors license", result.stdout)
        self.assertEqual(result.stderr, "")

    def test_msbuild_reads_environment_and_private_files_are_excluded(self):
        project = ROOT / "HockeyPlanner.Backend.WebAPI/HockeyPlanner.Backend.WebAPI.csproj"
        tree = ET.parse(project)
        package = tree.find(".//PackageReference[@Include='SixLabors.ImageSharp']")
        self.assertEqual(package.attrib["Version"], "4.1.2")
        mapping = tree.find(".//SixLaborsLicenseKey")
        self.assertEqual(mapping.text, "$(SIXLABORS_LICENSE_KEY)")
        self.assertEqual(mapping.attrib["Condition"], "'$(SixLaborsLicenseKey)' == ''")
        self.assertIn("sixlabors.lic", (ROOT / ".gitignore").read_text())
        self.assertIn("**/sixlabors.lic", (ROOT / ".dockerignore").read_text())
        self.assertIn("**/*.binlog", (ROOT / ".dockerignore").read_text())


if __name__ == "__main__":
    unittest.main()
