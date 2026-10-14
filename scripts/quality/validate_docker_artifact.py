"""Hosted CI only: licensed local build -> exact config IID -> offline scan.

No registry output, tags, credentials, deployment, or scanner bypass options.
Raw subprocess diagnostics stay in memory and are never printed.
"""
import json
import hashlib
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "staging"))
import check_imagesharp_image as scanner
from image_delivery_identity import inspect_archive

ROOT = Path(__file__).resolve().parents[2]
SECRET = "SIXLABORS_LICENSE_KEY"


def command(args, *, cwd, env):
    return subprocess.run(args, cwd=cwd, env=env, check=True,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=1800).stdout


def validate(root, temp_root, commit, license_text, run=command, on_verified=None):
    root, temp_root = Path(root).resolve(strict=True), Path(temp_root).resolve(strict=True)
    if (not re.fullmatch(r"[0-9a-f]{40}", commit) or not license_text.strip()
            or temp_root == root or root in temp_root.parents
            or any(character in str(temp_root) for character in (",", "\n", "\r"))):
        raise ValueError("Invalid CI inputs")
    env = dict(os.environ)
    env.pop(SECRET, None)
    if run(["git", "rev-parse", "HEAD"], cwd=root, env=env).decode().strip() != commit:
        raise ValueError("Checkout mismatch")
    with tempfile.TemporaryDirectory(prefix="hp90-", dir=temp_root) as folder:
        private = Path(folder)
        private.chmod(0o700)
        license_file = private / "sixlabors.lic"
        with license_file.open("x", encoding="utf-8") as stream:
            os.chmod(license_file, 0o600)
            stream.write(license_text)
        config = private / "docker-config"
        config.mkdir(mode=0o700)
        env["DOCKER_CONFIG"] = str(config)  # Empty: no registry credentials or reused builder.
        builder = "hp90-" + uuid.uuid4().hex
        iid_file, image_tar = private / "image.iid", private / "image.tar"
        try:
            run(["docker", "buildx", "create", "--name", builder, "--driver", "docker-container"], cwd=root, env=env)
            run(["docker", "buildx", "build", "--builder", builder,
                 "--file", "HockeyPlanner.Backend.WebAPI/Dockerfile.staging",
                 "--platform", "linux/amd64", "--load", "--no-cache", "--pull",
                 "--provenance=false", "--sbom=false", "--iidfile", str(iid_file),
                 "--secret", "id=sixlabors_license,src=" + str(license_file), "."], cwd=root, env=env)
            iid = iid_file.read_text(encoding="utf-8").strip()
            if not re.fullmatch(r"sha256:[0-9a-f]{64}", iid):
                raise ValueError("Missing immutable IID")
            observed = json.loads(run(["docker", "image", "inspect", iid], cwd=root, env=env))
            if not isinstance(observed, list) or len(observed) != 1 or observed[0]["Id"] != iid:
                raise ValueError("Loaded image mismatch")
            run(["docker", "image", "save", "--output", str(image_tar), iid], cwd=root, env=env)
            result = scanner.inspect(image_tar, license_file, expected_image_id=iid)
            if result.image_id != iid:
                raise ValueError("Scan identity mismatch")
            # Only allowlisted evidence leaves this process; never raw scanner metadata.
            evidence = {"status": "PASS", "commit": commit, "localImageId": iid, "layers": result.layers}
        finally:
            # Removal failure also fails the check. The hosted VM is disposable on cancellation.
            run(["docker", "buildx", "rm", "--force", builder], cwd=root, env=env)
        if on_verified is not None:
            # No second build/save: only these very bytes passed the mandatory
            # historical/DLL/rootfs scanner. The source config IID is distinct
            # from the manifest ID which the Docker29 receiver must prove.
            inspect_archive(image_tar, iid)
            with image_tar.open("rb") as stream:
                archive_sha = hashlib.file_digest(stream, "sha256").hexdigest()
            os.chmod(image_tar, 0o400)
            license_file.unlink()  # No license is available to SSH or the receiver.
            on_verified(image_tar, {**evidence, "archiveSHA256": archive_sha,
                                     "archiveSize": image_tar.stat().st_size})
    return evidence


def main():
    # Remove the step-scoped value before launching any subprocess.
    license_text = os.environ.pop(SECRET, "")
    try:
        evidence = validate(ROOT, os.environ["RUNNER_TEMP"], os.environ["GITHUB_SHA"], license_text)
        rendered = json.dumps(evidence, sort_keys=True)
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as stream:
                stream.write("Local Docker artifact (not a registry digest):\n\n```json\n" + rendered + "\n```\n")
    except Exception:
        print("BLOCKED: licensed local Docker artifact validation failed.", file=sys.stderr)
        return 1
    print(rendered)
    return 0


if __name__ == "__main__":
    sys.exit(main())
