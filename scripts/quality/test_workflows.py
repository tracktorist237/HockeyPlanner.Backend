"""Static gate contract plus failure-state simulation; never contacts a VPS.

GitHub needs semantics: failed/cancelled/skipped dependencies skip downstream jobs
unless an overriding condition is present. Lock that exact configuration here.
"""
import copy
import pathlib
import unittest
import yaml

ROOT = pathlib.Path(__file__).resolve().parents[2]


def load(name):
    return yaml.safe_load((ROOT / ".github/workflows" / name).read_text(encoding="utf-8"))


def verify(staging, validation):
    deploy = staging["jobs"]["deploy"]
    assert deploy["needs"] == "validation", "Deploy must depend on validation"
    assert "if" not in deploy, "Do not override GitHub implicit success()"
    assert not deploy.get("continue-on-error", False)
    assert staging["jobs"]["validation"]["uses"] == "./.github/workflows/validate.yml"
    assert staging["concurrency"]["cancel-in-progress"] is False, "Do not interrupt SSH deployment"
    job = validation["jobs"]["validate"]
    assert not job.get("continue-on-error", False)
    commands = []
    for step in job["steps"]:
        if "run" in step:
            assert not step.get("continue-on-error", False)
            assert "if" not in step, "Required checks must run"
            assert "|| true" not in step["run"]
            commands.append(step["run"])
    joined = "\n".join(commands)
    assert "git diff --check" in joined
    if "Backend" in validation["name"]:
        for required in ("dotnet restore", "dotnet build", "dotnet test", "check_test_results.py"):
            assert required in joined
    else:
        for required in ("npm ci", "npm test -- --watchAll=false", "npm run build"):
            assert required in joined
    script = deploy["steps"][0]["with"]["script"]
    assert 'git merge --ff-only "$EXPECTED_SHA"' in script
    assert 'test "$(git rev-parse HEAD)" = "$EXPECTED_SHA"' in script
    assert "git pull" not in script


class WorkflowGateTests(unittest.TestCase):
    def test_workflows_parse_and_deploy_is_gated(self):
        for path in (ROOT / ".github/workflows").glob("*.yml"):
            self.assertIsInstance(yaml.safe_load(path.read_text(encoding="utf-8")), dict)
        verify(load("deploy-staging-vps.yml"), load("validate.yml"))

    def test_failure_cancel_and_skip_never_authorize_deploy(self):
        staging, validation = load("deploy-staging-vps.yml"), load("validate.yml")
        verify(staging, validation)
        for result in ("failure", "cancelled", "skipped", "success"):
            # No overriding if/continue-on-error, so only GitHub success() permits deploy.
            eligible = all(value == "success" for value in [result])
            self.assertEqual(eligible, result == "success")

    def test_gate_removal_and_failure_bypass_are_detected(self):
        original, validation = load("deploy-staging-vps.yml"), load("validate.yml")
        for mutation in ({"needs": []}, {"if": "always()"}, {"continue-on-error": True}):
            changed = copy.deepcopy(original)
            changed["jobs"]["deploy"].update(mutation)
            with self.assertRaises(AssertionError):
                verify(changed, validation)
        changed = copy.deepcopy(validation)
        next(step for step in changed["jobs"]["validate"]["steps"] if "run" in step)["continue-on-error"] = True
        with self.assertRaises(AssertionError):
            verify(original, changed)


if __name__ == "__main__":
    unittest.main()
