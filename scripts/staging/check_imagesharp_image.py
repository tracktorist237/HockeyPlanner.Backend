"""Offline Docker-save inspection, mandatory in locked build/deploy transactions.

Reads private license in memory for scanning; print no license/matched bytes.
Does not start containers, contact Docker/network, or extract archive files.
"""
import argparse
from collections import deque
from dataclasses import dataclass
import io
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import sys
import tarfile
from check_imagesharp_build import target, require


def patterns(raw):
    text = raw.decode("utf-8-sig").strip()
    if not text:
        raise ValueError("Empty license")
    fragments = {text, *[line.strip() for line in text.splitlines() if len(line.strip()) >= 32]}
    for token in re.findall(r"[A-Za-z0-9_+/=\-]{32,}", text):
        fragments.update((token, token[:32], token[-32:]))  # Fixed-width tar fields may truncate payloads.
    values = set()
    for value in fragments:
        for encoded in (value, json.dumps(value, ensure_ascii=True)[1:-1]):
            for encoding in ("utf-8", "utf-16-le", "utf-16-be"):
                values.add(encoded.encode(encoding))
    return values


def clean(stream, needles):
    overlap = max(map(len, needles)) - 1
    tail = b""
    while chunk := stream.read(1024 * 1024):
        data = tail + chunk
        if any(needle in data for needle in needles):
            raise ValueError("Private material detected")
        tail = data[-overlap:] if overlap else b""


# Official NuGet 4.1.2 lib/net8.0 DLL, repository signature verified with
# dotnet nuget verify --all, independently compared with HTTPS flat-container.
TRUSTED_DLL_SHA256 = "80bbde81578be31ca7864f303200b0ef188be1b3048a657546bda04afdf394d3"
DEPS = "app/HockeyPlanner.Backend.WebAPI.deps.json"
DLL = "app/SixLabors.ImageSharp.dll"

# These trees/files are replaced by Docker/runc independently of Compose mounts.
# Do not prove a custom lookup through a tree the runtime can replace first.
RUNTIME_ROOTS = ("/dev", "/proc", "/sys", "/etc/hosts", "/etc/hostname", "/etc/resolv.conf")


@dataclass(frozen=True)
class MountProof:
    destination: str
    effective: str
    lookups: tuple
    kind: str | None


def resolve_mount(filesystem, destination):
    """Component lookup in the final IMAGE namespace, never the host filesystem.

    Absolute links restart at container root; relative links start at their parent.
    Dotdot is applied after preceding links, clamped at root like Linux IN_ROOT.
    Absent ordinary paths can be created; absent link referents are unsupported.
    """
    target(destination)
    require(len(destination) <= 4096)
    require(filesystem.get(".", ("dir",))[0] == "dir")
    pending, current, lookups = deque(destination.split("/")[1:]), [], set()
    links, steps, inside_link = 0, 0, 0
    kind = "dir"
    while pending:
        component = pending.popleft()
        if component is None:  # End of this expanded symlink's referent.
            inside_link -= 1
            continue
        steps += 1
        require(steps <= 4096 and kind == "dir")
        if component in ("", "."):
            continue
        if component == "..":
            if current:
                current.pop()
            continue
        path = "/".join([*current, component])
        require(len(path) < 4096)
        lookups.add("/" + path)
        entry = filesystem.get(path)
        if entry is None:
            require(inside_link == 0)  # Dangling link: runtime creation semantics vary.
            current.append(component)
            kind = "dir"  # Only ordinary new mountpoint paths are supported.
        elif entry[0] == "symlink":
            links += 1
            require(links <= 40)
            link = entry[1]
            require(isinstance(link, str) and 0 < len(link) <= 4096 and "\0" not in link and "\\" not in link)
            require(not link.startswith("//"))
            if link.startswith("/"):
                current = []
            inside_link += 1
            pending.extendleft(reversed([*link.split("/"), None]))
        else:
            require(entry[0] in ("dir", "file"))  # Hardlink identity/type is unproven here.
            current.append(component)
            kind = entry[0]
    effective_path = "/" + "/".join(current)
    target(effective_path)
    entry = filesystem.get(effective_path[1:])
    return MountProof(destination, effective_path, tuple(sorted(lookups)), entry[0] if entry else None)


def under(path, ancestor):
    return path == ancestor or path.startswith(ancestor + "/")


