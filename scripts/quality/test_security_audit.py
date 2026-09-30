import ast
import re
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

    def fixture(self, name):
        return json.loads((ROOT / "scripts/quality/fixtures/security" / name).read_text())

    def test_real_nuget_direct_transitive_and_clean_shapes(self):
        data = self.fixture("nuget-v1.json")
        findings = audit.nuget_findings(data, REPO)
        self.assertEqual(len(findings), 21)
        self.assertEqual(audit.nuget_findings(self.fixture("nuget-v1-clean.json"), REPO), [])
        # Exercise top-level packages using a real serialized package record.
        changed = copy.deepcopy(data)
        fw = next(p["frameworks"][0] for p in changed["projects"] if p.get("frameworks"))
        fw["topLevelPackages"] = fw.pop("transitivePackages")
        for project in changed["projects"]:
            project["path"] = "C:/other/" + project["path"].rsplit("/", 1)[-1]
        self.assertEqual(findings, audit.nuget_findings(changed, REPO))

    def test_nuget_problems_and_malformed_shapes_fail_closed(self):
        clean = self.fixture("nuget-v1-clean.json")
        invalid = []
        for problems in ([{"level": "warning", "text": "Vulnerability data could not be retrieved."}],
                         [{"level": "error", "text": "Audit source unavailable."}],
                         {}, None, "warning", [{"level": 1}], [None]):
            invalid.append(dict(clean, problems=problems))
        for fields in ({"projects": []}, {"projects": None}, {"sources": []},
                       {"sources": "https://api.nuget.org/v3/index.json"}, {"parameters": []},
                       {"parameters": "--outdated --vulnerable --include-transitive"}, {"version": True}):
            invalid.append(dict(clean, **fields))
        for frameworks in ({}, None, [{"framework": "net10.0", "transitivePackages": {}}],
                           [{"framework": "net10.0", "transitivePackages": [{"id": "Example", "vulnerabilities": []}]}]):
            data = copy.deepcopy(clean)
            data["projects"][0]["frameworks"] = frameworks
            invalid.append(data)
        data = copy.deepcopy(clean)
        data["projects"][0]["problems"] = [{"level": "warning", "text": "Partial audit"}]
        invalid.append(data)
        for data in invalid:
            with self.subTest(data=data):
                with self.assertRaises((audit.InvalidScan, KeyError)):
                    audit.nuget_findings(data, REPO)
        # Reproduce the reviewer case through the CLI entry point, not just parser.
        with tempfile.TemporaryDirectory() as folder:
            bp = Path(folder) / "baseline.json"
            bp.write_text(json.dumps({"schemaVersion": 1, "findings": []}))
            bad = dict(clean, problems=[{"level": "warning", "text": "Incomplete vulnerability audit"}])
            with patch.object(sys, "argv", ["audit", "--kind", "backend", "--baseline", str(bp), "--output", folder]), \
                 patch.object(audit, "command", return_value=subprocess.CompletedProcess([], 0, json.dumps(bad), "")):
                self.assertEqual(audit.main(), 2)
            report = json.loads((Path(folder) / "findings.json").read_text())
            self.assertEqual(report["status"], "SCAN_ERROR")

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


def evaluate_condition(expression, context):
    """Evaluate only the small pure Actions boolean subset used by this workflow.

    No eval, arbitrary calls, secrets, dynamic property lookup or Python execution.
    Unsupported syntax fails validation instead of silently passing a matrix case.
    """
    tree = ast.parse(expression.replace("&&", " and ").replace("||", " or "), mode="eval")

    def visit(node):
        if isinstance(node, ast.Constant) and type(node.value) in (str, bool):
            return node.value
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Name) and node.value.id == "github":
            assert node.attr in context
            return context[node.attr]
        if isinstance(node, ast.BoolOp) and isinstance(node.op, (ast.And, ast.Or)):
            values = [visit(value) for value in node.values]
            assert all(type(x) is bool for x in values)
            return all(values) if isinstance(node.op, ast.And) else any(values)
        if isinstance(node, ast.Compare) and len(node.ops) == 1 and isinstance(node.ops[0], (ast.Eq, ast.NotEq)):
            left, right = visit(node.left), visit(node.comparators[0])
            equal = left.lower() == right.lower() if type(left) is str and type(right) is str else left == right
            return equal if isinstance(node.ops[0], ast.Eq) else not equal
        if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "startsWith":
            assert len(node.args) == 2 and not node.keywords
            values = [visit(x) for x in node.args]
            assert all(type(x) is str for x in values)
            return values[0].lower().startswith(values[1].lower())
        raise AssertionError("Unsupported workflow condition")

    result = visit(tree.body)
    assert type(result) is bool
    return result


