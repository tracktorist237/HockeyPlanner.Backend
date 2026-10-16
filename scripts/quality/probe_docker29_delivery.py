"""Actual upstream and Ubuntu Docker29 proof on isolated hosted daemons.

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
import deploy_imagesharp as deploy
from image_delivery_identity import (decode, descriptor, inspect_archive, OCI_CONFIG,
                                     OCI_INDEX, OCI_LAYER, OCI_MANIFEST)

DIND = "docker@sha256:173f284a4299164772a90f52b373e73e087583c0963f1334c9995f190ef6f3f5"
COMPOSE_URL = "https://github.com/docker/compose/releases/download/v2.40.3/docker-compose-linux-x86_64"
COMPOSE_SHA = "dba9d98e1ba5bfe11d88c99b9bd32fc4a0624a30fafe68eea34d61a3e42fd372"
# Ubuntu noble-updates Packages.xz SHA256 records, authenticated against the
# Ubuntu Archive Automatic Signing Key (2018); provenance/patch review in runbook.
UBUNTU_BASE = "ubuntu@sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55"
UBUNTU_DEBS = (
    ("universe/d/docker.io-app/docker.io_29.1.3-0ubuntu3~24.04.2_amd64.deb", 35558504,
     "bd5c22d42d4ce3021958b7a8d627701597f61a1a9867ba83fe3c841f04991e3a"),
    ("main/c/containerd-app/containerd_2.2.1-0ubuntu1~24.04.3_amd64.deb", 28145484,
     "459a0184cc631c17eb4a625392f749fa753627b4ddba196919e0666a6206d92a"),
    ("main/r/runc-app/runc_1.3.4-0ubuntu1~24.04.1_amd64.deb", 9573850,
     "6ddd64ed7ef1693dd95620a2b1148a4187ef4fe0819c3c3049fad9385ecfd5dc"),
    ("universe/d/docker-compose-v2/docker-compose-v2_2.40.3+ds1-0ubuntu1~24.04.1_amd64.deb", 14788638,
     "192f3df16d0c832e1cbf2362889da56522267835cfb19541241b3dd0ed964039"),
)


def run(args):
    return subprocess.run(args, check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          timeout=1800).stdout


def render(value):
    return json.dumps(value, separators=(",", ":")).encode()


def download(url, target, expected_sha, size=None):
    with urllib.request.urlopen(url, timeout=60) as response, target.open("xb") as output:
        output.write(response.read((size or 100 * 1024**2) + 1))
    if ((size is not None and target.stat().st_size != size)
            or hashlib.sha256(target.read_bytes()).hexdigest() != expected_sha):
        raise ValueError("Unverified isolated test tool")


def ubuntu_image(private):
    # This context has public test packages ONLY, never the scanned image or license.
    context = private / "ubuntu-tools"
    context.mkdir(mode=0o700)
    for name, size, sha in UBUNTU_DEBS:
        download("https://archive.ubuntu.com/ubuntu/pool/" + name, context / Path(name).name, sha, size)
    (context / "Dockerfile").write_text(f"""FROM {UBUNTU_BASE}