def mount_proofs(filesystem, destinations):
    proofs = tuple(resolve_mount(filesystem, destination) for destination in sorted(set(destinations)))
    implicit = tuple(resolve_mount(filesystem, path) for path in RUNTIME_ROOTS)
    require(all(proof.effective == proof.destination for proof in implicit))
    require(all(proof.kind in (None, "dir" if proof.destination in ("/dev", "/proc", "/sys") else "file")
                for proof in implicit))
    for proof in proofs:
        for other in (*proofs, *implicit):
            if other is proof:
                continue
            # Includes lexical symlink nodes visited before expansion, not only
            # the final pathname. An earlier mount must not change any lookup.
            require(not any(under(path, other.effective) for path in proof.lookups))
            require(not under(other.effective, proof.effective))
    return proofs


@dataclass(frozen=True)
class ScanResult:
    image_id: str
    layers: int
    destinations: tuple
    image_volumes: tuple
    mounts: tuple

    def verify(self, image_id, destinations):
        require(self.image_id == image_id and self.layers > 0)
        require(self.destinations == tuple(sorted(destinations)))

    def configured_destination(self, observed):
        target(observed)
        matches = [proof.destination for proof in self.mounts
                   if observed in (proof.destination, proof.effective) and proof.destination in self.destinations]
        require(len(matches) == 1)
        return matches[0]

    def verify_types(self, types):
        require(tuple(sorted(types)) == self.destinations)
        for proof in self.mounts:
            if proof.destination in types:
                require(types[proof.destination] in ("file", "dir"))
                require(proof.kind is None or proof.kind == types[proof.destination])


def metadata_clean(member, needles):
    clean(io.BytesIO(json.dumps({key: getattr(member, key) for key in
        ("name", "linkname", "uname", "gname", "uid", "gid", "mode", "mtime", "size", "devmajor", "devminor", "pax_headers")},
        sort_keys=True).encode()), needles)


def name(value):
    path = PurePosixPath(value)
    if path.is_absolute() or ".." in path.parts or "\\" in value:
        raise ValueError("Unsupported layer path")
    return str(path).removeprefix("./")


def remove(filesystem, target, descendants_only=False):
    for path in list(filesystem):
        if (path == target and not descendants_only) or path.startswith(target + "/") or target == ".":
            del filesystem[path]


def layer_entries(files, needles):
    entries, deletions = {}, []
    for member in files:
        metadata_clean(member, needles)
        path = name(member.name)
        basename = PurePosixPath(path).name
        if basename.lower() in ("sixlabors.lic", "sixlabors_license"):
            raise ValueError("Secret file in layer")
        if path in entries:
            raise ValueError("Ambiguous duplicate layer path")
        if basename.startswith(".wh."):
            if not member.isfile() or member.size:
                raise ValueError("Unsupported whiteout")
            parent = str(PurePosixPath(path).parent)
            target = parent if basename == ".wh..wh..opq" else str(PurePosixPath(parent) / basename[4:])
            deletions.append((target, basename == ".wh..wh..opq"))
        elif member.isfile():
            content = files.extractfile(member)
            payload = content.read()
            clean(io.BytesIO(payload), needles)
            entries[path] = ("file", hashlib.sha256(payload).hexdigest(), payload if path == DEPS else None)
        elif member.isdir():
            entries[path] = ("dir", None, None)
        elif member.issym() or member.islnk():
            # Never execute or dereference image links on the host. Required runtime
            # paths/ancestors must be ordinary files/directories for a proven PASS.
            entries[path] = ("symlink" if member.issym() else "hardlink", member.linkname, None)
        else:
            raise ValueError("Unsupported layer entry type")
    return entries, deletions


def effective(filesystem, entries, deletions):
    # Whiteouts affect lower layers, regardless of tar ordering in this layer.
    for target, opaque in deletions:
        remove(filesystem, target, descendants_only=opaque)
    for path in sorted(entries, key=lambda p: (len(PurePosixPath(p).parts), p)):
        entry = entries[path]
        if entry[0] != "dir" or filesystem.get(path, ("dir",))[0] != "dir":
            remove(filesystem, path)
        for ancestor in reversed(PurePosixPath(path).parents):
            ancestor = str(ancestor)
            if ancestor != ".":
                if filesystem.get(ancestor, ("dir",))[0] != "dir":
                    raise ValueError("Ambiguous link/file ancestor")
                filesystem.setdefault(ancestor, ("dir", None, None))
        filesystem[path] = entry


