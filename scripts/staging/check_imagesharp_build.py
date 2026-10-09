"""Validate resolved staging Compose privately on stdin; print no configuration.

No network, Docker mutations or license-content reads. Invalid configuration
must fail before build/up. The operator must first confirm existing project identity.
"""
import json
import os
from pathlib import Path
import stat
import sys

ROOT = Path("/opt/hockeyplanner-staging")
LICENSE = Path("/etc/hockeyplanner-staging/licenses/sixlabors.lic")
DOCKERFILE = "HockeyPlanner.Backend.WebAPI/Dockerfile.staging"


def require(condition):
    if not condition:
        raise ValueError("Invalid staging build prerequisite")


def validate(config, root=ROOT, license_file=LICENSE):
    context = (root / "backend-src").resolve()
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
    require(mount["source"] == "sixlabors_license")
    require(mount.get("target", "sixlabors_license") == "sixlabors_license")
    require("environment" not in config["secrets"]["sixlabors_license"])
    require(config["secrets"]["sixlabors_license"]["file"] == str(license_file))
    require(not config["secrets"]["sixlabors_license"].get("external"))
    # License is build-only, never a runtime secret, environment value or bind mount.
    require("sixlabors" not in json.dumps(backend.get("secrets", [])).lower())
    require(not any("license" in key.lower() or "sixlabors" in key.lower()
                    for key in backend.get("environment", {})))
    for volume in backend.get("volumes", []):
        require("sixlabors" not in json.dumps(volume).lower())
        source = volume.get("source")
        if source:
            require(not license_file.resolve().is_relative_to(Path(source).resolve()))


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
