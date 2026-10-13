"""HP-91 operator-label-only canary; no automatic/retry/deployment interface.

Public GitHub reads bind owner approval to current PR/head/base/CI. Only publication
uses the job token, in HTTPS headers or Docker password-stdin, never CLI arguments.
"""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request

import validate_docker_artifact as artifact

OWNER = "tracktorist237"
REPO = OWNER + "/HockeyPlanner.Backend"
BASE = "quality/ghcr-artifact-validation"
HEAD = "quality/ghcr-private-canary"
BASE_SHA = "24097e58cff2ed104682be83bed3673d93ec5e8a"
LABEL = "ghcr-canary-approved"
PACKAGE = "hockeyplanner-backend-hp91-canary"
NAME = OWNER + "/" + PACKAGE
IMAGE = "ghcr.io/" + NAME
API = "https://api.github.com"
REPO_API = API + "/repos/" + REPO
PACKAGE_API = API + "/users/" + OWNER + "/packages/container/" + PACKAGE
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")
MANIFEST_TYPES = ("application/vnd.docker.distribution.manifest.v2+json", "application/vnd.oci.image.manifest.v1+json")
CHECKS = {"validation / Backend quality gate", "Deterministic dependency audit", "CodeQL", "Licensed Docker artifact scan"}


def require(value):
    if not value:
        raise ValueError("Canary invariant failed")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None


