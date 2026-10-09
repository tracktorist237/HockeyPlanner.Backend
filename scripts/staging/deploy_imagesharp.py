"""Locked staging deploy; private snapshots, immutable source and build result ID.

Invoked only by the authorized SSH workflow after taking the shared host lock.
No configuration, subprocess output or exception data is printed.
"""
import argparse
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tarfile
import tempfile
import uuid

from check_imagesharp_build import LICENSE, ROOT, validate, require

LOCK = Path("/var/lib/hockeyplanner-staging/deploy/deploy.lock")
PROJECT = "hockeyplanner-staging"


def command(args, cwd=None):
    return subprocess.run(args, cwd=cwd, check=True, capture_output=True, timeout=1800).stdout


def json_command(args):
    return json.loads(command(args))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def protected(path, mode):
    metadata = path.stat()
    require(not path.is_symlink() and stat.S_IMODE(metadata.st_mode) == mode)
    require(metadata.st_uid == os.geteuid())


def held_lock(fd, path=LOCK):
    import fcntl
    protected(path.parent, 0o700)
    protected(path, 0o600)
    current = path.stat()
    opened = os.fstat(fd)
    require((current.st_dev, current.st_ino) == (opened.st_dev, opened.st_ino))
    fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)


def compose(root, files):
    result = ["docker", "compose", "--project-name", PROJECT, "--project-directory", str(root),
              "--env-file", os.devnull]
    for path in files:
        result.extend(["-f", str(path)])
    return result


def checkout(repo, expected):
    require(command(["git", "rev-parse", "HEAD"], repo).decode().strip() == expected)
    require(not command(["git", "status", "--porcelain", "--untracked-files=all"], repo).strip())


def encoded_config(config):
    # Compose config's serializer already escapes dollars for reuse as input.
    # Copy that resolved model verbatim, and require an exact native round trip.
    return json.dumps(config, sort_keys=True).encode()


def container_identity(root, config):
    ids = command(["docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=" + PROJECT,
                   "--filter", "label=com.docker.compose.service=backend"]).decode().split()
    require(len(ids) == 1)
    data = json_command(["docker", "container", "inspect", ids[0]])[0]
    labels = data["Config"]["Labels"]
    require(labels["com.docker.compose.project"] == PROJECT)
    require(labels["com.docker.compose.service"] == "backend")
    require(Path(labels["com.docker.compose.project.working_dir"]).resolve() == root.resolve())
    require(labels["com.docker.compose.container-number"] == "1")
    require(data["State"]["Running"] is True)
    name = config["services"]["backend"].get("container_name")
    if name:
        require(data["Name"] == "/" + name)
        require(command(["docker", "ps", "-aq", "--filter", "name=^/" + name + "$"]).decode().split() == ids)
    # Ignore changing health logs, but detect replacement/restart/config/network changes.
    stable = {key: data[key] for key in ("Id", "Image", "Name", "Config", "HostConfig")}
    stable["started"] = data["State"]["StartedAt"]
    stable["restarts"] = data["RestartCount"]
    stable["networks"] = data["NetworkSettings"]["Networks"]
    return hashlib.sha256(json.dumps(stable, sort_keys=True).encode()).hexdigest()


