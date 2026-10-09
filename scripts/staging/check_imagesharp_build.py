"""Validate resolved staging Compose privately on stdin; print no configuration.

No network, Docker mutations or license-content reads. Invalid configuration
must fail before build/up. The operator must first confirm existing project identity.
"""
import json
import os
from pathlib import Path, PurePosixPath
import posixpath
import re
import stat
import sys

ROOT = Path("/opt/hockeyplanner-staging")
LICENSE = Path("/etc/hockeyplanner-staging/licenses/sixlabors.lic")
DOCKERFILE = "HockeyPlanner.Backend.WebAPI/Dockerfile.staging"


def require(condition):
    if not condition:
        raise ValueError("Invalid staging build prerequisite")


REQUIRED = ("/app/SixLabors.ImageSharp.dll", "/app/HockeyPlanner.Backend.WebAPI.deps.json")


def target(value):
    require(isinstance(value, str) and value.startswith("/") and "\\" not in value and "\0" not in value)
    require(value == posixpath.normpath(value) and not value.startswith("//"))
    path = PurePosixPath(value)
    require(all(not PurePosixPath(required).is_relative_to(path) for required in REQUIRED))
    return value


def safe_host_path(path, license_file):
    """Bounded identity-only walk; unreadable/symlink/special paths are unsupported.

    No file contents read. Protected paths and cooperative writers must prevent
    new aliases during the transaction; arbitrary privileged writers are outside it.
    """
    require(path.is_absolute())
    original = path
    path = path.resolve(strict=True)
    require(not license_file.resolve(strict=True).is_relative_to(path))
    identity = license_file.stat()
    expected = (identity.st_dev, identity.st_ino)
    pending, count = [(path, 0)], 0
    seen = set()
    while pending:
        current, depth = pending.pop()
        count += 1
        require(count <= 50000 and depth <= 64)
        metadata = current.lstat()
        require(not stat.S_ISLNK(metadata.st_mode))
        require((metadata.st_dev, metadata.st_ino) != expected)
        require(stat.S_ISREG(metadata.st_mode) or stat.S_ISDIR(metadata.st_mode))
        if stat.S_ISDIR(metadata.st_mode):
            key = (metadata.st_dev, metadata.st_ino)
            require(key not in seen)
            seen.add(key)
            with os.scandir(current) as entries:
                for entry in entries:
                    count += 1
                    require(count <= 50000)
                    pending.append((Path(entry.path), depth + 1))
    # Canonical root aliases are permitted only when the inspected identity is safe.
    require(original.resolve(strict=True) == path)


def dependency_services(config):
    pending, checked = ["backend"], set()
    services = config["services"]
    while pending:
        name = pending.pop()
        require(isinstance(name, str) and name in services)
        if name in checked:
            continue
        checked.add(name)
        service = services[name]
        require(isinstance(service, dict) and not service.get("extends"))
        # Inheritance cannot be proven from Compose alone, including external containers.
        require(not service.get("volumes_from"))
        dependencies = service.get("depends_on", {})
        require(isinstance(dependencies, (dict, list)))
        pending.extend(dependencies)
        for field in ("network_mode", "pid", "ipc"):
            value = service.get(field, "")
            require(isinstance(value, str))
            require(not value.startswith("container:"))
            if value.startswith("service:"):
                pending.append(value.split(":", 1)[1])
        pending.extend(link.split(":", 1)[0] for link in service.get("links", []))
    return checked


