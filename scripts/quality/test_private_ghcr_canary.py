"""Owner-label canary transaction: real HP-90 scan, synthetic Git/Docker/HTTP.

Never applies a label, logs in to a registry or publishes a real image.
"""
import contextlib
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import unittest
import urllib.parse
from unittest.mock import patch

import private_ghcr_canary as canary
import test_docker_artifact as fixture
from test_imagesharp_image import MARKER
from test_workflows import load

TOKEN = "synthetic-job-token-not-a-credential"
HEAD_SHA = "b" * 40


class CanaryTransactionTests(unittest.TestCase):
    def setUp(self):
        self.build = fixture.DockerArtifactTests()
        self.build.setUp()
        self.addCleanup(self.build.doCleanups)
        self.env = dict(GITHUB_EVENT_NAME="pull_request", GITHUB_REPOSITORY=canary.REPO,
            GITHUB_ACTOR=canary.OWNER, GITHUB_TRIGGERING_ACTOR=canary.OWNER,
            GITHUB_RUN_ATTEMPT="1", GITHUB_RUN_ID="900", GITHUB_SHA=fixture.SHA,
            RUNNER_TEMP=str(self.build.private), GITHUB_STEP_SUMMARY=str(self.build.root.parent / "summary"),
            GITHUB_EVENT_PATH=str(self.build.root.parent / "event.json"))
        self.event = {"action": "labeled", "repository": {"full_name": canary.REPO, "owner": {"login": canary.OWNER}},
            "sender": {"login": canary.OWNER}, "label": {"name": canary.LABEL}, "pull_request": {"number": 24,
            "base": {"ref": canary.BASE, "sha": canary.BASE_SHA, "repo": {"full_name": canary.REPO}},
            "head": {"ref": canary.HEAD, "sha": HEAD_SHA, "repo": {"full_name": canary.REPO}}}}
        self.live = dict(copy.deepcopy(self.event["pull_request"]), state="open", merged=False,
                         merge_commit_sha=fixture.SHA, labels=[{"name": canary.LABEL}])
        self.manifest = json.dumps({"schemaVersion": 2, "mediaType": canary.MANIFEST_TYPES[0],
            "config": {"digest": self.build.iid}, "layers": [{"digest": "sha256:" + "f" * 64}]}).encode()
        self.digest = "sha256:" + hashlib.sha256(self.manifest).hexdigest()
        self.trace = []
        self.pushed = False
        self.overrides = {}
        self.inspect_id = self.build.iid
        self.push_digest = self.digest
        self.package_visibility = "private"
        self.anonymous_status = 403
        self.git_merge = [fixture.SHA, canary.BASE_SHA, HEAD_SHA]
        self.checks = [{"name": name, "app": {"slug": "github-actions"}, "head_sha": HEAD_SHA,
                        "conclusion": "success"} for name in canary.CHECKS]
        self.label_events = [{"event": "labeled", "label": {"name": canary.LABEL}, "actor": {"login": canary.OWNER}}]
        self.runs = [{"id": 900}]
        self.prior_job = "skipped"
        self.prior_attempts = None
        self.jobs_urls = []
        original = canary.artifact.validate
        patches = [patch.object(canary.artifact, "ROOT", self.build.root),
            patch.object(canary.artifact, "command", side_effect=lambda *a, **k: " ".join(self.git_merge).encode()),
            patch.object(canary.artifact, "validate", side_effect=lambda root, temp, sha, license:
                original(root, temp, sha, license, self.build.run_command)),
            patch.object(canary, "fetch", side_effect=self.fetch), patch.object(canary, "docker", side_effect=self.docker)]
        for patched in patches:
            patched.start()
            self.addCleanup(patched.stop)

    def fetch(self, url, headers=None):
        headers = headers or {}
        if url in self.overrides:
            return self.overrides[url]
        def reply(value, status=200, extra=None):
            return status, extra or {}, json.dumps(value).encode()
        if url == canary.REPO_API + f"/pulls/{self.event['pull_request']['number']}":
            return reply(self.live)
        if "/check-runs?" in url:
            return reply({"total_count": len(self.checks), "check_runs": self.checks})
        if f"/issues/{self.event['pull_request']['number']}/events?" in url:
            return reply(self.label_events)
        if url == canary.REPO_API + "/actions/runs/900":
            return reply({"head_branch": canary.HEAD, "run_attempt": 1, "head_sha": HEAD_SHA, "workflow_id": 77})
        if "/actions/workflows/77/runs?" in url:
            return reply({"total_count": len(self.runs), "workflow_runs": self.runs})
        if "/actions/runs/899/jobs?" in url:
            self.jobs_urls.append(url)
            conclusions = self.prior_attempts if self.prior_attempts is not None else [self.prior_job]
            jobs = [{"id": 89900 + attempt, "run_id": 899, "run_attempt": attempt,
                     "status": "completed", "conclusion": conclusion}
                    for attempt, conclusion in enumerate(conclusions, 1)]
            # Model GitHub's real default: latest hides earlier attempts of THIS run.
            query = urllib.parse.parse_qs(urllib.parse.urlsplit(url).query)
            if query.get("filter", ["latest"]) == ["latest"]:
                jobs = jobs[-1:]
            else:
                self.assertEqual(query["filter"], ["all"])
            return reply({"total_count": len(jobs), "jobs": jobs})
        if url.startswith("https://ghcr.io/token?"):
            return reply({"token": "authenticated-canary-bearer" if "Authorization" in headers else "anonymous-canary-bearer"})
        if url == canary.PACKAGE_API:
            if not self.pushed:
                return reply({"message": "Package not found"}, 404)
            return reply({"name": canary.PACKAGE, "package_type": "container", "owner": {"login": canary.OWNER},
                "repository": {"full_name": canary.REPO}, "visibility": self.package_visibility, "version_count": 1})
        if url == canary.PACKAGE_API + "/versions?per_page=100":
            return reply([{"name": self.digest, "metadata": {"container": {"tags": ["canary-" + HEAD_SHA]}}}])
        if url.endswith("/tags/list"):
            self.assertFalse(self.pushed)
            return reply({"errors": [{"code": "NAME_UNKNOWN"}]}, 404)
        if "/manifests/" in url:
            if headers.get("Authorization") == "Bearer anonymous-canary-bearer":
                return reply({"errors": [{"code": "DENIED"}]}, self.anonymous_status)
            self.assertTrue(self.pushed)
            return 200, {"Docker-Content-Digest": self.digest}, self.manifest
        self.fail("Unexpected HTTP operation: " + url)

    def docker(self, args, env, stdin=None):
        self.trace.append(args)
        self.assertNotIn(TOKEN, json.dumps(args))
        self.assertNotIn(MARKER, json.dumps(args))
        self.assertNotIn(canary.artifact.SECRET, env)
        self.assertNotIn("GHCR_JOB_TOKEN", env)
        config = Path(env["DOCKER_CONFIG"])
        self.assertFalse(config.is_relative_to(self.build.root))
        if args[0] == "login":
            self.assertEqual(args, ["login", "ghcr.io", "--username", canary.OWNER, "--password-stdin"])
            self.assertEqual(stdin, TOKEN.encode())
            (config / "config.json").write_text(json.dumps({"auths": {"ghcr.io": {"auth": "synthetic"}}}))
        elif args[:2] == ["image", "inspect"]:
            return json.dumps([{"Id": self.inspect_id, "RepoDigests": [canary.IMAGE + "@" + self.digest]}]).encode()
        elif args[:2] == ["image", "tag"]:
            self.assertEqual(args[2], self.build.iid)
            self.assertEqual(args[3], canary.IMAGE + ":canary-" + HEAD_SHA)
        elif args[:2] == ["image", "push"]:
            self.assertFalse(self.pushed)
            self.pushed = True
            return ("canary: digest: " + self.push_digest + " size: 100").encode()
        elif args[:2] == ["image", "pull"]:
            self.assertEqual(args, ["image", "pull", "--platform", "linux/amd64", canary.IMAGE + "@" + self.digest])
        else:
            self.fail("Unsupported Docker command")
        return b"discarded synthetic diagnostics"

    def cli(self, mode, secret=None):
        Path(self.env["GITHUB_EVENT_PATH"]).write_text(json.dumps(self.event))
        supplied = {canary.artifact.SECRET: MARKER if secret is None else secret} if mode == "prepare" else {"GHCR_JOB_TOKEN": TOKEN}
        out, err = io.StringIO(), io.StringIO()
        with patch.dict(os.environ, {**self.env, **supplied}), patch.object(sys, "argv", ["canary", mode]), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            result = canary.main()
        rendered = out.getvalue() + err.getvalue()
        self.assertNotIn(MARKER, rendered)
        self.assertNotIn(TOKEN, rendered)
        return result, rendered

    def prepared(self):
        code, rendered = self.cli("prepare")
        self.assertEqual(code, 0, rendered)
        self.assertFalse(self.pushed)

    def test_real_scanner_precedes_single_push_authenticated_digest_pull_and_privacy(self):
        with patch.object(canary.artifact.scanner, "inspect", wraps=canary.artifact.scanner.inspect) as scanner:
            self.prepared()
            self.assertEqual(scanner.call_args.kwargs, {"expected_image_id": self.build.iid})
            code, rendered = self.cli("publish")
        self.assertEqual(code, 0, rendered)
        self.assertEqual(scanner.call_count, 1, "Never rebuild/rescan between scan and push")
        self.assertEqual(sum(args[:2] == ["image", "push"] for args in self.trace), 1)
        self.assertIn('"registryDigest": "' + self.digest + '"', rendered)
        self.assertNotEqual(self.digest, self.build.iid)
        self.assertEqual(list(self.build.private.iterdir()), [])
        summary = Path(self.env["GITHUB_STEP_SUMMARY"]).read_text()
        self.assertNotIn(MARKER, summary)
        self.assertNotIn(TOKEN, summary)

    def test_automatic_events_wrong_label_fork_actor_and_reruns_reject_before_build(self):
        for mutate in (
            lambda: self.event.update(action="opened"), lambda: self.event.update(action="synchronize"),
            lambda: self.env.update(GITHUB_EVENT_NAME="push"),
            lambda: self.event["label"].update(name="other"),
            lambda: self.event["pull_request"]["head"]["repo"].update(full_name="attacker/fork"),
            lambda: self.env.update(GITHUB_ACTOR="attacker"),
            lambda: self.env.update(GITHUB_TRIGGERING_ACTOR="attacker"),
            lambda: self.env.update(GITHUB_RUN_ATTEMPT="2"),
            lambda: self.event["pull_request"]["head"].update(ref="other")):
            old_event, old_env = copy.deepcopy(self.event), self.env.copy()
            mutate()
            self.assertEqual(self.cli("prepare")[0], 1)
            self.event, self.env = old_event, old_env
        self.assertFalse(self.build.calls)
        self.assertFalse(self.trace)

    def test_stale_head_base_merge_or_non_green_check_rejects_before_build(self):
        for side in ("head", "base"):
            original = self.live[side]["sha"]
            self.live[side]["sha"] = "c" * 40
            self.assertEqual(self.cli("prepare")[0], 1)
            self.live[side]["sha"] = original
        self.git_merge = ["d" * 40, canary.BASE_SHA, HEAD_SHA]
        self.assertEqual(self.cli("prepare")[0], 1)
        self.git_merge[0] = fixture.SHA
        self.checks[0]["conclusion"] = "failure"
        self.assertEqual(self.cli("prepare")[0], 1)
        self.assertFalse(self.build.calls)

    def test_repeated_approval_or_previous_uncertain_attempt_blocks(self):
        self.label_events *= 2
        self.assertEqual(self.cli("prepare")[0], 1)
        self.label_events = self.label_events[:1]
        self.runs.append({"id": 899})
        for conclusion in (None, "failure", "cancelled", "success"):
            self.prior_job = conclusion
            self.assertEqual(self.cli("prepare")[0], 1)
        self.assertFalse(self.build.calls)

    def history_blocked(self):
        code, rendered = self.cli("prepare")
        self.assertEqual(code, 1, rendered)
        self.assertIn("BLOCKED", rendered)
        self.assertNotIn("PASS", rendered)
        self.assertFalse(self.build.calls, "No licensed build after rejected history")
        self.assertFalse(self.trace, "No registry login/push after rejected history")
        self.assertFalse(self.pushed)

    def test_failed_initial_attempt_skipped_rerun_replacement_pr_blocks_before_build(self):
        self.event["pull_request"]["number"] = self.live["number"] = 25
        # One old workflow run on PR24, two attempts; current run900 is on PR25.
        self.runs.append({"id": 899, "run_attempt": 2, "pull_requests": [{"number": 24}]})
        self.prior_attempts = ["failure", "skipped"]
        self.history_blocked()

    def test_single_previous_skipped_job_allows_first_attempt_and_requires_all_filter(self):
        self.runs.append({"id": 899})
        self.prepared()
        query = urllib.parse.parse_qs(urllib.parse.urlsplit(self.jobs_urls[-1]).query)
        self.assertEqual(query, {"filter": ["all"], "per_page": ["100"], "page": ["1"]})
        self.assertFalse(self.trace)

    def test_complete_previous_all_skipped_attempts_allow_first_attempt(self):
        self.runs.append({"id": 899, "run_attempt": 2})
        self.prior_attempts = ["skipped", "skipped"]
        self.prepared()
        self.assertFalse(self.trace)

    def test_non_skipped_initial_attempt_cannot_be_hidden_by_skipped_rerun(self):
        self.runs.append({"id": 899, "run_attempt": 2})
        for conclusion in ("failure", "success", "cancelled", None):
            with self.subTest(conclusion=conclusion):
                self.prior_attempts = [conclusion, "skipped"]
                self.history_blocked()

    def test_incomplete_ambiguous_or_unavailable_all_attempt_history_blocks(self):
        self.runs.append({"id": 899})
        job = {"id": 89901, "run_id": 899, "status": "completed", "conclusion": "skipped"}
        boundary = [dict(job, id=89900 + i) for i in range(100)]
        invalid = [
            {"total_count": 2, "jobs": [job]},
            {"total_count": 0, "jobs": []},
            {"total_count": True, "jobs": [job]},
            {"total_count": 1, "jobs": {}},
            {"total_count": 100, "jobs": boundary},
            {"total_count": 101, "jobs": boundary},
            {"total_count": 2, "jobs": [job, job]},
            {"total_count": 1, "jobs": [dict(job, run_id=898)]},
            {"total_count": 1, "jobs": [dict(job, id=True)]},
            {"total_count": 1, "jobs": [dict(job, status="in_progress")]},
            {"total_count": 1, "jobs": [{"id": 89901, "run_id": 899}]},
        ]
        url = canary.REPO_API + "/actions/runs/899/jobs?filter=all&per_page=100&page=1"
        for payload in invalid:
            with self.subTest(payload=payload):
                self.overrides[url] = 200, {}, json.dumps(payload).encode()
                self.history_blocked()
        for status in (403, 503):
            with self.subTest(status=status):
                self.overrides[url] = status, {}, b'{}'
                self.history_blocked()

    def test_missing_license_scanner_rejection_and_wrong_builder_iid_never_push(self):
        self.assertEqual(self.cli("prepare", "")[0], 1)
        self.build.export_mode = "corrupt"
        self.assertEqual(self.cli("prepare")[0], 1)
        self.build.export_mode = "valid"
        self.build.build_id = "sha256:" + "e" * 64
        self.assertEqual(self.cli("prepare")[0], 1)
        self.assertFalse(self.trace)
        self.assertFalse(self.pushed)

    def test_scanner_exception_never_pushes_and_diagnostics_are_sanitized(self):
        with patch.object(canary.artifact.scanner, "inspect", side_effect=RuntimeError(MARKER + TOKEN)):
            self.assertEqual(self.cli("prepare")[0], 1)
        self.assertFalse(self.trace)

    def test_existing_package_or_ambiguous_registry_absence_blocks_before_login(self):
        self.prepared()
        for status, body in ((200, {"visibility": "private"}), (403, {})):
            self.overrides[canary.PACKAGE_API] = status, {}, json.dumps(body).encode()
            with patch.dict(os.environ, self.env):
                with self.assertRaises(ValueError):
                    canary.absent(TOKEN, "bearer")
        self.overrides.clear()
        tags = "https://ghcr.io/v2/" + canary.NAME + "/tags/list"
        for status, body in ((200, {"tags": ["canary-" + HEAD_SHA]}),
                             (404, {"errors": [{"code": "DENIED"}]}), (403, {})):
            self.overrides[tags] = status, {}, json.dumps(body).encode()
            self.assertEqual(self.cli("publish")[0], 1)
            self.assertFalse(self.trace)
            # Restore only the synthetic scan proof to exercise another negative.
            if not (self.build.private / "hp91-proof-900").exists():
                self.prepared()

    def test_wrong_loaded_iid_blocks_push(self):
        self.prepared()
        self.inspect_id = "sha256:" + "c" * 64
        self.assertEqual(self.cli("publish")[0], 1)
        self.assertFalse(self.pushed)

    def test_stale_after_scan_blocks_login_or_push(self):
        self.prepared()
        self.live["head"]["sha"] = "c" * 40
        self.assertEqual(self.cli("publish")[0], 1)
        self.assertFalse(self.trace)

    def test_source_drift_immediately_before_push_blocks_mutation(self):
        self.prepared()
        original = self.docker
        def drift(args, env, stdin=None):
            result = original(args, env, stdin)
            if args[:2] == ["image", "tag"]:
                self.live["base"]["sha"] = "c" * 40
            return result
        with patch.object(canary, "docker", side_effect=drift):
            self.assertEqual(self.cli("publish")[0], 1)
        self.assertFalse(self.pushed)

    def test_valid_registry_digest_with_different_config_iid_blocks_success(self):
        self.prepared()
        changed = json.loads(self.manifest)
        changed["config"]["digest"] = "sha256:" + "c" * 64
        self.manifest = json.dumps(changed).encode()
        self.digest = self.push_digest = "sha256:" + hashlib.sha256(self.manifest).hexdigest()
        self.assertEqual(self.cli("publish")[0], 1)
        self.assertTrue(self.pushed)
        self.assertFalse(Path(self.env["GITHUB_STEP_SUMMARY"]).exists())

    def test_registry_digest_and_authenticated_pull_mismatches_block_success(self):
        self.prepared()
        self.push_digest = "sha256:" + "c" * 64
        self.assertEqual(self.cli("publish")[0], 1)
        self.assertTrue(self.pushed)
        self.assertFalse(Path(self.env["GITHUB_STEP_SUMMARY"]).exists())
        # Fresh synthetic runner for pulled identity mismatch.
        self.pushed = False
        self.push_digest = self.digest
        self.prepared()
        original = self.docker
        def mismatch(args, env, stdin=None):
            result = original(args, env, stdin)
            if args[:2] == ["image", "inspect"] and "@" in args[2]:
                return json.dumps([{"Id": "sha256:" + "c" * 64, "RepoDigests": []}]).encode()
            return result
        with patch.object(canary, "docker", side_effect=mismatch):
            self.assertEqual(self.cli("publish")[0], 1)
        self.assertFalse(Path(self.env["GITHUB_STEP_SUMMARY"]).exists())

    def test_private_visibility_failure_or_anonymous_access_never_reports_pass(self):
        for visibility, status in (("public", 403), ("private", 200), ("private", 404)):
            self.pushed = False
            self.package_visibility, self.anonymous_status = visibility, status
            self.prepared()
            self.assertEqual(self.cli("publish")[0], 1)
            self.assertFalse(Path(self.env["GITHUB_STEP_SUMMARY"]).exists())

    def test_anonymous_rate_limit_or_opaque_error_is_not_privacy_proof(self):
        for status, body in ((403, {"errors": [{"code": "TOOMANYREQUESTS"}]}), (401, {}), (404, {})):
            self.pushed = False
            self.prepared()
            endpoint = "https://ghcr.io/v2/" + canary.NAME + "/manifests/" + self.digest
            original = self.fetch
            def fetch(url, headers=None):
                if url == endpoint and (headers or {}).get("Authorization") == "Bearer anonymous-canary-bearer":
                    return status, {}, json.dumps(body).encode()
                return original(url, headers)
            with patch.object(canary, "fetch", side_effect=fetch):
                self.assertEqual(self.cli("publish")[0], 1)
            self.assertFalse(Path(self.env["GITHUB_STEP_SUMMARY"]).exists())


