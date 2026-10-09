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
import sys
import tarfile
import uuid

from check_imagesharp_build import LICENSE, ROOT, validate, require, runtime_mounts, dependency_services, target, safe_host_path
from check_imagesharp_image import inspect as scan_image
from transaction import CURRENT, Cancelled, supervised, admission

LOCK = Path("/var/lib/hockeyplanner-staging/deploy/deploy.lock")
PROJECT = "hockeyplanner-staging"


def command(args, cwd=None):
    require(CURRENT.get() is not None)
    return CURRENT.get().run(args, cwd)


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


def volume_inspect(name):
    result = json_command(["docker", "volume", "inspect", name])
    require(len(result) == 1)
    return result[0]


def image_mounts(config, iid, license_file):
    for service_name in dependency_services(config):
        reference = iid if service_name == "backend" else config["services"][service_name]["image"]
        data = json_command(["docker", "image", "inspect", reference])
        require(len(data) == 1)
        expected = runtime_mounts(config, service_name, license_file, volume_inspect)
        volumes = data[0]["Config"].get("Volumes")
        require(volumes is None or (isinstance(volumes, dict) and all(value == {} for value in volumes.values())))
        for destination in volumes or {}:
            require(target(destination) in expected)  # No unexplained anonymous image mount.


def configured_mounts(config, service_name):
    result = {}
    service = config["services"][service_name]
    for mount in service.get("volumes", []):
        kind = mount["type"]
        source = mount.get("source")
        if kind == "volume":
            source = config["volumes"][source]["name"]
        result[mount["target"]] = (kind, source)
    for category in ("secrets", "configs"):
        for reference in service.get(category, []):
            source = reference if isinstance(reference, str) else reference["source"]
            default = "/run/secrets/" + source if category == "secrets" else "/" + source
            destination = reference.get("target", default) if isinstance(reference, dict) else default
            result[destination] = ("bind", config[category][source]["file"])
    for destination in service.get("tmpfs", []):
        result[destination] = ("tmpfs", None)
    return result


def post_up(root, config, iid, license_file):
    """Inspect the actual runtime mount set as well as backend IID/labels/state.

    An unexpected daemon result cannot be undone atomically: retain recovery barrier.
    """
    container_identity(root, config)
    for service_name in dependency_services(config):
        ids = command(["docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=" + PROJECT,
                       "--filter", "label=com.docker.compose.service=" + service_name]).decode().split()
        require(len(ids) == 1)
        data = json_command(["docker", "container", "inspect", ids[0]])[0]
        labels = data["Config"]["Labels"]
        require(labels["com.docker.compose.project"] == PROJECT and labels["com.docker.compose.service"] == service_name)
        require(labels["com.docker.compose.container-number"] == "1")
        require(Path(labels["com.docker.compose.project.working_dir"]).resolve() == root.resolve())
        require(data["State"]["Running"] is True)
        if service_name == "backend":
            require(data["Image"] == iid and data["Config"].get("WorkingDir") == "/app")
        expected = runtime_mounts(config, service_name, license_file, volume_inspect)
        bindings = configured_mounts(config, service_name)
        observed = set()
        for mount in data["Mounts"]:
            destination = target(mount["Destination"])
            require(destination in expected and destination not in observed)
            observed.add(destination)
            require(mount["Type"] in ("bind", "volume", "tmpfs"))
            require(mount["Type"] == bindings[destination][0])
            if mount["Type"] == "volume":
                require(mount["Name"] == bindings[destination][1])
                actual = volume_inspect(mount["Name"])
                require(actual["Driver"] == "local" and actual["Scope"] == "local")
                safe_host_path(Path(actual["Mountpoint"]), license_file)
            elif mount["Type"] == "bind":
                require(Path(mount["Source"]).resolve(strict=True) == Path(bindings[destination][1]).resolve(strict=True))
                safe_host_path(Path(mount["Source"]), license_file)
        tmpfs = data["HostConfig"].get("Tmpfs")
        require(tmpfs is None or isinstance(tmpfs, dict))
        for destination in tmpfs or {}:
            require(target(destination) in expected and bindings[destination][0] == "tmpfs")
            observed.add(destination)
        require(observed == expected)


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
    admission(lock_path)
    with supervised(lock_fd, lock_path, expected) as transaction:
        return deploy_locked(expected, lock_fd, root, license_file, lock_path, build_only, transaction)


