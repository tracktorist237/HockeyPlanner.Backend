import copy
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import yaml
import security_audit as audit

ROOT = Path(__file__).resolve().parents[2]
REPO = "test/repo"
ADVISORY = "https://github.com/advisories/GHSA-abcd-1234-abcd"


def sample(**changes):
    args = dict(repo=REPO, source="nuget", category="dependency", level="High",
                component="Sample.Package", rule=ADVISORY, version="1.2.3")
    args.update(changes)
    return audit.finding(**args)


def baseline(item):
    return {"schemaVersion": 1, "findings": [{"fingerprint": item["fingerprint"],
        "category": item["category"], "firstSeen": "2026-09-30",
        "status": "accepted-existing", "rationale": "Existing HP-77 observation; no dependency upgrades."}]}


class NormalizerTests(unittest.TestCase):
    def test_severity(self):
        for raw, expected in [("Critical", "critical"), ("HIGH", "high"), ("moderate", "medium"),
                              ("medium", "medium"), ("Low", "low"), ("informational", "info")]:
            self.assertEqual(audit.severity(raw), expected)
        with self.assertRaises(audit.InvalidScan):
            audit.severity("unknown")

    def test_fingerprint_stability_and_material_changes(self):
        original = sample()["fingerprint"]
        self.assertEqual(original, sample()["fingerprint"])
        for change in [dict(rule=ADVISORY + "x"), dict(version="2.0.0"), dict(component="Other"),
                       dict(level="critical"), dict(scope="new.csproj"), dict(details="new-range")]:
            self.assertNotEqual(original, sample(**change)["fingerprint"])

    def test_baseline_and_failure_policy(self):
        known = audit.apply_baseline([sample()], baseline(sample()))[0]
        self.assertEqual(known["status"], "KNOWN/BASELINED")
        self.assertEqual(known["firstSeen"], "2026-09-30")
        self.assertFalse(audit.blocking([known]))
        new = audit.apply_baseline([sample(version="1.2.4")], baseline(sample()))[0]
        self.assertEqual(new["status"], "NEW")
        self.assertTrue(audit.blocking([new]))
        for level in ("medium", "low", "info"):
            self.assertFalse(audit.blocking([sample(level=level)]))
        self.assertTrue(audit.blocking([sample(level="critical")]))

    def test_nuget_direct_transitive_dedup_and_path_independence(self):
        package = {"id": "Example", "resolvedVersion": "1.0.0", "vulnerabilities": [
            {"severity": "High", "advisoryurl": ADVISORY}]}
        data = {"version": 1, "parameters": "--vulnerable --include-transitive", "projects": [
            {"path": "C:/local/Example.csproj", "frameworks": [{"framework": "net10.0",
              "topLevelPackages": [package], "transitivePackages": [package]}]}]}
        result = audit.nuget_findings(data, REPO)
        self.assertEqual(len(result), 1)
        data["projects"][0]["path"] = "/runner/Example.csproj"
        self.assertEqual(result, audit.nuget_findings(data, REPO))
        data["logs"] = [{"level": "error", "message": "withheld"}]
        with self.assertRaises(audit.InvalidScan):
            audit.nuget_findings(data, REPO)

    def test_npm_transitive_cause_and_exact_lock_version(self):
        lock = {"packages": {"node_modules/wrapper": {"version": "2.0.0"},
                             "node_modules/leaf": {"version": "1.0.0"}}}
        data = {"auditReportVersion": 2, "metadata": {"vulnerabilities": {"total": 2}}, "vulnerabilities": {
            "wrapper": {"severity": "high", "nodes": ["node_modules/wrapper"], "via": ["leaf"]},
            "leaf": {"severity": "moderate", "nodes": ["node_modules/leaf"], "via": [
                {"url": ADVISORY, "range": "<2.0.0", "severity": "moderate"}]}}}
        result = audit.npm_findings(data, lock, REPO)
        self.assertEqual({x["package"] for x in result}, {"wrapper", "leaf"})
        self.assertEqual({x["severity"] for x in result}, {"high", "medium"})
        lock["packages"]["node_modules/leaf"]["version"] = "1.0.1"
        self.assertNotEqual(result, audit.npm_findings(data, lock, REPO))
        del data["vulnerabilities"]["leaf"]
        with self.assertRaises(audit.InvalidScan):
            audit.npm_findings(data, lock, REPO)

    def test_browserslist_warning_only(self):
        lock = {"packages": {"node_modules/caniuse-lite": {"version": "1.0.30000000"}}}
        self.assertEqual(audit.browserslist_finding("", lock, REPO), [])
        result = audit.browserslist_finding("Browserslist: browsers data (caniuse-lite) is 9 months old.", lock, REPO)
        self.assertEqual(result[0]["severity"], "info")

    def test_fake_secret_is_never_in_metadata_summary_or_fingerprint(self):
        # Synthetic, invalid credential; deliberately assembled, never real credentials.
        canary = "ghp_" + "FAKE_CANARY_DO_NOT_USE_1234567890"
        raw = json.dumps({"number": 42, "secret": canary, "secret_type_display_name": canary,
                          "html_url": canary, "path": canary, "resolution_comment": canary})
        sanitized = audit.secret_metadata(json.loads(raw), REPO)
        rendered = json.dumps(sanitized) + audit.summary({"status": "COMPLETE", "findings": [sanitized]})
        self.assertNotIn(canary, rendered)
        self.assertEqual(sanitized, audit.secret_metadata({"number": 42, "secret": "other"}, REPO))
        self.assertTrue(audit.blocking([sanitized]))
        with self.assertRaises(audit.InvalidScan):
            audit.apply_baseline([sanitized], baseline(sanitized))

    def test_invalid_json_and_subprocess_failure_fail_safely(self):
        for data in ({}, {"version": 1, "projects": []}, {"error": "secret withheld"}):
            with self.assertRaises(audit.InvalidScan):
                audit.nuget_findings(data, REPO)
            with self.assertRaises(audit.InvalidScan):
                audit.npm_findings(data, {}, REPO)
        canary = "fake-sensitive-diagnostic"
        with tempfile.TemporaryDirectory() as folder:
            bp = Path(folder) / "baseline.json"
            bp.write_text(json.dumps({"schemaVersion": 1, "findings": []}))
            for error in (json.JSONDecodeError("bad", canary, 0), subprocess.TimeoutExpired(canary, 1)):
                with patch.object(sys, "argv", ["audit", "--kind", "frontend", "--baseline", str(bp), "--output", folder]), \
                     patch.object(audit, "scan", side_effect=error):
                    self.assertEqual(audit.main(), 2)
                report = (Path(folder) / "findings.json").read_text()
                self.assertNotIn(canary, report)
                self.assertIn("SCAN_ERROR", report)


