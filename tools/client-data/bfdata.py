#!/usr/bin/env python3
"""Fill client-data/<build>/data and dataraw from an installed game client.

data/     the client's archives (.bfz, .pkn of any layout) the server reads, copied whole at their
          original client-relative paths and letter case (each archive's keys derive from that path).
dataraw/  loose files unpacked from archives that are mostly models, textures or sounds, at their
          client paths; the server reads dataraw before data.
sources.tsv records where every file came from.

Run without arguments for the interactive menu, or:
    python tools/client-data/bfdata.py --build kr-812 --client "D:/Games/BubbleFighter" [--out client-data] [--yes]
    python tools/client-data/bfdata.py --list
Which folders are copied or unpacked, and which files must be present, is in src/BubbleFighter.Fantasy.Archives/client-data-builds.json.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
TOOL_PROJECT = REPO / "tools" / "BubbleFighter.Fantasy.Archives.Tool"
BUILD_ID = re.compile(r"^[a-z]{2}-\d+$")


def load_manifest() -> dict:
    with open(REPO / "src" / "BubbleFighter.Fantasy.Archives" / "client-data-builds.json", encoding="utf-8") as f:
        return json.load(f)


def archive_tool() -> list[str]:
    """The bffarchive command line, building the tool once when it is missing."""
    override = os.environ.get("BFF_ARCHIVE_TOOL")
    if override:
        return [override]
    out = TOOL_PROJECT / "bin" / "Release" / "net10.0"
    exe = out / ("bffarchive.exe" if os.name == "nt" else "bffarchive")
    dll = out / "bffarchive.dll"
    if not exe.exists() and not dll.exists():
        print("Building bffarchive (first run)...")
        subprocess.run(["dotnet", "build", str(TOOL_PROJECT), "-c", "Release", "-p:NuGetAudit=false", "-nologo", "-v", "q"],
                       check=True)
    return [str(exe)] if exe.exists() else ["dotnet", str(dll)]


def generate(build: str, client: Path, out_root: Path, assume_yes: bool, ask=input) -> int:
    """Delegate generation to the shared native implementation; keep this script's menu."""
    target = out_root / build
    print(f"Client: {client}\nDestination: {target}")
    if target.exists() and any(target.iterdir()):
        raise SystemExit(f"Destination is not empty: {target}. Choose a new output directory.")
    if not assume_yes and ask("Continue? [y/N]: ").strip().lower() not in ("y", "yes"):
        print("Nothing changed.")
        return 1
    return subprocess.run(archive_tool() + ["generate", build, str(client), str(target)], check=False).returncode


def choose_build(manifest: dict, ask=input) -> str:
    builds = manifest["builds"]
    print("\nWhich client build is this?")
    print("  [0] custom (type the build id)")
    for i, b in enumerate(builds, 1):
        print(f"  [{i}] {b['id']:8} {b['label']} ({b['date']})")
    while True:
        answer = ask("Choose: ").strip()
        if answer.isdigit() and 1 <= int(answer) <= len(builds):
            return builds[int(answer) - 1]["id"]
        if answer == "0":
            custom = ask("Build id (<region>-<ClientVersion_Major>, e.g. cn-705): ").strip().lower()
            if BUILD_ID.match(custom):
                return custom
            print("  That is not a build id like cn-705.")
        else:
            print("  Please type one of the numbers above.")


def interactive() -> int:
    manifest = load_manifest()
    print("bfdata - copy the server's client data out of a game client")
    build = choose_build(manifest)
    while True:
        client = Path(input("Client folder (the one holding BubbleFighter.exe or CrazyShooter.exe): ").strip().strip('"'))
        if client.is_dir():
            break
        print("  That folder does not exist.")
    default_out = Path(os.environ.get("BFF_CLIENT_DATA", REPO / "client-data"))
    out = input(f"client-data folder [{default_out}]: ").strip().strip('"')
    return generate(build, client, Path(out) if out else default_out, assume_yes=False)


def main(argv: list[str]) -> int:
    if not argv:
        return interactive()
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--build", help="build id, for example kr-812")
    parser.add_argument("--client", type=Path, help="the installed client's folder")
    parser.add_argument("--out", type=Path, default=Path(os.environ.get("BFF_CLIENT_DATA", REPO / "client-data")),
                        help="the client-data folder (default: BFF_CLIENT_DATA or <repo>/client-data)")
    parser.add_argument("--yes", action="store_true", help="do not ask before generating into an empty build directory")
    parser.add_argument("--list", action="store_true", help="list the known builds")
    args = parser.parse_args(argv)
    if args.list:
        for b in load_manifest()["builds"]:
            print(f"{b['id']:8} {b['label']} ({b['date']})")
        return 0
    if not args.build or not args.client:
        parser.error("--build and --client are required (or run without arguments for the menu)")
    if not BUILD_ID.match(args.build):
        parser.error("--build must look like cn-705")
    if not args.client.is_dir():
        parser.error(f"{args.client} is not a folder")
    return generate(args.build, args.client, args.out, args.yes)


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        print("\nCancelled.")
        sys.exit(130)
