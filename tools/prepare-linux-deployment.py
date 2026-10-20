"""Assemble a published Linux host, profiles, packet tables and Compose files without credentials.

Client data is not part of a release: the container mounts the host's client-data directory
(see deploy/docker/compose.external-db.yaml), provisioned separately by the operator.
"""

import argparse
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tarfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--published", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    published = args.published.resolve()
    output = args.output.resolve()
    archive = output.parent / (output.name + ".tar.gz")
    if archive.exists():
        parser.error("Archive already exists; use a fresh release name.")
    if output.exists() and any(output.iterdir()):
        parser.error("Output must be an empty directory.")
    binary = (published / "BubbleFighter.Fantasy.Host").read_bytes()
    if binary[:4] != b"\x7fELF" or int.from_bytes(binary[18:20], "little") not in (62, 183):
        parser.error("Published host must be a Linux x64 or arm64 ELF executable.")
    for name in ("libcoreclr.so", "libhostfxr.so", "libhostpolicy.so", "System.Private.CoreLib.dll"):
        if (published / name).exists():
            parser.error("Framework-dependent publish required; runtime files must not be included.")
    if output == published or published in output.parents or output in published.parents:
        parser.error("Published input and output directories must not overlap.")

    ignore = shutil.ignore_patterns(".env", "*.local.json", "local.patch.yaml", "logs", "__pycache__")
    shutil.copytree(published, output / "server", ignore=ignore)
    # The Host publish already carries profiles/ and packets/; the repository copies replace them.
    shutil.copytree(repo / "profiles", output / "server/profiles", ignore=ignore, dirs_exist_ok=True)
    shutil.copytree(repo / "packets", output / "server/packets", ignore=ignore, dirs_exist_ok=True)
    shutil.copy2(repo / "deploy/docker/Dockerfile.published", output / "Dockerfile")
    shutil.copy2(repo / "deploy/docker/compose.external-db.yaml", output / "compose.yaml")
    # The deployment overlay changes providers and time-zone syntax, never game layouts.
    (output / "server/profiles/all/deployment.patch.yaml").write_text(
        "- replace:\n    id: persistence.mysql\n    disabled: false\n"
        "- replace:\n    id: persistence.memory\n    disabled: true\n"
        "- set:\n    id: diagnostics.packet-log\n    config:\n      file_time_zone: Asia/Shanghai\n",
        encoding="utf-8",
    )
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repo, text=True).strip()
    (output / "SOURCE_COMMIT.txt").write_text(revision + "\n", encoding="utf-8")
    files = sorted(path for path in output.rglob("*") if path.is_file())
    manifest = {path.relative_to(output).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest() for path in files}
    (output / "DEPLOYMENT-MANIFEST.json").write_text(
        json.dumps({"source_commit": revision, "files": manifest}, indent=2), encoding="utf-8"
    )
    # Neither source metadata nor the output filename belongs in a reproducible release.
    with archive.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as package:
                for path in sorted(output.rglob("*"), key=lambda entry: entry.relative_to(output).as_posix()):
                    if not path.is_file():
                        continue
                    name = path.relative_to(output).as_posix()
                    info = tarfile.TarInfo(name)
                    info.size = path.stat().st_size
                    info.mtime = 0
                    info.uid = info.gid = 0
                    info.uname = info.gname = ""
                    info.mode = 0o755 if name == "server/BubbleFighter.Fantasy.Host" else 0o644
                    with path.open("rb") as content:
                        package.addfile(info, content)
    print(f"Archive: {archive}")
    print(f"Files: {len(manifest)}")
    print(f"SHA256: {hashlib.sha256(archive.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
