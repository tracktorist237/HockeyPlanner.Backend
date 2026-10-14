"""Bind a single tagless, uncompressed Docker-save image to Docker29 load.

This is deliberately limited to the archive model admitted by the HP-90 scanner.
No registry, compressed layer, multi-platform index or tag conversion is supported.
"""
from dataclasses import dataclass
import hashlib
import gzip
import json
from pathlib import PurePosixPath
import re
import tarfile

from check_imagesharp_build import require

DOCKER_MANIFEST = "application/vnd.docker.distribution.manifest.v2+json"
DOCKER_CONFIG = "application/vnd.docker.container.image.v1+json"
DOCKER_LAYER = "application/vnd.docker.image.rootfs.diff.tar"
OCI_MANIFEST = "application/vnd.oci.image.manifest.v1+json"
OCI_CONFIG = "application/vnd.oci.image.config.v1+json"
OCI_LAYER = "application/vnd.oci.image.layer.v1.tar"
OCI_INDEX = "application/vnd.oci.image.index.v1+json"


def object_pairs(pairs):
    value = {}
    for key, child in pairs:
        require(key not in value)
        value[key] = child
    return value


def decode(raw):
    return json.loads(raw, object_pairs_hook=object_pairs)


def sha256(raw):
    return "sha256:" + hashlib.sha256(raw).hexdigest()


def descriptor(media_type, raw):
    return {"mediaType": media_type, "digest": sha256(raw), "size": len(raw)}


@dataclass(frozen=True)
class Identity:
    config_id: str
    imported_id: str
    diff_ids: tuple

    def verify_inspect(self, result):
        require(isinstance(result, list) and len(result) == 1)
        data = result[0]
        require(data["Id"] == self.imported_id)
        require(data["Descriptor"]["digest"] == self.imported_id)
        require(data["Descriptor"]["mediaType"] in (DOCKER_MANIFEST, OCI_MANIFEST))
        require(data["Os"] == "linux" and data["Architecture"] == "amd64")
        require(data["RootFS"] == {"Type": "layers", "Layers": list(self.diff_ids)})


def inspect_archive(path, expected_config_id, *, loaded_id=None, expected_diff_ids=None):
    require(re.fullmatch(r"sha256:[0-9a-f]{64}", expected_config_id) is not None)
    with tarfile.open(path, "r:") as archive:
        members = archive.getmembers()
        require(len({m.name for m in members}) == len(members))
        require(all(m.isfile() or m.isdir() for m in members))
        # containerd uses path.Clean, while Python getmember uses the original
        # spelling. Reject aliases BEFORE load, including ./oci-layout/index.json.
        for member in members:
            name = PurePosixPath(member.name)
            require(not name.is_absolute() and ".." not in name.parts and "\\" not in member.name)
            require(str(name) == member.name)
        def read(name):
            member = archive.getmember(name)
            require(member.isfile() and member.size <= 20 * 1024**2)
            return archive.extractfile(member).read()
        manifest = decode(read("manifest.json"))
        require(isinstance(manifest, list) and len(manifest) == 1)
        saved = manifest[0]
        require(not saved.get("RepoTags"))  # Load must never replace a service tag.
        raw_config = read(saved["Config"])
        require(sha256(raw_config) == expected_config_id)
        config = decode(raw_config)
        require(config["os"] == "linux" and config["architecture"] == "amd64")
        require(config["rootfs"]["type"] == "layers")
        layers, diff_ids = [], []
        for name in saved["Layers"]:
            member = archive.getmember(name)
            require(member.isfile() and 0 < member.size <= 2 * 1024**3)
            with archive.extractfile(member) as stream:
                blob_hash = "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest()
            with archive.extractfile(member) as stream:
                compressed = stream.read(2) == b"\x1f\x8b"
            require(not compressed or loaded_id is not None)
            if compressed:
                raw_hash, raw_size = hashlib.sha256(), 0
                with archive.extractfile(member) as stream, gzip.GzipFile(fileobj=stream) as decoded:
                    while chunk := decoded.read(1024 * 1024):
                        raw_size += len(chunk)
                        require(raw_size <= 2 * 1024**3)
                        raw_hash.update(chunk)
                diff_ids.append("sha256:" + raw_hash.hexdigest())
            else:
                diff_ids.append(blob_hash)
            layers.append({"digest": blob_hash, "size": member.size, "compressed": compressed})
        diff_ids = tuple(diff_ids)
        require(diff_ids and config["rootfs"]["diff_ids"] == list(diff_ids))
        if expected_diff_ids is not None:
            require(diff_ids == expected_diff_ids)
        if "oci-layout" in {m.name for m in members}:
            # containerd consumes the OCI graph, not manifest.json. Both MUST
            # describe the very same bytes scanned by HP-90, without annotations.
            require(decode(read("oci-layout")) == {"imageLayoutVersion": "1.0.0"})
            index = decode(read("index.json"))
            require(set(index) == {"schemaVersion", "mediaType", "manifests"})
            require(index["schemaVersion"] == 2 and index["mediaType"] == OCI_INDEX)
            require(len(index["manifests"]) == 1)
            target = index["manifests"][0]
            require(set(target) == {"mediaType", "digest", "size"})
            require(target["mediaType"] in (OCI_MANIFEST, DOCKER_MANIFEST))
            require(re.fullmatch(r"sha256:[0-9a-f]{64}", target["digest"]) is not None)
            raw_manifest = read("blobs/sha256/" + target["digest"][7:])
            require(target == descriptor(target["mediaType"], raw_manifest))
            image_manifest = decode(raw_manifest)
            config_type, layer_type = ((OCI_CONFIG, OCI_LAYER) if target["mediaType"] == OCI_MANIFEST
                                       else (DOCKER_CONFIG, DOCKER_LAYER))
            require(image_manifest == {
                "schemaVersion": 2, "mediaType": target["mediaType"],
                "config": descriptor(config_type, raw_config),
                "layers": [{"mediaType": layer_type + (("+gzip" if layer_type == OCI_LAYER else ".gzip")
                                                      if layer["compressed"] else ""),
                            "digest": layer["digest"], "size": layer["size"]} for layer in layers]})
            require(read("blobs/sha256/" + sha256(raw_config)[7:]) == raw_config)
            for name, layer in zip(saved["Layers"], layers):
                require(name == "blobs/sha256/" + layer["digest"][7:])
            imported_id = target["digest"]
        else:
            require("index.json" not in {m.name for m in members})
            # containerd v2.2.0 archive importer converts legacy Docker-save to
            # this schema2 manifest using Go's struct JSON field order. No tags.
            raw_manifest = json.dumps({
                "schemaVersion": 2, "mediaType": DOCKER_MANIFEST,
                "config": descriptor(DOCKER_CONFIG, raw_config),
                "layers": [{"mediaType": DOCKER_LAYER, "digest": layer["digest"], "size": layer["size"]}
                           for layer in layers]},
                separators=(",", ":")).encode()
            imported_id = sha256(raw_manifest)
        if loaded_id is not None:
            # Existing compressed blobs can alter legacy import's manifest.
            # Prove the observed ID from the daemon's OCI re-export graph,
            # bound to the source config bytes and each uncompressed DiffID.
            require("oci-layout" in {m.name for m in members})
            require(imported_id == loaded_id)
        return Identity(expected_config_id, imported_id, diff_ids)
