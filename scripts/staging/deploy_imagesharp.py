"""Locked staging deploy; private snapshots, immutable source and build result ID.

Invoked only by the authorized SSH workflow after taking the shared host lock.
No configuration, subprocess output or exception data is printed.
"""
import argparse
import copy
from dataclasses import dataclass
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

from check_imagesharp_build import LICENSE, ROOT, validate, require, runtime_model, runtime_mounts, dependency_services, target, safe_host_path
from check_imagesharp_image import inspect as scan_image, ScanResult
from transaction import CURRENT, Cancelled, supervised, admission

LOCK = Path("/var/lib/hockeyplanner-staging/deploy/deploy.lock")
PROJECT = "hockeyplanner-staging"
# Moby23.0.5 oci/defaults.go. Do not accept arbitrary paths even under /proc:
# /proc/self/root and similar kernel links can escape a simple lexical prefix.
MASKED_PATHS = ("/proc/asound", "/proc/acpi", "/proc/kcore", "/proc/keys", "/proc/latency_stats",
                "/proc/timer_list", "/proc/timer_stats", "/proc/sched_debug", "/proc/scsi", "/sys/firmware")
READONLY_PATHS = ("/proc/bus", "/proc/fs", "/proc/irq", "/proc/sys", "/proc/sysrq-trigger")
MASKED_PATHS_29 = (*MASKED_PATHS, "/proc/interrupts", "/sys/devices/virtual/powercap")


@dataclass(frozen=True)
class RuntimeProfile:
    name: str
    masked_paths: tuple
    cpu_masks: bool = False
    status_metadata: bool = False
    containerd_version: str | None = None
    engine_commit: str | None = None
    delivery_only: bool = False


# Deliberate source-reviewed tuples, not independent version allowlists.
RUNTIME_PROFILES = {
    ("23.0.5", "2.17.3", "1.1.5"): RuntimeProfile("legacy", MASKED_PATHS),
    ("29.1.3", "2.40.3", "1.3.4"): RuntimeProfile(
        "docker29", MASKED_PATHS_29, True, True, "v2.2.0"),
    ("29.1.3", "2.40.3+ds1-0ubuntu1~24.04.1", "1.3.4-0ubuntu1~24.04.1"): RuntimeProfile(
        "ubuntu-noble-docker29", MASKED_PATHS_29, True, True, "2.2.1",
        "29.1.3-0ubuntu3~24.04.2", True),
}

UBUNTU_PACKAGES = {
    "docker.io": "29.1.3-0ubuntu3~24.04.2",
    "containerd": "2.2.1-0ubuntu1~24.04.3",
    "runc": "1.3.4-0ubuntu1~24.04.1",
    "docker-compose-v2": "2.40.3+ds1-0ubuntu1~24.04.1",
}
UBUNTU_PACKAGE_QUERY = ["dpkg-query", "--show", "--showformat=${binary:Package}=${Version}\\n",
                        *UBUNTU_PACKAGES]

# runc v1.3.4's vendored runtime-spec Features schema. This is passive
# capability metadata, NEVER a hook list/configuration to execute or trust.
FEATURE_SCHEMA = {
    "ociVersionMin": str, "ociVersionMax": str, "hooks": (str,), "mountOptions": (str,),
    "annotations": "string-map", "potentiallyUnsafeConfigAnnotations": (str,),
    "linux": {
        "namespaces": (str,), "capabilities": (str,),
        "cgroup": {key: bool for key in ("v1", "v2", "systemd", "systemdUser", "rdma")},
        "seccomp": {"enabled": bool, **{key: (str,) for key in
                    ("actions", "operators", "archs", "knownFlags", "supportedFlags")}},
        "apparmor": {"enabled": bool}, "selinux": {"enabled": bool}, "intelRdt": {"enabled": bool},
        "mountExtensions": {"idmap": {"enabled": bool}},
    },
}


def feature_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result)
        result[key] = value
    return result


def feature_shape(value, schema):
    if isinstance(schema, dict):
        require(isinstance(value, dict) and set(value) <= set(schema))
        for key, child in value.items():
            feature_shape(child, schema[key])
    elif schema == "string-map":
        require(isinstance(value, dict))
        for key, child in value.items():
            feature_shape(key, str)
            feature_shape(child, str)
    elif isinstance(schema, tuple):
        require(isinstance(value, list) and all(isinstance(child, str) for child in value))
        require(len(set(value)) == len(value))
        for child in value:
            feature_shape(child, schema[0])
    else:
        require(type(value) is schema)
        if schema is str:
            require(len(value) <= 4096 and "\0" not in value)


def runtime_status(status):
    key = "org.opencontainers.runtime-spec.features"
    require(isinstance(status, dict) and set(status) <= {key})
    if key in status:
        raw = status[key]
        require(isinstance(raw, str) and len(raw) <= 65536)
        features = json.loads(raw, object_pairs_hook=feature_object)
        feature_shape(features, FEATURE_SCHEMA)
        for field in ("ociVersionMin", "ociVersionMax"):
            require(isinstance(features.get(field), str) and
                    re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-dev)?", features[field]) is not None)
        require(set(features.get("hooks", [])) <= {"prestart", "createRuntime", "createContainer", "startContainer", "poststart", "poststop"})


