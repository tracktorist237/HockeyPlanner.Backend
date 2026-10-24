"""Fixed SSH receiver. No source updates, license, tags, build or auto-recovery.

An operator must prepare the exact clean checkout, image-only Compose override,
and protected approval record. Missing prerequisites reject before reading stdin.
"""
import argparse
import copy
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys

import deploy_imagesharp as deploy
from check_imagesharp_build import (LICENSE, ROOT, dependency_services, require,
                                    runtime_model, runtime_mounts)
from check_imagesharp_image import inspect_received_rootfs
from image_delivery_identity import decode, inspect_archive
from transaction import CURRENT, Cancelled, admission, supervised

MAX_ARCHIVE = 2 * 1024**3
PROTOCOL = "hp92-flock-recovery-v1"
GIT = ["git", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null",
       "-c", "core.untrackedCache=false"]


def checkout(repo, expected):
    require(deploy.command(GIT + ["rev-parse", "HEAD"], repo).decode().strip() == expected)
    require(not deploy.command(GIT + ["status", "--porcelain", "--untracked-files=all"], repo).strip())


class ReadOnly:
    def __init__(self, fd):
        self.fd = fd

    def run(self, args, cwd=None):
        prefixes = (GIT,
                    deploy.UBUNTU_PACKAGE_QUERY,
                    ["docker", "version"], ["docker", "info"],
                    ["docker", "ps"], ["docker", "container", "inspect"],
                    ["docker", "volume", "inspect"], ["docker", "compose"])
        require(any(args[:len(prefix)] == prefix for prefix in prefixes))
        if args[0] == "dpkg-query":
            require(args == deploy.UBUNTU_PACKAGE_QUERY)
        if args[0] == "git":
            require(args[len(GIT):] in (["rev-parse", "HEAD"], ["status", "--porcelain", "--untracked-files=all"]))
        if args[:2] == ["docker", "compose"]:
            require(args[-2:] == ["version", "--short"] or args[-3:] == ["config", "--format", "json"])
        env = dict(os.environ, GIT_OPTIONAL_LOCKS="0")
        return subprocess.run(args, cwd=cwd, env=env, check=True, pass_fds=(self.fd,),
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60).stdout


def image_only(config, expected, volume_inspector):
    require(config["name"] == deploy.PROJECT)
    require(not os.path.lexists(LICENSE))
    for name in dependency_services(config):
        service = config["services"][name]
        require("build" not in service)
        require(not any("license" in key.lower() or "sixlabors" in key.lower()
                        for key in service.get("environment", {})))
        runtime_mounts(config, name, None, volume_inspector)
    backend = config["services"]["backend"]
    runtime_model(backend)
    require(backend["environment"]["APP_COMMIT"] == expected)


def approval(path, expected):
    deploy.protected(path, 0o600)
    require(path.is_file())
    record = decode(path.read_bytes())
    require(record == {
        "protocol": PROTOCOL, "sourceSha": expected,
        "allWritersCooperate": True, "frontendCooperates": True,
        "ownerApprovedTransferLoadDeploy": True})
    require(all(record[field] is True for field in
                ("allWritersCooperate", "frontendCooperates", "ownerApprovedTransferLoadDeploy")))
    return deploy.digest(path)


def preflight(root, expected, lock_fd, lock_path):
    deploy.held_lock(lock_fd, lock_path)
    admission(lock_path)
    require(root.is_dir() and not root.is_symlink())
    repo = root / "backend-src"
    require(repo.is_dir() and not repo.is_symlink())
    checkout(repo, expected)
    approved = lock_path.parent / "hp92-operator-approval.json"
    approved_hash = approval(approved, expected)
    inputs = [root / "docker-compose.yml", root / "compose.ci-image.yml"]
    for path in inputs:
        require(path.is_file() and not path.is_symlink())
    deploy.protected(inputs[1], 0o600)
    original = [deploy.digest(path) for path in inputs]
    base = deploy.compose(root, inputs)
    base.remove("--env-file")
    base.remove(os.devnull)
    config = deploy.json_command(base + ["config", "--format", "json"])
    image_only(config, expected, deploy.volume_inspect)
    profile = deploy.supported_runtime(delivered_image=True)
    before = deploy.container_identity(root, config, profile)
    return repo, approved, approved_hash, inputs, original, base, config, profile, before


def receive(stream, path, size, expected_hash):
    require(type(size) is int and 0 < size <= MAX_ARCHIVE)
    require(re.fullmatch(r"[0-9a-f]{64}", expected_hash) is not None)
    remaining, digest = size, hashlib.sha256()
    with path.open("xb") as output:
        os.chmod(path, 0o600)
        while remaining:
            chunk = stream.read(min(1024 * 1024, remaining))
            require(chunk and len(chunk) <= remaining)
            digest.update(chunk)
            output.write(chunk)
            remaining -= len(chunk)
        require(not stream.read(1))  # Extra/truncated bytes never authorize load.
        output.flush()
        os.fsync(output.fileno())
    require(digest.hexdigest() == expected_hash)
    os.chmod(path, 0o400)