COPY *.deb /packages/
RUN printf '#!/bin/sh\\nexit 101\\n' > /usr/sbin/policy-rc.d && chmod 755 /usr/sbin/policy-rc.d \\
    && apt-get update \\
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates /packages/*.deb \\
    && rm -rf /packages /var/lib/apt/lists/*
""", encoding="utf-8")
    iid = private / "ubuntu-tools.iid"
    run(["docker", "build", "--platform", "linux/amd64", "--quiet", "--iidfile", str(iid), str(context)])
    result = iid.read_text().strip()
    if re.fullmatch(r"sha256:[0-9a-f]{64}", result) is None:
        raise ValueError("Missing isolated tool image identity")
    return result


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
             "manifest.json": render([{"Config": "blobs/sha256/" + descriptor(OCI_CONFIG, config)["digest"][7:],
                                      "RepoTags": [], "Layers": ["blobs/sha256/" + layer["digest"][7:]]}]),
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


def snapshot(cid, diff_id, expected=None):
    """Read the committed first-layer chain ID from this owned daemon only."""
    info = decode(run(["docker", "exec", cid, "ctr", "--address",
        "/var/run/docker/containerd/containerd.sock", "--namespace", "moby",
        "snapshots", "--snapshotter", "overlayfs", "info", diff_id]))
    if (set(info) != {"Name", "Kind", "Created", "Updated"}
            or info["Name"] != diff_id or info["Kind"] != "Committed"
            or any(not isinstance(info[key], str) or re.fullmatch(
                r"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,9})?Z", info[key]) is None
                for key in ("Created", "Updated"))):
        raise ValueError("Unverified compressed-layer snapshot")
    if expected is not None and info != expected:
        raise ValueError("Compressed-layer snapshot was replaced")
    return info


def verify_pair(identity, oci_source, pair):
    if oci_source:
        # An OCI import preserves its descriptors, even in a populated store.
        if any(item["importedImageId"] != identity.imported_id for item in pair):
            raise ValueError("OCI source manifest changed")
    elif pair[0]["importedImageId"] == pair[1]["importedImageId"]:
        raise ValueError("Legacy compressed-blob substitution was not exercised")
    if pair[1]["gzipSnapshotReused"] is not True:
        raise ValueError("Compressed-layer snapshot reuse was not exercised")


def prove_store(source, identity, private, seed, image, compose, profile_name, populated, archive_sha):
    cid = None
    store = "gzip-preseeded" if populated else "empty"
    # Fixed phase names only: never print command arguments, image metadata,
    # subprocess output or exception text from the licensed archive.
    def phase(name):
        print("Isolated proof: " + profile_name + "/" + store + "/" + name, flush=True)
    try:
        phase("sandbox-start")
        cid = run(["docker", "run", "--detach", "--privileged", "--network", "none",
                   "--volume", "/var/lib/docker", "--env", "DOCKER_TLS_CERTDIR=", image, "dockerd",
                   "--host=unix:///var/run/docker.sock", "--tls=false", "--iptables=false",
                   "--ip6tables=false", "--bridge=none", "--feature=containerd-snapshotter=true"]).decode().strip()
        if not re.fullmatch(r"[0-9a-f]{64}", cid):
            raise ValueError("Missing owned sandbox ID")
        import time
        for attempt in range(30):
            try:
                server = decode(run(["docker", "exec", cid, "docker", "version", "--format", "{{json .Server}}"]))
                break
            except subprocess.CalledProcessError:
                if attempt == 29:
                    raise
                time.sleep(1)
        if profile_name == "docker29":
            run(["docker", "cp", str(private / "compose"), cid + ":/compose"])
            run(["docker", "exec", cid, "chmod", "700", "/compose"])
        phase("runtime-admission")
        short = run(["docker", "exec", cid, *compose, "version", "--short"]).decode().strip()
        info = decode(run(["docker", "exec", cid, "docker", "info", "--format", "{{json .}}"]))
        packages = (run(["docker", "exec", cid, *deploy.UBUNTU_PACKAGE_QUERY])
                    if profile_name == "ubuntu-noble-docker29" else None)
        profile = deploy.runtime_profile(server, short, info, delivered_image=True, packages=packages)
        if profile.name != profile_name:
            raise ValueError("Mismatched isolated runtime profile")
        deploy.runtime_status({"org.opencontainers.runtime-spec.features":
                               run(["docker", "exec", cid, "runc", "features"]).decode()})
        if populated:
            phase("gzip-seed-proof")
            with tarfile.open(seed, "r:") as archive:
                target = decode(archive.extractfile("index.json").read())["manifests"][0]
                manifest = decode(archive.extractfile("blobs/sha256/" + target["digest"][7:]).read())
                seed_config_id = manifest["config"]["digest"]
                if manifest["layers"][0]["mediaType"] != OCI_LAYER + "+gzip":
                    raise ValueError("Seed is not compressed")
            inspect_archive(seed, seed_config_id, loaded_id=target["digest"],
                            expected_diff_ids=(identity.diff_ids[0],))
            run(["docker", "cp", str(seed), cid + ":/seed.tar"])
            if run(["docker", "exec", cid, "docker", "image", "load", "--input", "/seed.tar"]).decode().strip() != (
                    "Loaded image ID: " + target["digest"]):
                raise ValueError("Seed import identity mismatch")
            run(["docker", "exec", cid, "docker", "image", "save", "--output", "/seed-export.tar", target["digest"]])
            seed_export = private / (profile_name + "-seed-export.tar")
            run(["docker", "cp", cid + ":/seed-export.tar", str(seed_export)])
            seed_proof = inspect_archive(seed_export, seed_config_id, loaded_id=target["digest"],
                                         expected_diff_ids=(identity.diff_ids[0],))
            seed_proof.verify_inspect(decode(run(["docker", "exec", cid, "docker", "image", "inspect", target["digest"]])))
            # Exact seed manifest ID binds its gzip descriptor/blob, not just
            # the uncompressed DiffID. Its unpacked chain must survive load.
            seeded_snapshot = snapshot(cid, identity.diff_ids[0])
        phase("exact-source-load")
        run(["docker", "cp", str(source), cid + ":/image.tar"])
        if run(["docker", "exec", cid, "sha256sum", "/image.tar"]).decode().split()[0] != archive_sha:
            raise ValueError("Sandbox archive changed")
        loaded = run(["docker", "exec", cid, "docker", "image", "load", "--input", "/image.tar"])
        match = re.fullmatch(r"Loaded image ID: (sha256:[0-9a-f]{64})", loaded.decode().strip())
        if match is None:
            raise ValueError("Ambiguous sandbox import")
        loaded_id = match[1]
        if populated:
            snapshot(cid, identity.diff_ids[0], expected=seeded_snapshot)
        phase("exact-source-reexport")
        run(["docker", "exec", cid, "docker", "image", "save", "--output", "/export.tar", loaded_id])
        exported = private / (profile_name + "-" + store + ".tar")
        run(["docker", "cp", cid + ":/export.tar", str(exported)])
        proof = inspect_archive(exported, identity.config_id, loaded_id=loaded_id,
                                expected_diff_ids=identity.diff_ids)
        proof.verify_inspect(decode(run(["docker", "exec", cid, "docker", "image", "inspect", loaded_id])))
        phase("compose-create-only")
        config = private / "compose.json"
        config.write_bytes(render({"services": {"backend": {"image": loaded_id,
            "init": False, "pull_policy": "never", "network_mode": "none"}}}))
        run(["docker", "cp", str(config), cid + ":/compose.json"])
        project = "hp92-proof-" + uuid.uuid4().hex
        base = ["docker", "exec", cid, *compose, "--project-name", project, "--file", "/compose.json"]
        resolved = decode(run(base + ["config", "--format", "json"]))
        config.write_bytes(render(resolved))
        run(["docker", "cp", str(config), cid + ":/compose.json"])
        if decode(run(base + ["config", "--format", "json"])) != resolved:
            raise ValueError("Compose snapshot changed")
        run(base + ["create", "--no-build", "--pull", "never", "backend"])
        container = decode(run(["docker", "exec", cid, "docker", "container", "inspect", project + "-backend-1"]))[0]
        deploy.container_runtime(container, profile)
        if (container["Image"] != loaded_id or container["Config"]["Image"] != loaded_id
                or container["State"]["Status"] != "created" or container["State"]["Running"] is not False
                or container["Mounts"]):
            raise ValueError("Compose selected unverified image/runtime")
        phase("complete")
        return {"profile": profile_name, "store": store, "configImageId": identity.config_id,
                "importedImageId": loaded_id, "archiveSHA256": archive_sha, "composeShort": short,
                "gzipSnapshotReused": populated}
    finally:
        if cid is not None and re.fullmatch(r"[0-9a-f]{64}", cid):
            run(["docker", "rm", "--force", "--volumes", cid])


def prove(source, evidence):
    identity = inspect_archive(source, evidence["localImageId"])
    with tarfile.open(source, "r:") as archive:
        oci_source = "oci-layout" in archive.getnames()
    print("Isolated source format: " + ("OCI" if oci_source else "legacy Docker-save"), flush=True)
    with source.open("rb") as stream:
        archive_sha = hashlib.file_digest(stream, "sha256").hexdigest()
    if archive_sha != evidence["archiveSHA256"]:
        raise ValueError("Scanned archive changed")
    with tempfile.TemporaryDirectory(prefix="hp92-probe-", dir=source.parent) as folder:
        private = Path(folder)
        os.chmod(private, 0o700)
        download(COMPOSE_URL, private / "compose", COMPOSE_SHA)
        seed = compressed_seed(source, identity, private)
        ubuntu = ubuntu_image(private)
        results = []
        for name, image, compose in (("docker29", DIND, ["/compose"]),
                                     ("ubuntu-noble-docker29", ubuntu, ["docker", "compose"])):
            print("Checking isolated compatibility profile: " + name, flush=True)
            pair = [prove_store(source, identity, private, seed, image, compose, name, populated, archive_sha)
                    for populated in (False, True)]
            verify_pair(identity, oci_source, pair)
            results.extend(pair)
        with source.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != archive_sha:
                raise ValueError("Scanned archive changed during proof")
        return results


def main():
    license_text = os.environ.pop(artifact.SECRET, "")
    try:
        proofs = []
        evidence = artifact.validate(artifact.ROOT, os.environ["RUNNER_TEMP"], os.environ["GITHUB_SHA"],
                                     license_text, on_verified=lambda path, proof: proofs.extend(prove(path, proof)))
        if [(p["profile"], p["store"]) for p in proofs] != [
                (name, store) for name in ("docker29", "ubuntu-noble-docker29")
                for store in ("empty", "gzip-preseeded")]:
            raise ValueError("Incomplete isolated compatibility proof")
    except Exception:
        print("BLOCKED: licensed archive or isolated Docker29 identity proof failed.", file=sys.stderr)
        return 1
    print(json.dumps({**evidence, "docker29EmptyAndPopulatedStore": "PASS", "compose2403CreateOnly": "PASS",
                      "ubuntuNobleEmptyAndPopulatedStore": "PASS", "compatibilityProofs": proofs}, sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