def supported_runtime(*, delivered_image=False):
    """Read-only admission for the source-reviewed Linux OCI mount model.

    Version strings do not attest binaries/hooks: trusted stock daemon/runc and
    protected configuration remain operator prerequisites. Unknown models block.
    """
    server = json_command(["docker", "version", "--format", "{{json .Server}}"])
    compose_version = command(["docker", "compose", "version", "--short"]).decode().strip()
    info = json_command(["docker", "info", "--format", "{{json .}}"])
    packages = (command(UBUNTU_PACKAGE_QUERY) if compose_version == UBUNTU_PACKAGES["docker-compose-v2"]
                else None)
    return runtime_profile(server, compose_version, info, delivered_image=delivered_image, packages=packages)


def runtime_profile(server, compose_version, info, *, delivered_image=False, packages=None):
    """Same admission for live receiver reads and the real isolated CI proof.

    No CI result, environment override or operator attestation can replace these
    daemon reads. Distro suffixes are exact identities, never normalized away.
    """
    require(server["Os"] == "linux" and isinstance(server["Version"], str))
    components = server.get("Components")
    require(isinstance(components, list))
    require(all(isinstance(item, dict) and isinstance(item.get("Name"), str)
                and isinstance(item.get("Version"), str) and item["Name"] and item["Version"]
                for item in components))
    require(len({item["Name"] for item in components}) == len(components))
    runc = [item for item in components if item["Name"] == "runc"]
    require(len(runc) == 1 and isinstance(runc[0]["Version"], str))
    versions = (server["Version"], compose_version, runc[0]["Version"])
    require(versions in RUNTIME_PROFILES)
    profile = RUNTIME_PROFILES[versions]
    require(not profile.delivery_only or delivered_image)
    if profile.engine_commit is not None:
        require(isinstance(packages, bytes))
        observed = packages.decode().splitlines()
        require(len(observed) == len(UBUNTU_PACKAGES)
                and set(observed) == {name + "=" + version for name, version in UBUNTU_PACKAGES.items()})
        require(server.get("GitCommit") == profile.engine_commit and server.get("Arch") == "amd64")
        require({item["Name"] for item in components} == {"Engine", "containerd", "runc", "docker-init"})
        engine = [item for item in components if item["Name"] == "Engine"]
        require(len(engine) == 1 and engine[0]["Version"] == server["Version"])
        require(isinstance(engine[0].get("Details"), dict)
                and engine[0]["Details"].get("GitCommit") == profile.engine_commit)
        # These Ubuntu builds deliberately compile an empty revision. Empty is
        # accepted only for this exact source-reviewed tuple, never as a wildcard.
        for name in ("runc", "containerd", "docker-init"):
            component = [item for item in components if item["Name"] == name]
            require(len(component) == 1 and component[0].get("Details") == {"GitCommit": ""})
        require(next(item["Version"] for item in components if item["Name"] == "docker-init") == "0.19.0")
    require(info["OSType"] == "linux" and info["DefaultRuntime"] == "runc")
    runtime = info["Runtimes"]["runc"]
    require(isinstance(runtime, dict))
    require(set(runtime) <= ({"path", "runtimeArgs", "status"} if profile.status_metadata else {"path", "runtimeArgs"}))
    require(runtime["path"] == "runc")
    require(runtime.get("runtimeArgs", []) == [])
    if "status" in runtime:
        runtime_status(runtime["status"])
    require(isinstance(info["SecurityOptions"], list))
    options = info["SecurityOptions"]
    require(all(isinstance(option, str) for option in options) and len(set(options)) == len(options))
    require(set(options) <= {"name=apparmor", "name=seccomp,profile=builtin", "name=selinux", "name=cgroupns"})
    # The local-build profiles use classic overlay2 config-IID Docker-save semantics.
    # containerd returns manifest/index IDs and blob-layout/compressed exports;
    # this scanner does not attest that different identity model. Block BEFORE build.
    if delivered_image:
        require(profile.containerd_version is not None)
        containerd = [item for item in components if item["Name"] == "containerd"]
        require(len(containerd) == 1 and containerd[0]["Version"] == profile.containerd_version)
        require(info["Driver"] == "overlayfs")
        require(info["DriverStatus"] == [["driver-type", "io.containerd.snapshotter.v1"]])
    else:
        require(info["Driver"] == "overlay2" and isinstance(info["DriverStatus"], list))
        require(all(isinstance(pair, list) and len(pair) == 2 and all(isinstance(value, str) for value in pair)
                    and pair[0] != "driver-type" for pair in info["DriverStatus"]))
    return profile


