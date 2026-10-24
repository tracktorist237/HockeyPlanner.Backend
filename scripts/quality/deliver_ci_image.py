"""Private verified archive to fixed pinned SSH receiver. Develop only.

All staging credentials are removed from the environment BEFORE any build tool.
There is no arbitrary archive, remote command, host-key discovery or retry option.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

import validate_docker_artifact as artifact
from image_delivery_identity import decode

NAMES = ("VPS_HOST", "VPS_USER", "VPS_SSH_KEY", "STAGING_KNOWN_HOSTS")
RECEIVER = "/opt/hockeyplanner-staging/backend-src/scripts/staging/receive_ci_image.py"


def transport(path, evidence, credentials, run=subprocess.run):
    host, user, key, known_hosts = (credentials[name] for name in NAMES)
    if (not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}", host)
            or not re.fullmatch(r"[a-z_][a-z0-9_-]{0,31}", user)
            or not key.strip() or not known_hosts.strip()):
        raise ValueError("Missing private SSH prerequisites")
    if sys.platform != "linux":
        raise ValueError("Dedicated hosted Linux runner required")
    expected = {"status": "PASS", "commit": evidence["commit"],
                "archiveSHA256": evidence["archiveSHA256"],
                "configImageId": evidence["localImageId"]}
    if (evidence["status"] != "PASS"
            or not re.fullmatch(r"[0-9a-f]{40}", expected["commit"])
            or not re.fullmatch(r"[0-9a-f]{64}", expected["archiveSHA256"])
            or any(not re.fullmatch(r"sha256:[0-9a-f]{64}", expected[field])
                   for field in ("configImageId",))
            or type(evidence["archiveSize"]) is not int or not 0 < evidence["archiveSize"] <= 2 * 1024**3
            or path.is_symlink() or path.stat().st_size != evidence["archiveSize"]):
        raise ValueError("Invalid scanned archive evidence")
    with path.open("rb") as payload:
        if hashlib.file_digest(payload, "sha256").hexdigest() != expected["archiveSHA256"]:
            raise ValueError("Scanned archive changed")
        payload.seek(0)
        with tempfile.TemporaryDirectory(prefix="hp92-ssh-", dir=path.parent) as folder:
            private = Path(folder)
            os.chmod(private, 0o700)
            key_path, hosts_path = private / "identity", private / "known_hosts"
            for target, value in ((key_path, key), (hosts_path, known_hosts)):
                with target.open("x", encoding="utf-8") as output:
                    os.chmod(target, 0o600)
                    output.write(value)
                    if not value.endswith("\n"):
                        output.write("\n")
            # Exact hostname must already be present in the trusted secret; no
            # ssh-keyscan, acceptance prompt or implicit global SSH configuration.
            env = {name: os.environ[name] for name in ("PATH", "HOME") if name in os.environ}
            pin = run(["ssh-keygen", "-F", host, "-f", str(hosts_path)], env=env,
                      check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15)
            if not pin.stdout.strip():
                raise ValueError("No pinned key for the deployment host")
            args = ["ssh", "-F", "none", "-T", "-i", str(key_path),
                    "-o", "BatchMode=yes", "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none",
                    "-o", "StrictHostKeyChecking=yes", "-o", "UpdateHostKeys=no",
                    "-o", "GlobalKnownHostsFile=/dev/null", "-o", "UserKnownHostsFile=" + str(hosts_path),
                    "-o", "PasswordAuthentication=no", "-o", "PreferredAuthentications=publickey",
                    "-o", "ProxyCommand=none", "-o", "ProxyJump=none", "-o", "ConnectTimeout=15",
                    "-o", "ForwardAgent=no", "-o", "ClearAllForwardings=yes", "-o", "LogLevel=ERROR",
                    user + "@" + host, "/usr/bin/python3", RECEIVER,
                    "--expected-sha", expected["commit"], "--archive-sha256", expected["archiveSHA256"],
                    "--archive-size", str(evidence["archiveSize"]), "--config-id", expected["configImageId"]]
            received = run(args, env=env, stdin=payload, check=True,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=1800)
            result = decode(received.stdout)
            if (not isinstance(result, dict) or set(result) != {*expected, "importedImageId"}
                    or not isinstance(result["importedImageId"], str)
                    or not re.fullmatch(r"sha256:[0-9a-f]{64}", result["importedImageId"])
                    or {name: result[name] for name in expected} != expected):
                raise ValueError("Unverified deployment result")
    return result


def main():
    # Read values only into process memory; none is printed or passed as an
    # environment variable, build argument or license value to a child process.
    credentials = {name: os.environ.pop(name, "") for name in NAMES}
    license_present = bool(os.environ.pop(artifact.SECRET, ""))
    try:
        commit = os.environ["GITHUB_SHA"]
        if (os.environ.get("GITHUB_EVENT_NAME") != "push"
                or os.environ.get("GITHUB_REF") != "refs/heads/develop"
                or os.environ.get("GITHUB_REPOSITORY") != "tracktorist237/HockeyPlanner.Backend"
                or os.environ.get("HP92_APPROVED_HEAD") != commit or license_present or os.name != "posix"):
            raise ValueError("Separate exact-source owner approval required")
        private = Path(os.environ["HP92_VERIFIED_DIRECTORY"])
        temp_root = Path(os.environ["RUNNER_TEMP"]).resolve(strict=True)
        if (private.is_symlink() or private.resolve(strict=True).parent != temp_root
                or not re.fullmatch(r"hp92-verified-[A-Za-z0-9_-]+", private.name)
                or private.stat().st_uid != os.geteuid()
                or private.stat().st_mode & 0o777 != 0o700
                or {item.name for item in private.iterdir()} != {"image.tar", "verified.json"}):
            raise ValueError("Invalid private archive directory")
        for name, mode in (("image.tar", 0o400), ("verified.json", 0o600)):
            item = private / name
            if (item.is_symlink() or not item.is_file() or item.stat().st_uid != os.geteuid()
                    or item.stat().st_mode & 0o777 != mode):
                raise ValueError("Invalid private archive ownership")
        evidence = decode((private / "verified.json").read_bytes())
        if evidence["commit"] != commit:
            raise ValueError("Verified source changed")
        result = transport(private / "image.tar", evidence, credentials)
        (private / "image.tar").unlink()
        (private / "verified.json").unlink()
        private.rmdir()
    except Exception:
        print("BLOCKED: private scanned-image delivery is not authorized or verified.", file=sys.stderr)
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