def runtime_mounts(config, service_name, license_file, volume_inspector=None):
    service = config["services"][service_name]
    require(not service.get("devices") and not service.get("use_api_socket"))
    for category in ("secrets", "configs", "volumes", "tmpfs"):
        require(isinstance(service.get(category, []), list))
    destinations = set()
    def destination(value):
        value = target(value)
        require(value not in destinations)
        destinations.add(value)
    for category in ("secrets", "configs"):
        for reference in service.get(category, []):
            require(isinstance(reference, (str, dict)))
            if isinstance(reference, dict):
                require(set(reference) <= {"source", "target", "uid", "gid", "mode"})
            source = reference if isinstance(reference, str) else reference["source"]
            require(isinstance(source, str) and re.fullmatch(r"[A-Za-z0-9_.-]+", source) is not None)
            default = "/run/secrets/" + source if category == "secrets" else "/" + source
            destination(reference.get("target", default) if isinstance(reference, dict) else default)
            definition = config[category][source]
            require(isinstance(definition, dict) and set(definition) <= {"file", "name"})
            require(isinstance(definition.get("file"), str))
            path = Path(definition["file"])
            require(path.is_absolute() and path.is_file())
            safe_host_path(path, license_file)
    for mount in service.get("volumes", []):
        require(isinstance(mount, dict) and set(mount) <= {"type", "source", "target", "read_only", "bind", "volume", "tmpfs"})
        require("type" in mount and "target" in mount)
        destination(mount["target"])
        kind = mount["type"]
        require(kind in ("bind", "volume", "tmpfs"))
        require(all(option not in mount for option in {"bind", "volume", "tmpfs"} - {kind}))
        require(isinstance(mount.get("read_only", False), bool))
        if kind == "bind":
            require(set(mount.get("bind", {})) <= {"create_host_path"})
            require(isinstance(mount.get("bind", {}).get("create_host_path", False), bool))
            safe_host_path(Path(mount["source"]), license_file)
        elif kind == "tmpfs":
            require("source" not in mount and set(mount.get("tmpfs", {})) <= {"size", "mode"})
        else:
            require(set(mount.get("volume", {})) <= {"nocopy"})
            source = mount["source"]
            definition = config["volumes"][source]
            require(isinstance(definition, dict) and set(definition) <= {"name", "driver", "driver_opts"})
            require(definition.get("driver", "local") == "local")
            options = definition.get("driver_opts", {})
            require(isinstance(options, dict))
            if options:
                require(set(options) == {"type", "o", "device"})
                require(options["type"] == "none" and options["o"] in ("bind", "bind,ro", "bind,rw"))
                safe_host_path(Path(options["device"]), license_file)
            # Existing engine volume may have stale/mismatched options even when
            # Compose says ordinary local. Mandatory read-only engine inspection.
            require(volume_inspector is not None and isinstance(definition.get("name"), str))
            require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", definition["name"]) is not None)
            actual = volume_inspector(definition["name"])
            require(actual["Name"] == definition["name"] and actual["Driver"] == "local" and actual["Scope"] == "local")
            require(actual.get("Options") is None or isinstance(actual["Options"], dict))
            require((actual.get("Options") or {}) == options)
            safe_host_path(Path(actual["Mountpoint"]), license_file)
    for mount in service.get("tmpfs", []):
        require(isinstance(mount, str) and ":" not in mount)
        destination(mount)
    return destinations


def validate(config, root=ROOT, license_file=LICENSE, source_context=None, volume_inspector=None):
    context = (source_context or root / "backend-src").resolve()
    override = root / "compose.imagesharp-license.yml"
    require(override.is_file() and not override.is_symlink())
    require(license_file.is_file() and not license_file.is_symlink())
    require(license_file.stat().st_size > 0 and os.access(license_file, os.R_OK))
    require(not license_file.resolve().is_relative_to(context))
    # Deploy-account ownership and no group/world access on the private file/directory.
    for path, mode in ((license_file, 0o600), (license_file.parent, 0o700)):
        metadata = path.stat()
        require(stat.S_IMODE(metadata.st_mode) == mode and metadata.st_uid == os.geteuid())
    require(config["name"] == "hockeyplanner-staging")
    backend = config["services"]["backend"]
    build = backend["build"]
    require(Path(build["context"]).resolve() == context)
    require(build["dockerfile"] == DOCKERFILE and "dockerfile_inline" not in build)
    require(build.get("target", "final") == "final")
    require(not build.get("args"))
    require(not build.get("additional_contexts"))
    mounts = build["secrets"]
    require(len(mounts) == 1)
    mount = mounts[0]
    require(set(mount) <= {"source", "target"})
    require(mount["source"] == "sixlabors_license")
    require(mount.get("target", "sixlabors_license") == "sixlabors_license")
    require("environment" not in config["secrets"]["sixlabors_license"])
    require(config["secrets"]["sixlabors_license"]["file"] == str(license_file))
    require(not config["secrets"]["sixlabors_license"].get("external"))
    # Resolve the full dependency closure (not secret names); ambiguous references fail closed.
    for name in dependency_services(config):
        service = config["services"][name]
        runtime_mounts(config, name, license_file, volume_inspector)
        require(not any("license" in key.lower() or "sixlabors" in key.lower()
                        for key in service.get("environment", {})))


def main():
    try:
        validate(json.load(sys.stdin))
    except (ValueError, KeyError, TypeError, OSError, AttributeError):
        print("BLOCKED: staging ImageSharp build prerequisites are invalid.", file=sys.stderr)
        return 1
    print("PASS: staging ImageSharp build configuration and private file permissions.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
