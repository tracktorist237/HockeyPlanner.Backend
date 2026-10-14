"""Actual Docker29/containerd and Compose2.40.3 proof on isolated hosted daemons.

Loads the exact licensed, scanned archive into empty and compressed-layer-populated
stores. Creates (NEVER starts) a Compose container. No SSH, VPS or publication.
"""
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import uuid

import validate_docker_artifact as artifact
from image_delivery_identity import (decode, descriptor, inspect_archive, OCI_CONFIG,
                                     OCI_INDEX, OCI_LAYER, OCI_MANIFEST)

DIND = "docker@sha256:173f284a4299164772a90f52b373e73e087583c0963f1334c9995f190ef6f3f5"
COMPOSE_URL = "https://github.com/docker/compose/releases/download/v2.40.3/docker-compose-linux-x86_64"
COMPOSE_SHA = "dba9d98e1ba5bfe11d88c99b9bd32fc4a0624a30fafe68eea34d61a3e42fd372"


def run(args):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          timeout=1800).stdout


def render(value):
    return json.dumps(value, separators=(",", ":")).encode()


def compressed_seed(source, identity, private):
    """Prepopulate the SAME first DiffID, actually unpacked from a gzip OCI layer."""
    compressed = private / "seed-layer.gz"
    with tarfile.open(source, "r:") as archive:
        saved = decode(archive.extractfile("manifest.json").read())[0]
        with archive.extractfile(saved["Layers"][0]) as raw, compressed.open("xb") as output:
            os.chmod(compressed, 0o600)
            with gzip.GzipFile(fileobj=output, mode="wb", mtime=0) as gz:
                while chunk := raw.read(1024 * 1024):
                    gz.write(chunk)
    config = render({"architecture": "amd64", "os": "linux", "config": {},
                     "rootfs": {"type": "layers", "diff_ids": [identity.diff_ids[0]]}})
    with compressed.open("rb") as stream:
        layer = {"mediaType": OCI_LAYER + "+gzip",
                 "digest": "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest(),
                 "size": compressed.stat().st_size}
    manifest = render({"schemaVersion": 2, "mediaType": OCI_MANIFEST,
                       "config": descriptor(OCI_CONFIG, config), "layers": [layer]})
    target = descriptor(OCI_MANIFEST, manifest)
    files = {"oci-layout": render({"imageLayoutVersion": "1.0.0"}),
             "index.json": render({"schemaVersion": 2, "mediaType": OCI_INDEX, "manifests": [target]}),
             "blobs/sha256/" + descriptor(OCI_CONFIG, config)["digest"][7:]: config,
             "blobs/sha256/" + target["digest"][7:]: manifest}
    seed = private / "seed.tar"
    with seed.open("xb") as output:
        os.chmod(seed, 0o600)
        with tarfile.open(fileobj=output, mode="w") as archive:
            for name, raw in files.items():
                member = tarfile.TarInfo(name); member.size = len(raw); member.mode = 0o600
                archive.addfile(member, io.BytesIO(raw))
            member = tarfile.TarInfo("blobs/sha256/" + layer["digest"][7:])
            member.size, member.mode = layer["size"], 0o600
            with compressed.open("rb") as stream:
                archive.addfile(member, stream)
    return seed