def inspect(archive_path, license_path, expected_image_id=None, mount_destinations=()):
    needles = patterns(Path(license_path).read_bytes())
    with tarfile.open(archive_path, "r:*") as archive:
        members = archive.getmembers()
        if len({m.name for m in members}) != len(members):
            raise ValueError("Duplicate image archive member")
        # Historical disclosure scan is independent of final-filesystem existence.
        # Scan outer members too: raw tar headers, manifests, configs, and blobs.
        for member in members:
            metadata_clean(member, needles)
            name(member.name)
            if not member.isfile() and not member.isdir():
                raise ValueError("Unsupported Docker-save outer entry")
            if member.isfile():
                clean(archive.extractfile(member), needles)
        manifest = json.load(archive.extractfile("manifest.json"))
        if len(manifest) != 1 or not manifest[0]["Layers"]:
            raise ValueError("Expected one Docker-save image")
        config_name = manifest[0]["Config"]
        config_bytes = archive.extractfile(config_name).read()
        if config_name != hashlib.sha256(config_bytes).hexdigest() + ".json":
            raise ValueError("Image configuration identity mismatch")
        if expected_image_id is not None and expected_image_id != "sha256:" + config_name[:-5]:
            raise ValueError("Build result and saved image identity differ")
        config = json.loads(config_bytes)
        if config["rootfs"]["type"] != "layers" or len(config["rootfs"]["diff_ids"]) != len(manifest[0]["Layers"]):
            raise ValueError("Incomplete image rootfs")
        if config["config"].get("WorkingDir") != "/app":
            raise ValueError("Unsupported runtime directory")
        volumes = config["config"].get("Volumes")
        if volumes is not None and (not isinstance(volumes, dict) or any(value != {} for value in volumes.values())):
            raise ValueError("Unsupported image mount declaration")
        for destination in volumes or {}:
            target(destination)  # Image-declared volumes must not obscure verified files.
        for entry in config["config"].get("Env", []):
            key = entry.split("=", 1)[0].lower()
            if "license" in key or "sixlabors" in key:
                raise ValueError("Runtime license environment")
        filesystem = {}
        for layer, diff_id in zip(manifest[0]["Layers"], config["rootfs"]["diff_ids"]):
            raw = archive.extractfile(layer)
            digest = hashlib.file_digest(raw, "sha256").hexdigest()
            if diff_id != "sha256:" + digest:
                raise ValueError("Corrupt layer identity or unsupported compressed layer")
            with tarfile.open(fileobj=archive.extractfile(layer), mode="r|*") as files:
                entries, deletions = layer_entries(files, needles)
            effective(filesystem, entries, deletions)
        for required in (DEPS, DLL):
            if filesystem.get(required, (None,))[0] != "file":
                raise ValueError("Required final runtime file missing")
            for parent in PurePosixPath(required).parents:
                if str(parent) != "." and filesystem.get(str(parent), (None,))[0] != "dir":
                    raise ValueError("Unverified runtime ancestor")
        deps = json.loads(filesystem[DEPS][2])
        versions = [key for key in deps["libraries"] if key.startswith("SixLabors.ImageSharp/")]
        target_versions = {key for target in deps["targets"].values() for key in target
                           if key.startswith("SixLabors.ImageSharp/")}
        runtime_files = [filename for target in deps["targets"].values()
                         for filename in target.get("SixLabors.ImageSharp/4.1.2", {}).get("runtime", {})]
        if (versions != ["SixLabors.ImageSharp/4.1.2"] or target_versions != {"SixLabors.ImageSharp/4.1.2"}
                or not any(filename.endswith("/SixLabors.ImageSharp.dll") for filename in runtime_files)
                or filesystem[DLL][1] != TRUSTED_DLL_SHA256):
            raise ValueError("Untrusted final dependency identity")
        destinations = tuple(sorted(mount_destinations))
        require(len(set(destinations)) == len(destinations))
        declared = tuple(sorted(volumes or {}))
        proofs = mount_proofs(filesystem, (*destinations, *declared))
        require(all(proof.kind in (None, "dir") for proof in proofs if proof.destination in declared))
        return ScanResult("sha256:" + config_name[:-5], len(manifest[0]["Layers"]), destinations, declared, proofs)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image-tar", required=True)
    parser.add_argument("--license-file", required=True)
    parser.add_argument("--expected-image-id", required=True)
    args = parser.parse_args()
    try:
        result = inspect(args.image_tar, args.license_file, args.expected_image_id)
    except (ValueError, KeyError, TypeError, OSError, AttributeError, tarfile.TarError):
        print("BLOCKED: private image/layer verification failed.", file=sys.stderr)
        return 1
    print(f"PASS: {result.layers} layers scanned; historical leakage scan clean; final deps and trusted ImageSharp 4.1.2 DLL verified.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