class CanaryWorkflowTests(unittest.TestCase):
    def test_only_owner_label_can_grant_publication_permissions_and_secret_steps_are_separate(self):
        workflow = load("ghcr-private-canary.yml")
        self.assertEqual(workflow.get("on", workflow.get(True)), {"pull_request": {"types": ["labeled"], "branches": [canary.BASE]}})
        self.assertEqual(workflow["permissions"], {"contents": "read"})
        self.assertEqual(workflow["concurrency"], {"group": "hp91-private-ghcr-canary", "cancel-in-progress": False})
        self.assertEqual(set(workflow["jobs"]), {"publish"})
        job = workflow["jobs"]["publish"]
        self.assertEqual(job["permissions"], {"contents": "read", "packages": "write"})
        for condition in ("github.event.label.name == 'ghcr-canary-approved'", "github.head_ref == 'quality/ghcr-private-canary'",
                          "github.actor == github.repository_owner", "github.triggering_actor == github.repository_owner",
                          "github.run_attempt == 1", "github.event.pull_request.head.repo.full_name == github.repository"):
            self.assertIn(condition, job["if"])
        self.assertNotIn("||", job["if"])
        steps = job["steps"]
        self.assertEqual(len(steps), 4)
        self.assertEqual(steps[0]["with"], {"fetch-depth": 2, "persist-credentials": False})
        self.assertEqual(steps[2]["env"], {canary.artifact.SECRET: "${{ secrets.SIXLABORS_LICENSE_KEY }}"})
        self.assertEqual(steps[3]["env"], {"GHCR_JOB_TOKEN": "${{ github.token }}"})
        self.assertEqual(steps[2]["run"], "python scripts/quality/private_ghcr_canary.py prepare")
        self.assertEqual(steps[3]["run"], "python scripts/quality/private_ghcr_canary.py publish")
        for step in steps:
            self.assertNotIn("if", step)
            self.assertNotIn("continue-on-error", step)
            self.assertFalse(step.get("uses", "").startswith("actions/upload-artifact"))
        self.assertNotIn("env", job)
        self.assertNotIn("env", workflow)


if __name__ == "__main__":
    unittest.main()