def deploy(expected, lock_fd, root=ROOT, license_file=LICENSE, lock_path=LOCK, build_only=False):
    require(re.fullmatch(r"[0-9a-f]{40}", expected) is not None)
    held_lock(lock_fd, lock_path)
    protected(lock_path.parent, 0o700)
    repo = root / "backend-src"
    checkout(repo, expected)
    inputs = [root / "docker-compose.yml", root / "compose.imagesharp-license.yml"]
    for path in inputs:
        require(path.is_file() and not path.is_symlink())
    original = [digest(path) for path in inputs]
    # Base resolution intentionally includes the existing .env, privately in memory.
    base = compose(root, inputs)
    base.remove("--env-file")
    base.remove(os.devnull)
    config = json_command(base + ["config", "--format", "json"])
    validate(config, root, license_file)
    build = config["services"]["backend"]["build"]
    require(set(build) <= {"context", "dockerfile", "secrets", "target"})
    before = container_identity(root, config)
    os.environ["DOCKER_BUILDKIT"] = "1"
    with tempfile.TemporaryDirectory(prefix="transaction-", dir=lock_path.parent) as temporary:
        private = Path(temporary)
        os.chmod(private, 0o700)
        source = private / "source"
        source.mkdir(mode=0o700)
        try:
            archived = command(["git", "archive", "--format=tar", expected], repo)
            with tarfile.open(fileobj=io.BytesIO(archived)) as archive:
                require(all(member.isfile() or member.isdir() for member in archive.getmembers()))
                archive.extractall(source, filter="data")
            source_hashes = {p.relative_to(source): digest(p) for p in source.rglob("*") if p.is_file()}
            for p in source.rglob("*"):
                os.chmod(p, 0o555 if p.is_dir() else 0o444)
            os.chmod(source, 0o555)
            snapshot = copy.deepcopy(config)
            service = snapshot["services"]["backend"]
            service["build"]["context"] = str(source)
            tag = "hockeyplanner-staging-backend-check:" + expected + "-" + uuid.uuid4().hex
            service["image"] = tag
            validate(snapshot, root, license_file, source_context=source)
            path = private / "compose.json"
            path.write_bytes(encoded_config(snapshot))
            os.chmod(path, 0o400)
            verified = compose(root, [path])
            require(json_command(verified + ["config", "--format", "json"]) == snapshot)
            snapshot_hash = digest(path)
            iidfile = private / "image.id"
            # Derive the only supported build fields from the verified configuration.
            # iidfile binds the builder result independently of a mutable tag.
            command(["docker", "buildx", "build", "--load", "--no-cache", "--iidfile", str(iidfile),
                     "--secret", "id=sixlabors_license,src=" + str(license_file),
                     "--target", "final", "-f", str(source / build["dockerfile"]), "-t", tag, str(source)])
            iid = iidfile.read_text().strip()
            require(re.fullmatch(r"sha256:[0-9a-f]{64}", iid) is not None)
            require(json_command(["docker", "image", "inspect", tag])[0]["Id"] == iid)
            require(json_command(["docker", "image", "inspect", iid])[0]["Id"] == iid)
            require(digest(path) == snapshot_hash)
            # Single reviewed configuration: only the verified result's image binding changes.
            service["image"] = iid
            os.chmod(path, 0o600)
            path.write_bytes(encoded_config(snapshot))
            os.chmod(path, 0o400)
            require(json_command(verified + ["config", "--format", "json"]) == snapshot)
            runtime_hash = digest(path)
            checkout(repo, expected)
            require([digest(p) for p in inputs] == original)
            require(json_command(base + ["config", "--format", "json"]) == config)
            require({p.relative_to(source): digest(p) for p in source.rglob("*") if p.is_file()} == source_hashes)
            held_lock(lock_fd, lock_path)
            validate(snapshot, root, license_file, source_context=source)
            require(digest(path) == runtime_hash)
            require(json_command(["docker", "image", "inspect", tag])[0]["Id"] == iid)
            require(json_command(["docker", "image", "inspect", iid])[0]["Id"] == iid)
            require(container_identity(root, config) == before)  # Immediately before replacement.
            if build_only:
                return iid
            command(verified + ["up", "-d", "--no-build", "--pull", "never", "backend"])
            command(verified + ["ps"])
            return iid
        finally:
            # Private source was made read-only; restore owner access for safe cleanup.
            for directory in [source, *[p for p in source.rglob("*") if p.is_dir()]]:
                os.chmod(directory, 0o700)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--expected-sha", required=True)
    parser.add_argument("--lock-fd", type=int, required=True)
    parser.add_argument("--build-only", action="store_true")
    args = parser.parse_args()
    try:
        iid = deploy(args.expected_sha, args.lock_fd, build_only=args.build_only)
    except (ValueError, KeyError, TypeError, OSError, AttributeError, ImportError, subprocess.SubprocessError, tarfile.TarError):
        print("BLOCKED: staging deployment transaction failed.", file=sys.stderr)
        return 1
    print(json.dumps({"status": "PASS", "mode": "build-only" if args.build_only else "deploy", "imageId": iid}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