def prove(source, evidence):
    identity = inspect_archive(source, evidence["localImageId"])
    with tempfile.TemporaryDirectory(prefix="hp92-probe-", dir=source.parent) as folder:
        private = Path(folder)
        os.chmod(private, 0o700)
        compose_binary = private / "compose"
        with urllib.request.urlopen(COMPOSE_URL, timeout=60) as response, compose_binary.open("xb") as output:
            output.write(response.read(100 * 1024**2))
        if hashlib.sha256(compose_binary.read_bytes()).hexdigest() != COMPOSE_SHA:
            raise ValueError("Unverified Compose binary")
        seed = compressed_seed(source, identity, private)
        for populated in (False, True):
            cid = None
            try:
                cid = run(["docker", "run", "--detach", "--privileged", "--network", "none",
                           "--env", "DOCKER_TLS_CERTDIR=", DIND, "dockerd",
                           "--host=unix:///var/run/docker.sock", "--tls=false",
                           "--feature=containerd-snapshotter=true"]).decode().strip()
                if not re.fullmatch(r"[0-9a-f]{64}", cid):
                    raise ValueError("Missing owned sandbox ID")
                # Readiness retries are bounded and contact only our local sandbox.
                import time
                for attempt in range(30):
                    try:
                        server = decode(run(["docker", "exec", cid, "docker", "version", "--format", "{{json .Server}}"]))
                        break
                    except subprocess.CalledProcessError:
                        if attempt == 29:
                            raise
                        time.sleep(1)
                if server["Version"] != "29.1.3" or server["Os"] != "linux":
                    raise ValueError("Unexpected sandbox engine")
                components = {item["Name"]: item["Version"] for item in server["Components"]}
                if components["runc"] != "1.3.4" or components["containerd"] != "v2.2.0":
                    raise ValueError("Unexpected sandbox runtime")
                info = decode(run(["docker", "exec", cid, "docker", "info", "--format", "{{json .}}"] ))
                if info["Driver"] != "overlayfs" or info["DriverStatus"] != [["driver-type", "io.containerd.snapshotter.v1"]]:
                    raise ValueError("Unsupported sandbox store")
                if populated:
                    run(["docker", "cp", str(seed), cid + ":/seed.tar"])
                    run(["docker", "exec", cid, "docker", "image", "load", "--input", "/seed.tar"])
                run(["docker", "cp", str(source), cid + ":/image.tar"])
                loaded = run(["docker", "exec", cid, "docker", "image", "load", "--input", "/image.tar"])
                match = re.fullmatch(r"Loaded image ID: (sha256:[0-9a-f]{64})", loaded.decode().strip())
                if match is None:
                    raise ValueError("Ambiguous sandbox import")
                loaded_id = match[1]
                run(["docker", "exec", cid, "docker", "image", "save", "--output", "/export.tar", loaded_id])
                exported = private / ("populated.tar" if populated else "empty.tar")
                run(["docker", "cp", cid + ":/export.tar", str(exported)])
                proof = inspect_archive(exported, identity.config_id, loaded_id=loaded_id,
                                        expected_diff_ids=identity.diff_ids)
                proof.verify_inspect(decode(run(["docker", "exec", cid, "docker", "image", "inspect", loaded_id])))
                run(["docker", "cp", str(compose_binary), cid + ":/compose"])
                run(["docker", "exec", cid, "chmod", "700", "/compose"])
                if run(["docker", "exec", cid, "/compose", "version", "--short"]).strip() != b"2.40.3":
                    raise ValueError("Unexpected Compose version")
                config = private / "compose.json"
                config.write_bytes(render({"services": {"backend": {"image": loaded_id,
                    "init": False, "pull_policy": "never", "network_mode": "none"}}}))
                run(["docker", "cp", str(config), cid + ":/compose.json"])
                project = "hp92-proof-" + uuid.uuid4().hex
                run(["docker", "exec", cid, "/compose", "--project-name", project, "--file", "/compose.json",
                     "create", "--no-build", "--pull", "never", "backend"])
                container = decode(run(["docker", "exec", cid, "docker", "container", "inspect", project + "-backend-1"]))[0]
                if (container["Image"] != loaded_id or container["Config"]["Image"] != loaded_id
                        or container["HostConfig"]["Init"] is not False or container["State"]["Status"] != "created"):
                    raise ValueError("Compose selected unverified image")
            finally:
                if cid is not None and re.fullmatch(r"[0-9a-f]{64}", cid):
                    run(["docker", "rm", "--force", "--volumes", cid])


def main():
    license_text = os.environ.pop(artifact.SECRET, "")
    try:
        evidence = artifact.validate(artifact.ROOT, os.environ["RUNNER_TEMP"], os.environ["GITHUB_SHA"],
                                     license_text, on_verified=prove)
    except Exception:
        print("BLOCKED: licensed archive or isolated Docker29 identity proof failed.", file=sys.stderr)
        return 1
    print(json.dumps({**evidence, "docker29EmptyAndPopulatedStore": "PASS", "compose2403CreateOnly": "PASS"}, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