def verify_workflow(workflow):
    events = workflow.get("on", workflow.get(True))
    assert events["pull_request"]["branches"] == ["develop"]
    assert events["schedule"][0]["cron"] and "workflow_dispatch" in events
    assert set(events) == {"pull_request", "schedule", "workflow_dispatch"}
    assert workflow["permissions"] == {"contents": "read"}
    for job in workflow["jobs"].values():
        assert "environment" not in job and not job.get("continue-on-error")
        assert "github.ref == 'refs/heads/develop'" in job["if"]
        assert "github.event_name == 'pull_request'" in job["if"]
        assert "github.repository == 'tracktorist237/" in job["if"]
        assert job["timeout-minutes"] <= 30
        if "permissions" in job:
            assert job["permissions"] == {"contents": "read", "security-events": "write"}
        for step in job["steps"]:
            assert not step.get("continue-on-error")
            if "uses" in step:
                assert __import__("re").fullmatch(r"(?:actions|github)/[\w/\-]+@[0-9a-f]{40}", step["uses"])
            if "actions/checkout@" in step.get("uses", ""):
                assert step["with"]["persist-credentials"] is False
                assert step["with"]["ref"] == "${{ github.event_name == 'pull_request' && github.sha || 'develop' }}"
            if "actions/upload-artifact@" in step.get("uses", ""):
                assert step["with"]["path"] == "security-audit/"
                assert step["with"]["retention-days"] == 7
    rendered = json.dumps(workflow)
    for forbidden in ("secrets.", "pull_request_target", "ssh-action", "deploy", "OPENAI_API", "git push"):
        assert forbidden not in rendered


class SecurityWorkflowTests(unittest.TestCase):
    def test_workflow_safety(self):
        workflow = yaml.safe_load((ROOT / ".github/workflows/security-audit.yml").read_text())
        verify_workflow(workflow)

    def test_safety_mutations_are_rejected(self):
        original = yaml.safe_load((ROOT / ".github/workflows/security-audit.yml").read_text())
        for mutation in ({"environment": "staging-smoke"}, {"permissions": {"contents": "write"}},
                         {"if": "always()"}, {"continue-on-error": True}):
            changed = copy.deepcopy(original)
            changed["jobs"]["dependencies"].update(mutation)
            with self.assertRaises(AssertionError):
                verify_workflow(changed)


if __name__ == "__main__":
    unittest.main()