def fetch(url, headers=None):
    require(urllib.parse.urlsplit(url).scheme == "https" and urllib.parse.urlsplit(url).netloc in ("api.github.com", "ghcr.io"))
    request = urllib.request.Request(url, headers={"User-Agent": "HP-91-canary", **(headers or {})})
    try:
        response = urllib.request.build_opener(NoRedirect()).open(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        raw = response.read(2 * 1024 * 1024 + 1)
        require(len(raw) <= 2 * 1024 * 1024)
        return response.code, dict(response.headers.items()), raw


def data(url, token=None):
    headers = {"Accept": "application/vnd.github+json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    status, _, raw = fetch(url, headers)
    require(status == 200)
    return json.loads(raw)


def pages(url):
    rows = []
    for page in range(1, 6):
        batch = data(url + ("&" if "?" in url else "?") + f"per_page=100&page={page}")
        require(isinstance(batch, list))
        rows.extend(batch)
        if len(batch) < 100:
            return rows
    raise ValueError("History too large")


def gate(event, env):
    pr = event["pull_request"]
    require(env["GITHUB_EVENT_NAME"] == "pull_request" and event["action"] == "labeled")
    require(env["GITHUB_REPOSITORY"] == REPO and event["repository"]["full_name"] == REPO)
    require(event["repository"]["owner"]["login"] == OWNER)
    require(env["GITHUB_ACTOR"] == env["GITHUB_TRIGGERING_ACTOR"] == event["sender"]["login"] == OWNER)
    require(env["GITHUB_RUN_ATTEMPT"] == "1" and event["label"]["name"] == LABEL)
    require(pr["base"]["ref"] == BASE and pr["base"]["sha"] == BASE_SHA)
    require(pr["base"]["repo"]["full_name"] == pr["head"]["repo"]["full_name"] == REPO)
    require(pr["head"]["ref"] == HEAD and re.fullmatch(r"[0-9a-f]{40}", pr["head"]["sha"]))
    require(re.fullmatch(r"[0-9a-f]{40}", env["GITHUB_SHA"]) and env["GITHUB_RUN_ID"].isdigit())
    require(type(pr["number"]) is int and pr["number"] > 0)
    return pr


def source(event, env):
    pr = gate(event, env)
    live = data(REPO_API + f"/pulls/{pr['number']}")
    require(live["state"] == "open" and not live["merged"])
    for side in ("base", "head"):
        for field in ("ref", "sha"):
            require(live[side][field] == pr[side][field])
        require(live[side]["repo"]["full_name"] == REPO)
    require(live["merge_commit_sha"] == env["GITHUB_SHA"])
    require(LABEL in {label["name"] for label in live["labels"]})
    merge = artifact.command(["git", "rev-list", "--parents", "-1", "HEAD"], cwd=artifact.ROOT, env=dict(os.environ)).decode().split()
    require(merge == [env["GITHUB_SHA"], BASE_SHA, pr["head"]["sha"]])
    checks = data(REPO_API + f"/commits/{pr['head']['sha']}/check-runs?per_page=100&filter=latest")
    require(checks["total_count"] <= 100)
    for name in CHECKS:
        matches = [check for check in checks["check_runs"] if check["name"] == name and check["app"]["slug"] == "github-actions"]
        require(len(matches) == 1 and matches[0]["head_sha"] == pr["head"]["sha"] and matches[0]["conclusion"] == "success")
    approvals = [item for item in pages(REPO_API + f"/issues/{pr['number']}/events")
                 if item["event"] == "labeled" and item["label"]["name"] == LABEL]
    require(len(approvals) == 1 and approvals[0]["actor"]["login"] == OWNER)
    return pr


def one_attempt(env):
    current = data(REPO_API + "/actions/runs/" + env["GITHUB_RUN_ID"])
    require(current["head_branch"] == HEAD and current["run_attempt"] == 1)
    require(current["head_sha"] == json.loads(Path(env["GITHUB_EVENT_PATH"]).read_text())["pull_request"]["head"]["sha"])
    history = data(REPO_API + f"/actions/workflows/{current['workflow_id']}/runs?per_page=100")
    require(history["total_count"] == len(history["workflow_runs"]) <= 100)
    require(any(str(run["id"]) == env["GITHUB_RUN_ID"] for run in history["workflow_runs"]))
    for run in history["workflow_runs"]:
        if str(run["id"]) != env["GITHUB_RUN_ID"]:
            jobs = data(REPO_API + f"/actions/runs/{run['id']}/jobs?filter=all&per_page=100&page=1")
            entries = jobs["jobs"]
            # Fail closed at the page boundary; never accept a partial attempt history.
            require(isinstance(entries, list) and type(jobs["total_count"]) is int
                    and 0 < jobs["total_count"] == len(entries) < 100)
            require(all(type(job["id"]) is int and job["id"] > 0
                        and type(job["run_id"]) is int and job["run_id"] == run["id"]
                        and job["status"] == "completed" and job["conclusion"] == "skipped"
                        for job in entries))
            require(len({job["id"] for job in entries}) == len(entries))


def registry_token(token=None):
    headers = {}
    if token:
        headers["Authorization"] = "Basic " + base64.b64encode((OWNER + ":" + token).encode()).decode()
    scope = "repository:" + NAME + (":pull,push" if token else ":pull")
    status, _, raw = fetch("https://ghcr.io/token?" + urllib.parse.urlencode({"service": "ghcr.io", "scope": scope}), headers)
    if not token and status in (401, 403):
        permission_denied(status, raw)
        return None
    require(status == 200)
    value = json.loads(raw)["token"]
    require(isinstance(value, str) and value and not any(c.isspace() for c in value))
    return value


def permission_denied(status, raw):
    errors = json.loads(raw)["errors"]
    require(status in (401, 403) and isinstance(errors, list) and errors)
    require(all(error["code"] in ("UNAUTHORIZED", "DENIED") for error in errors))


def absent(token, bearer):
    # REST404 alone may hide an inaccessible package. Require an authenticated,
    # typed registry NAME_UNKNOWN as well; DENIED/UNAUTHORIZED/opaque404 block.
    status, _, _ = fetch(PACKAGE_API, {"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json"})
    require(status == 404)
    status, _, raw = fetch("https://ghcr.io/v2/" + NAME + "/tags/list", {"Authorization": "Bearer " + bearer})
    require(status == 404 and [error["code"] for error in json.loads(raw)["errors"]] == ["NAME_UNKNOWN"])


def private_package(token, digest, tag):
    package = data(PACKAGE_API, token)
    require(package["name"] == PACKAGE and package["package_type"] == "container")
    require(package["owner"]["login"] == OWNER and package["visibility"] == "private")
    require(package["repository"]["full_name"] == REPO and package["version_count"] == 1)
    versions = data(PACKAGE_API + "/versions?per_page=100", token)
    require(len(versions) == 1 and versions[0]["name"] == digest and versions[0]["metadata"]["container"]["tags"] == [tag])


def manifest(reference, bearer, iid, expected_digest):
    status, headers, raw = fetch("https://ghcr.io/v2/" + NAME + "/manifests/" + reference,
                                {"Authorization": "Bearer " + bearer, "Accept": ", ".join(MANIFEST_TYPES)})
    require(status == 200)
    advertised = next((value for key, value in headers.items() if key.lower() == "docker-content-digest"), None)
    require(advertised == expected_digest == "sha256:" + hashlib.sha256(raw).hexdigest())
    value = json.loads(raw)
    require(value["schemaVersion"] == 2 and value["mediaType"] in MANIFEST_TYPES)
    require(value["config"]["digest"] == iid and value["layers"])


def anonymous_denied(digest):
    bearer = registry_token()
    if bearer is not None:
        status, _, raw = fetch("https://ghcr.io/v2/" + NAME + "/manifests/" + digest,
                             {"Authorization": "Bearer " + bearer, "Accept": ", ".join(MANIFEST_TYPES)})
        permission_denied(status, raw)  # Challenge401 alone,404/rate limits are NOT proof.


def docker(args, env, stdin=None):
    return subprocess.run(["docker", *args], cwd=artifact.ROOT, env=env, input=stdin,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True, timeout=1800).stdout


def publish(event, env, evidence, token):
    require(token and artifact.SECRET not in os.environ and "GHCR_JOB_TOKEN" not in os.environ)
    pr = source(event, env)
    one_attempt(env)
    require(evidence["status"] == "PASS" and evidence["commit"] == env["GITHUB_SHA"])
    iid = evidence["localImageId"]
    require(DIGEST.fullmatch(iid) and type(evidence["layers"]) is int and evidence["layers"] > 0)
    bearer = registry_token(token)
    absent(token, bearer)
    tag = "canary-" + pr["head"]["sha"]
    tagged = IMAGE + ":" + tag
    with tempfile.TemporaryDirectory(prefix="hp91-auth-", dir=env["RUNNER_TEMP"]) as folder:
        Path(folder).chmod(0o700)
        child_env = dict(os.environ, DOCKER_CONFIG=folder)
        docker(["login", "ghcr.io", "--username", OWNER, "--password-stdin"], child_env, token.encode())
        config = Path(folder) / "config.json"
        require(config.is_file() and not config.is_symlink())
        config.chmod(0o600)
        loaded = json.loads(docker(["image", "inspect", iid], child_env))
        require(len(loaded) == 1 and loaded[0]["Id"] == iid)
        docker(["image", "tag", iid, tagged], child_env)
        require(json.loads(docker(["image", "inspect", tagged], child_env))[0]["Id"] == iid)
        source(event, env)
        absent(token, bearer)
        pushed = docker(["image", "push", tagged], child_env).decode()
        digests = re.findall(r"digest: (sha256:[0-9a-f]{64}) size: [0-9]+", pushed)
        require(len(digests) == 1)
        digest = digests[0]
        manifest(tag, bearer, iid, digest)
        manifest(digest, bearer, iid, digest)
        immutable = IMAGE + "@" + digest
        docker(["image", "pull", "--platform", "linux/amd64", immutable], child_env)
        pulled = json.loads(docker(["image", "inspect", immutable], child_env))
        require(len(pulled) == 1 and pulled[0]["Id"] == iid and immutable in pulled[0]["RepoDigests"])
        private_package(token, digest, tag)
        anonymous_denied(digest)
        source(event, env)
    return {"status": "PASS", "sourceSha": pr["head"]["sha"], "mergeSha": env["GITHUB_SHA"],
            "localImageId": iid, "registryDigest": digest, "image": immutable, "visibility": "PRIVATE",
            "anonymousAccess": "DENIED", "packageUrl": "https://github.com/users/" + OWNER + "/packages/container/package/" + PACKAGE}


def proof_path(env):
    root = Path(env["RUNNER_TEMP"]).resolve(strict=True)
    require(root != artifact.ROOT.resolve() and artifact.ROOT.resolve() not in root.parents)
    return root / ("hp91-proof-" + env["GITHUB_RUN_ID"])


def main():
    license_text = os.environ.pop(artifact.SECRET, "")
    token = os.environ.pop("GHCR_JOB_TOKEN", "")
    try:
        require(sys.argv[1:] in (["prepare"], ["publish"]))
        env = dict(os.environ)
        event = json.loads(Path(env["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
        gate(event, env)
        proof = proof_path(env)
        if sys.argv[1] == "prepare":
            require(not token)
            source(event, env)
            one_attempt(env)
            evidence = artifact.validate(artifact.ROOT, env["RUNNER_TEMP"], env["GITHUB_SHA"], license_text)
            source(event, env)
            proof.mkdir(mode=0o700)
            (proof / "scan.json").write_text(json.dumps(evidence), encoding="utf-8")
            (proof / "scan.json").chmod(0o400)
            print("PASS: exact-IID licensed build/scan prepared; nothing published.")
        else:
            require(not license_text and proof.is_dir() and not proof.is_symlink())
            try:
                evidence = json.loads((proof / "scan.json").read_text())
                result = publish(event, env, evidence, token)
            finally:
                (proof / "scan.json").chmod(0o600)  # Windows synthetic tests also remove readonly proofs.
                (proof / "scan.json").unlink()
                proof.rmdir()
            rendered = json.dumps(result, sort_keys=True)
            with open(env["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
                stream.write("```json\n" + rendered + "\n```\n")
            print(rendered)
    except Exception:
        print("BLOCKED: private canary failed; inspect operator prerequisites; never retry publication automatically.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
