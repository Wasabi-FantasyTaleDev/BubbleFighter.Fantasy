"""Build the prebuilt server packages for a release: Windows x64/ARM64 zips and Linux x64/arm64 tarballs.

Every package is a framework-dependent publish of the host with its plugins, profiles and packet tables; the
machine that runs it needs the ASP.NET Core Runtime 10. The asset names carry no version, so the stable
address .../releases/latest/download/<asset> always names the newest release. The output directory also
receives install.sh and SHA256SUMS, which the installer checks every download against.
"""

import argparse
import gzip
import hashlib
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import zipfile

# Runtime identifier -> (archive suffix, executable name, expected machine field).
TARGETS = {
    "win-x64": (".zip", "BubbleFighter.Fantasy.Host.exe", 0x8664),
    "win-arm64": (".zip", "BubbleFighter.Fantasy.Host.exe", 0xAA64),
    "linux-x64": (".tar.gz", "BubbleFighter.Fantasy.Host", 62),
    "linux-arm64": (".tar.gz", "BubbleFighter.Fantasy.Host", 183),
}
RUNTIME_FILES = ("coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "libcoreclr.so", "libhostfxr.so",
                 "libhostpolicy.so", "System.Private.CoreLib.dll")
# Operator files that must never ride along in a package, even if a developer left one in the tree.
PRIVATE_NAMES = {".env", "local.patch.yaml", "installation.json", "installation.lock"}
PRIVATE_DIRECTORIES = {"state", "logs", "client-data"}
PACKAGE_ROOT = "bubblefighter-fantasy"


def machine(executable: Path, runtime: str) -> int:
    data = executable.read_bytes()
    if runtime.startswith("win"):
        if data[:2] != b"MZ":
            raise SystemExit(f"{runtime}: the executable is not a PE image")
        offset = int.from_bytes(data[0x3C:0x40], "little")
        return int.from_bytes(data[offset + 4:offset + 6], "little")
    if data[:4] != b"\x7fELF":
        raise SystemExit(f"{runtime}: the executable is not an ELF image")
    return int.from_bytes(data[18:20], "little")


def publish(repo: Path, runtime: str, version: str, output: Path, skip_client: bool) -> None:
    command = ["dotnet", "publish", "src/BubbleFighter.Fantasy.Host", "-c", "Release", "-r", runtime,
               "--self-contained", "false", "-p:PublishTrimmed=false", "-p:PublishAot=false",
               f"-p:Version={version}", "-o", str(output)]
    if skip_client:
        # The first publish compiled the web interface; the others embed the same output.
        command.append("-p:SkipWebUiClientBuild=true")
    subprocess.run(command, cwd=repo, check=True)
    suffix, executable, expected = TARGETS[runtime]
    for name in RUNTIME_FILES:
        if (output / name).exists():
            raise SystemExit(f"{runtime}: the publish bundles a runtime file ({name})")
    found = machine(output / executable, runtime)
    if found != expected:
        raise SystemExit(f"{runtime}: the executable machine is {found:#x}, expected {expected:#x}")
    if not (output / "plugins/BubbleFighter.Fantasy.InGame/BubbleFighter.Fantasy.InGame.dll").exists():
        raise SystemExit(f"{runtime}: the publish is missing the dynamically loaded plugins")


def package_files(published: Path, repo: Path):
    for path in sorted(published.rglob("*"), key=lambda entry: entry.relative_to(published).as_posix()):
        relative = path.relative_to(published)
        if not path.is_file() or path.suffix == ".pdb":
            continue
        if path.name in PRIVATE_NAMES or path.name.endswith(".local.json"):
            raise SystemExit(f"Refusing to package an operator file: {relative.as_posix()}")
        if relative.parts[0] in PRIVATE_DIRECTORIES:
            raise SystemExit(f"Refusing to package an operator directory: {relative.as_posix()}")
        yield relative.as_posix(), path
    yield "LICENSE", repo / "LICENSE"


def write_zip(archive: Path, files) -> None:
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as package:
        for name, path in files:
            info = zipfile.ZipInfo(f"{PACKAGE_ROOT}/{name}", date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            package.writestr(info, path.read_bytes())


def write_tar(archive: Path, files, executable: str) -> None:
    # Fixed times and owners keep two builds of one commit byte-identical.
    with archive.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as package:
                for name, path in files:
                    info = tarfile.TarInfo(f"{PACKAGE_ROOT}/{name}")
                    info.size = path.stat().st_size
                    info.mtime = 0
                    info.uid = info.gid = 0
                    info.uname = info.gname = ""
                    info.mode = 0o755 if name == executable else 0o644
                    with path.open("rb") as content:
                        package.addfile(info, content)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="release version without the leading v, e.g. 0.1.0")
    parser.add_argument("--output", type=Path, required=True, help="empty directory for the release assets")
    parser.add_argument("--runtime", action="append", choices=sorted(TARGETS),
                        help="build only this runtime (repeatable; default: all four)")
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?", args.version):
        parser.error("--version must look like 0.1.0 or 0.1.0-rc.1")
    repo = Path(__file__).resolve().parents[1]
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        parser.error("--output must be an empty directory")
    # Content items glob the working tree, so an untracked profile or table would ship silently.
    dirty = subprocess.check_output(["git", "status", "--porcelain", "--", "src", "profiles", "packets", "data"],
                                    cwd=repo, text=True).strip()
    if dirty:
        parser.error("src, profiles, packets or data has uncommitted or untracked files:\n" + dirty)
    output.mkdir(parents=True, exist_ok=True)
    staging = output / ".publish"
    runtimes = args.runtime or list(TARGETS)
    for index, runtime in enumerate(runtimes):
        published = staging / runtime
        publish(repo, runtime, args.version, published, skip_client=index > 0)
        suffix, executable, _ = TARGETS[runtime]
        archive = output / f"bubblefighter-fantasy-{runtime}{suffix}"
        files = list(package_files(published, repo))
        if suffix == ".zip":
            write_zip(archive, files)
        else:
            write_tar(archive, files, executable)
        print(f"{archive.name}: {len(files)} files, {archive.stat().st_size:,} bytes")
    shutil.rmtree(staging)
    shutil.copy2(repo / "deploy/install.sh", output / "install.sh")
    sums = [f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}"
            for path in sorted(output.iterdir()) if path.is_file()]
    (output / "SHA256SUMS").write_text("\n".join(sums) + "\n", encoding="utf-8", newline="\n")
    print("\n".join(sums))


if __name__ == "__main__":
    main()
