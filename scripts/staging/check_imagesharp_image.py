"""Operator-only offline Docker-save inspection. Print no license/matched bytes.

Reads a private license only when explicitly run by an authorized operator.
Does not start containers, contact Docker/network, or extract archive files.
"""
import argparse
import io
import json
from pathlib import Path
import re
import sys
import tarfile


def patterns(raw):
    text = raw.decode("utf-8-sig").strip()
    if not text:
        raise ValueError("Empty license")
    fragments = {text, *[line.strip() for line in text.splitlines() if len(line.strip()) >= 32]}
    fragments.update(re.findall(r"[A-Za-z0-9_+/=\-]{32,}", text))
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


def inspect(archive_path, license_path):
    needles = patterns(Path(license_path).read_bytes())
    with tarfile.open(archive_path, "r:*") as archive:
        manifest = json.load(archive.extractfile("manifest.json"))
        if len(manifest) != 1 or not manifest[0]["Layers"]:
            raise ValueError("Expected one Docker-save image")
        config_name = manifest[0]["Config"]
        clean(archive.extractfile(config_name), needles)
        config = json.load(archive.extractfile(config_name))
        for entry in config.get("config", {}).get("Env", []):
            name = entry.split("=", 1)[0].lower()
            if "license" in name or "sixlabors" in name:
                raise ValueError("Runtime license environment")
        found_version = False
        for layer in manifest[0]["Layers"]:
            with tarfile.open(fileobj=archive.extractfile(layer), mode="r|*") as files:
                for member in files:
                    if Path(member.name).name.lower() in ("sixlabors.lic", "sixlabors_license"):
                        raise ValueError("Secret file in layer")
                    if member.isfile():
                        stream = files.extractfile(member)
                        if member.name.endswith("HockeyPlanner.Backend.WebAPI.deps.json"):
                            # Also inspect the actual published dependency identity.
                            content = stream.read()
                            clean(io.BytesIO(content), needles)
                            deps = json.loads(content)
                            versions = [name for name in deps["libraries"] if name.startswith("SixLabors.ImageSharp/")]
                            if versions != ["SixLabors.ImageSharp/4.1.2"]:
                                raise ValueError("Incorrect ImageSharp version")
                            found_version = True
                        else:
                            clean(stream, needles)
        if not found_version:
            raise ValueError("Published dependency evidence missing")
        return len(manifest[0]["Layers"])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image-tar", required=True)
    parser.add_argument("--license-file", required=True)
    args = parser.parse_args()
    try:
        count = inspect(args.image_tar, args.license_file)
    except (ValueError, KeyError, TypeError, OSError, AttributeError, tarfile.TarError):
        print("BLOCKED: private image/layer verification failed.", file=sys.stderr)
        return 1
    print(f"PASS: {count} layers scanned; no private license/file match; published ImageSharp 4.1.2.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
