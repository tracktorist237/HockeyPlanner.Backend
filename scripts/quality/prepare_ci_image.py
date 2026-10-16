"""Save only the scanned bytes privately between steps of ONE hosted job."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile

import validate_docker_artifact as artifact


def preserve(path, evidence, temp_root):
    private = Path(tempfile.mkdtemp(prefix="hp92-verified-", dir=temp_root))
    os.chmod(private, 0o700)
    target = private / "image.tar"
    try:
        with path.open("rb") as source, target.open("xb") as output:
            os.chmod(target, 0o600)
            shutil.copyfileobj(source, output, 1024 * 1024)
            output.flush()
            os.fsync(output.fileno())
        with target.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != evidence["archiveSHA256"]:
                raise ValueError("Verified archive copy changed")
        os.chmod(target, 0o400)
        with (private / "verified.json").open("x", encoding="utf-8") as output:
            os.chmod(private / "verified.json", 0o600)
            json.dump(evidence, output, sort_keys=True)
        return private
    except BaseException:
        for item in (target, private / "verified.json"):
            if item.is_file():
                item.unlink()
        private.rmdir()
        raise


def main():
    license_text = os.environ.pop(artifact.SECRET, "")
    try:
        if os.name != "posix":
            raise ValueError("Disposable hosted Linux required")
        temp_root = Path(os.environ["RUNNER_TEMP"]).resolve(strict=True)
        directories = []
        artifact.validate(artifact.ROOT, temp_root, os.environ["GITHUB_SHA"], license_text,
                          on_verified=lambda path, proof: directories.append(preserve(path, proof, temp_root)))
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write("directory=" + str(directories[0]) + "\n")
    except Exception:
        print("BLOCKED: licensed private archive preparation failed.", file=sys.stderr)
        return 1
    print("PASS: exact scanned archive retained privately for this hosted job.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