def container_runtime(data, profile):
    # Evidence must come from HostConfig even when inspect Mounts is empty.
    host = data["HostConfig"]
    require(host.get("Init") is False and host.get("Runtime") == "runc")
    require(host.get("Privileged") is False and not host.get("SecurityOpt"))
    require(not host.get("DeviceRequests") and not host.get("Devices"))
    # runc also installs masked/readonly mounts outside inspect Mounts. Stock
    # defaults are confined to replaced /proc and /sys trees. Exact profile
    # defaults plus documented optional CPU masks; never a proc/sys prefix fallback.
    for field, defaults in (("MaskedPaths", profile.masked_paths), ("ReadonlyPaths", READONLY_PATHS)):
        paths = host.get(field)
        if paths is None:
            continue  # Moby selects this pinned profile's OCI defaults.
        require(isinstance(paths, list) and all(isinstance(path, str) for path in paths))
        require(len(set(paths)) == len(paths) and set(defaults) <= set(paths))
        extra = set(paths) - set(defaults)
        require(not extra or (field == "MaskedPaths" and profile.cpu_masks))
        for path in extra:
            match = re.fullmatch(r"/sys/devices/system/cpu/cpu(0|[1-9][0-9]{0,18})/thermal_throttle", path)
            require(match is not None and int(match[1]) <= 2**63 - 1)


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


def image_mounts(config, iid, license_file, proof, *, config_id=None):
    runtime_model(config["services"]["backend"])
    for service_name in dependency_services(config):
        reference = iid if service_name == "backend" else config["services"][service_name]["image"]
        data = json_command(["docker", "image", "inspect", reference])
        require(len(data) == 1)
        expected = runtime_mounts(config, service_name, license_file, volume_inspect)
        volumes = data[0]["Config"].get("Volumes")
        require(volumes is None or (isinstance(volumes, dict) and all(value == {} for value in volumes.values())))
        if service_name == "backend":
            proof.verify(config_id or iid, expected)
            proof.verify_types(mount_types(config))
            require(data[0]["Id"] == iid and tuple(sorted(volumes or {})) == proof.image_volumes)
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


def mount_types(config):
    result = {}
    for destination, (kind, source) in configured_mounts(config, "backend").items():
        if kind == "bind":
            require(Path(source).is_file() or Path(source).is_dir())
            result[destination] = "dir" if Path(source).is_dir() else "file"
        else:
            result[destination] = "dir"
    return result


def post_up(root, config, iid, license_file, proof, profile, *, config_id=None):
    """Inspect the actual runtime mount set as well as backend IID/labels/state.

    An unexpected daemon result cannot be undone atomically: retain recovery barrier.
    """
    runtime_model(config["services"]["backend"])
    require(supported_runtime(delivered_image=True) == profile if config_id else supported_runtime() == profile)
    container_identity(root, config, profile)
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
            container_runtime(data, profile)
            require(data["Image"] == iid and data["Config"].get("WorkingDir") == "/app")
            if config_id is not None:
                require(data["Config"]["Image"] == iid)
        expected = runtime_mounts(config, service_name, license_file, volume_inspect)
        if service_name == "backend":
            proof.verify(config_id or iid, expected)
            proof.verify_types(mount_types(config))
        bindings = configured_mounts(config, service_name)
        observed = set()
        for mount in data["Mounts"]:
            destination = target(mount["Destination"])
            if service_name == "backend":
                destination = proof.configured_destination(destination)
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
        tmpfs_destinations = set()
        for destination in tmpfs or {}:
            if service_name == "backend":
                destination = proof.configured_destination(destination)
            require(target(destination) in expected and bindings[destination][0] == "tmpfs")
            require(destination not in tmpfs_destinations)
            tmpfs_destinations.add(destination)
            observed.add(destination)
        require(observed == expected)


def container_identity(root, config, profile):
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
    container_runtime(data, profile)
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
    profile = supported_runtime()
    build = config["services"]["backend"]["build"]
    require(set(build) <= {"context", "dockerfile", "secrets", "target"})
    before = container_identity(root, config, profile)
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
        destinations = runtime_mounts(snapshot, "backend", license_file, volume_inspect)
        proof = scan_image(image_archive, license_file, iid, destinations)
        require(isinstance(proof, ScanResult))  # No layer-count/previous-build authorization fallback.
        proof.verify(iid, destinations)
        proof.verify_types(mount_types(snapshot))
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
    image_mounts(snapshot, iid, license_file, proof)
    require(digest(path) == runtime_hash)
    require(json_command(["docker", "image", "inspect", tag])[0]["Id"] == iid)
    require(json_command(["docker", "image", "inspect", iid])[0]["Id"] == iid)
    require(supported_runtime() == profile)
    require(container_identity(root, config, profile) == before)  # Immediately before replacement.
    if build_only:
        return iid
    transaction.uncertain = True  # Cleared only after actual runtime postconditions.
    command(verified + ["up", "-d", "--no-build", "--pull", "never", "backend"])
    post_up(root, snapshot, iid, license_file, proof, profile)
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