def deliver_locked(stream, expected, archive_sha, size, config_id,
                   lock_fd, root=ROOT, lock_path=deploy.LOCK):
    require(re.fullmatch(r"[0-9a-f]{40}", expected) is not None)
    require(re.fullmatch(r"sha256:[0-9a-f]{64}", config_id) is not None)
    require(type(size) is int and 0 < size <= MAX_ARCHIVE)
    require(re.fullmatch(r"[0-9a-f]{64}", archive_sha) is not None)
    token = CURRENT.set(ReadOnly(lock_fd))
    try:
        initial = preflight(root, expected, lock_fd, lock_path)
    finally:
        CURRENT.reset(token)
    repo, approved, approved_hash, inputs, original, base, config, profile, before = initial
    with supervised(lock_fd, lock_path, expected) as transaction:
        # Every captured prerequisite is checked again before load and replacement.
        path = transaction.private / "image.tar"
        transaction.uncertain = True
        receive(stream, path, size, archive_sha)
        transaction.uncertain = False  # EOF and fsync prove receipt has ended.
        identity = inspect_archive(path, config_id)
        destinations = runtime_mounts(config, "backend", None, deploy.volume_inspect)
        proof = inspect_received_rootfs(path, config_id, destinations)
        proof.verify(config_id, destinations)
        proof.verify_types(deploy.mount_types(config))
        snapshot = copy.deepcopy(config)
        compose_path = transaction.private / "compose.json"
        compose_path.write_bytes(deploy.encoded_config(snapshot))
        os.chmod(compose_path, 0o400)
        compose_hash = deploy.digest(compose_path)
        verified = deploy.compose(root, [compose_path])
        require(deploy.json_command(verified + ["config", "--format", "json"]) == snapshot)

        def unchanged():
            deploy.held_lock(lock_fd, lock_path)
            require(approval(approved, expected) == approved_hash)
            checkout(repo, expected)
            require([deploy.digest(p) for p in inputs] == original)
            require(deploy.json_command(base + ["config", "--format", "json"]) == config)
            image_only(config, expected, deploy.volume_inspect)
            require(deploy.supported_runtime(delivered_image=True) == profile)
            require(deploy.container_identity(root, config, profile) == before)
            require(deploy.digest(compose_path) == compose_hash)
            require(deploy.digest(path) == archive_sha)

        unchanged()
        transaction.uncertain = True
        loaded = deploy.command(["docker", "image", "load", "--input", str(path)])
        match = re.fullmatch(r"Loaded image ID: (sha256:[0-9a-f]{64})", loaded.decode().strip())
        require(match is not None)
        imported_id = match[1]
        exported = transaction.private / "imported.tar"
        exported.touch(mode=0o600, exist_ok=False)
        deploy.command(["docker", "image", "save", "--output", str(exported), imported_id])
        # Re-export from that immutable ID proves its config and ordered raw layers,
        # rather than equating the containerd .Id with the source config IID.
        imported = inspect_archive(exported, config_id, loaded_id=imported_id,
                                   expected_diff_ids=identity.diff_ids)
        imported.verify_inspect(deploy.json_command(["docker", "image", "inspect", imported_id]))
        transaction.uncertain = False
        unchanged()
        # The verified configuration's ONLY replacement is the content-bound ID.
        snapshot["services"]["backend"]["image"] = imported_id
        os.chmod(compose_path, 0o600)
        compose_path.write_bytes(deploy.encoded_config(snapshot))
        os.chmod(compose_path, 0o400)
        compose_hash = deploy.digest(compose_path)
        require(deploy.json_command(verified + ["config", "--format", "json"]) == snapshot)
        unchanged()
        deploy.image_mounts(snapshot, imported_id, None, proof, config_id=config_id)
        transaction.uncertain = True
        deploy.command(verified + ["up", "-d", "--no-build", "--pull", "never", "backend"])
        deploy.post_up(root, snapshot, imported_id, None, proof, profile, config_id=config_id)
        imported.verify_inspect(deploy.json_command(["docker", "image", "inspect", imported_id]))
        deploy.command(verified + ["ps"])
        transaction.uncertain = False
    return {"status": "PASS", "commit": expected, "archiveSHA256": archive_sha,
            "configImageId": config_id, "importedImageId": imported_id}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--expected-sha", required=True)
    parser.add_argument("--archive-sha256", required=True)
    parser.add_argument("--archive-size", type=int, required=True)
    parser.add_argument("--config-id", required=True)
    args = parser.parse_args()
    fd = None
    try:
        # Never provision a missing lock or replace its inode.
        deploy.protected(deploy.LOCK.parent, 0o700)
        deploy.protected(deploy.LOCK, 0o600)
        require(stat.S_ISREG(deploy.LOCK.lstat().st_mode))
        fd = os.open(deploy.LOCK, os.O_RDWR | os.O_NOFOLLOW)
        result = deliver_locked(sys.stdin.buffer, args.expected_sha, args.archive_sha256,
                                args.archive_size, args.config_id, fd)
    except (Exception, Cancelled):
        print("BLOCKED: private image delivery prerequisites or verification failed.", file=sys.stderr)
        return 1
    finally:
        if fd is not None:
            os.close(fd)
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