def verify_workflow(workflow):
    events = workflow.get("on", workflow.get(True))
    assert set(events) == {"pull_request", "push", "schedule", "workflow_dispatch"}
    assert events["pull_request"] == {"branches": ["develop"]}
    assert events["push"] == {"branches": ["develop"]}
    assert events["workflow_dispatch"] is None
    assert events["schedule"] == [{"cron": "23 7 * * 1"}]
    assert workflow["permissions"] == {"contents": "read"}
    assert set(workflow) <= {"name", "on", True, "permissions", "concurrency", "jobs"}
    assert set(workflow["jobs"]) == {"dependencies", "codeql"}
    backend = (ROOT / "HockeyPlanner.Backend.sln").exists()
    kind = "backend" if backend else "frontend"
    repo = "tracktorist237/" + ("HockeyPlanner.Backend" if backend else "hockey-planner")
    allowed_commands = {
        "pip install -r scripts/quality/requirements.txt",
        'python -m unittest discover -s scripts/quality -p "test_security_*.py"',
        "python scripts/quality/security_audit.py --kind " + kind,
    }
    if not backend:
        allowed_commands.add("npm ci --ignore-scripts --no-audit --no-fund")
    for role, job in workflow["jobs"].items():
        assert set(job) <= {"name", "if", "runs-on", "timeout-minutes", "permissions", "steps"}
        assert job["runs-on"] == "ubuntu-latest" and 0 < job["timeout-minutes"] <= 30
        expected_permissions = {"contents": "read"}
        if role == "codeql":
            expected_permissions["security-events"] = "write"
        assert job.get("permissions", workflow["permissions"]) == expected_permissions
        # Test the actual expression independently of trigger filtering: even a
        # mistakenly delivered master-ref event must never run a develop audit.
        for event in ("pull_request", "push", "schedule", "workflow_dispatch", "pull_request_target", "other"):
            for ref in ("refs/pull/1/merge", "refs/heads/develop", "refs/heads/master", "refs/heads/task"):
                for base in ("develop", "master", ""):
                    for repository in (repo, "someone/fork"):
                        expected = repository == repo and (
                            (event == "pull_request" and base == "develop" and ref == "refs/pull/1/merge") or
                            (event in ("push", "schedule", "workflow_dispatch") and ref == "refs/heads/develop"))
                        assert evaluate_condition(job["if"], dict(repository=repository, event_name=event, ref=ref, base_ref=base)) == expected
        commands, actions = set(), []
        for step in job["steps"]:
            assert set(step) <= {"name", "uses", "with", "run", "if"}
            assert ("run" in step) != ("uses" in step)
            if "run" in step:
                assert role == "dependencies" and "if" not in step
                lines = {x.strip() for x in step["run"].splitlines() if x.strip()}
                # An explicit command allowlist rejects deploy, DB mutation,
                # credential exports and appended shell commands, not just keywords.
                assert lines <= allowed_commands
                commands.update(lines)
                continue
            action, sha = step["uses"].split("@")
            assert re.fullmatch(r"[0-9a-f]{40}", sha)
            actions.append(action)
            inputs = step.get("with", {})
            if action == "actions/checkout":
                assert inputs == {"persist-credentials": False,
                    "ref": "${{ github.event_name == 'pull_request' && github.sha || 'develop' }}"}
            elif action == "actions/upload-artifact":
                assert role == "dependencies"
                assert set(inputs) == {"name", "path", "retention-days"}
                assert set(inputs["path"].split()) == {"security-audit/findings.json", "security-audit/summary.md"}
                assert inputs["retention-days"] == 7
                assert step["if"] == "always() && hashFiles('security-audit/findings.json') != ''"
            elif action == "actions/setup-python":
                assert inputs == {"python-version": "3.12"}
            elif action == "actions/setup-dotnet":
                assert inputs == {"dotnet-version": "10.0.x"}
            elif action == "actions/setup-node":
                assert inputs == {"node-version": "20", "package-manager-cache": False}
            elif action == "github/codeql-action/init":
                assert inputs == {"languages": "csharp" if backend else "javascript-typescript", "build-mode": "none"}
            elif action == "github/codeql-action/analyze":
                assert inputs == {"category": "hp77-" + ("csharp" if backend else "javascript-typescript")}
            else:
                raise AssertionError("Unaudited action")
            if action != "actions/upload-artifact":
                assert "if" not in step
        expected_actions = (["actions/checkout", "github/codeql-action/init", "github/codeql-action/analyze"]
            if role == "codeql" else ["actions/checkout", "actions/setup-python",
                "actions/setup-dotnet" if backend else "actions/setup-node", "actions/upload-artifact"])
        assert actions == expected_actions
        assert commands == (allowed_commands if role == "dependencies" else set())
    assert "secrets." not in json.dumps(workflow).lower()


class SecurityWorkflowTests(unittest.TestCase):
    def workflow(self):
        return yaml.safe_load((ROOT / ".github/workflows/security-audit.yml").read_text())

    def test_workflow_safety_and_event_ref_matrix(self):
        verify_workflow(self.workflow())

    def test_safety_mutations_are_rejected(self):
        original = self.workflow()
        mutations = [
            lambda w: w.update(permissions={"contents": "write"}),
            lambda w: w["jobs"]["dependencies"].update(permissions={"contents": "read", "security-events": "write"}),
            lambda w: w["jobs"]["codeql"].update(permissions={"contents": "write", "security-events": "write"}),
            lambda w: w["jobs"]["dependencies"].update(environment="staging-smoke"),
            lambda w: w["jobs"]["dependencies"].update(env={"KEY": "${{ secrets.PRODUCTION_KEY }}"}),
            lambda w: w["jobs"]["dependencies"].update({"continue-on-error": True}),
            lambda w: w["jobs"]["dependencies"].update({"if": w["jobs"]["dependencies"]["if"] + " || github.ref == 'refs/heads/master'"}),
            lambda w: w["jobs"]["dependencies"].update({"if": w["jobs"]["dependencies"]["if"] + " || github.ref == 'refs/heads/MASTER'"}),
            lambda w: w["jobs"]["dependencies"]["steps"].append({"run": "psql -c 'DROP DATABASE test'"}),
            lambda w: w["jobs"]["dependencies"]["steps"].append({"run": "npm run deploy"}),
            lambda w: w["jobs"]["dependencies"]["steps"][0]["with"].update({"persist-credentials": True}),
            lambda w: w["jobs"]["dependencies"]["steps"][-1]["with"].update(path="security-audit/"),
            lambda w: w.get("on", w.get(True)).update(pull_request_target={}),
        ]
        for mutation in mutations:
            changed = copy.deepcopy(original)
            mutation(changed)
            with self.assertRaises(AssertionError):
                verify_workflow(changed)


if __name__ == "__main__":
    unittest.main()