def deploy_locked(expected, lock_fd, root, license_file, lock_path, build_only, transaction):
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
    validate(config, root, license_file, volume_inspector=volume_inspect)
    build = config["services"]["backend"]["build"]
    require(set(build) <= {"context", "dockerfile", "secrets", "target"})
    before = container_identity(root, config)
    os.environ["DOCKER_BUILDKIT"] = "1"
    private = transaction.private
    os.chmod(private, 0o700)
    source = private / "source"
    source.mkdir(mode=0o700)
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
    validate(snapshot, root, license_file, source_context=source, volume_inspector=volume_inspect)
    path = private / "compose.json"
    path.write_bytes(encoded_config(snapshot))
    os.chmod(path, 0o400)
    verified = compose(root, [path])
    require(json_command(verified + ["config", "--format", "json"]) == snapshot)
    snapshot_hash = digest(path)
    iidfile = private / "image.id"
    # Derive the only supported build fields from the verified configuration.
    # iidfile binds the builder result independently of a mutable tag.
    transaction.uncertain = True
    command(["docker", "buildx", "build", "--load", "--no-cache", "--iidfile", str(iidfile),
             "--secret", "id=sixlabors_license,src=" + str(license_file),
             "--target", "final", "-f", str(source / build["dockerfile"]), "-t", tag, str(source)])
    transaction.uncertain = False  # Successful synchronous --load completed.
    iid = iidfile.read_text().strip()
    require(re.fullmatch(r"sha256:[0-9a-f]{64}", iid) is not None)
    require(json_command(["docker", "image", "inspect", tag])[0]["Id"] == iid)
    require(json_command(["docker", "image", "inspect", iid])[0]["Id"] == iid)
    image_archive = private / "image.tar"  # Outside frozen source/Git; parent0700.
    image_archive.touch(mode=0o600, exist_ok=False)
    transaction.uncertain = True  # Export CLI may still be writing on cancellation.
    command(["docker", "image", "save", "-o", str(image_archive), iid])
    transaction.uncertain = False
    require(image_archive.is_file() and not image_archive.is_symlink() and image_archive.stat().st_size > 0)
    os.chmod(image_archive, 0o600)
    try:
        layers = scan_image(image_archive, license_file, iid)  # Mandatory exact-build historical + final validation.
        require(type(layers) is int and layers > 0)
    finally:
        image_archive.unlink()  # Export complete; scanner is synchronous and starts no processes.
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
    validate(snapshot, root, license_file, source_context=source, volume_inspector=volume_inspect)
    image_mounts(snapshot, iid, license_file)
    require(digest(path) == runtime_hash)
    require(json_command(["docker", "image", "inspect", tag])[0]["Id"] == iid)
    require(json_command(["docker", "image", "inspect", iid])[0]["Id"] == iid)
    require(container_identity(root, config) == before)  # Immediately before replacement.
    if build_only:
        return iid
    transaction.uncertain = True  # Cleared only after actual runtime postconditions.
    command(verified + ["up", "-d", "--no-build", "--pull", "never", "backend"])
    post_up(root, snapshot, iid, license_file)
    command(verified + ["ps"])
    transaction.uncertain = False
    return iid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--expected-sha", required=True)
    parser.add_argument("--lock-fd", type=int, required=True)
    parser.add_argument("--build-only", action="store_true")
    args = parser.parse_args()
    try:
        iid = deploy(args.expected_sha, args.lock_fd, build_only=args.build_only)
    except (Cancelled, Exception):
        print("BLOCKED: staging deployment transaction failed.", file=sys.stderr)
        return 1
    print(json.dumps({"status": "PASS", "mode": "build-only" if args.build_only else "deploy", "imageId": iid}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
