"""Exercise the built host bootstrap using fresh processes and real module fixtures."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import threading
import time
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--timeout-seconds", type=int, default=30)
    parser.add_argument("--output-limit-bytes", type=int, default=1048576)
    args = parser.parse_args()
    if args.timeout_seconds < 1 or args.output_limit_bytes < 1024:
        parser.error("Process timeout and output limits must be positive and practical.")
    repository = args.repository.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    host_directory = repository / "src/BubbleFighter.Fantasy.Host/bin" / args.configuration / "net10.0"
    host = host_directory / "BubbleFighter.Fantasy.Host.dll"
    seed_module = host_directory / "BubbleFighter.Fantasy.Loader.Native.dll"
    fixtures = repository / "tests/BubbleFighter.Fantasy.Kernel.Tests/bin" / args.configuration / "net10.0/fixtures"
    module = fixtures / "multi-definition/MultiDefinitionModuleFixture.dll"
    duplicate = fixtures / "multi-definition-duplicate/DuplicateDefinitionModuleFixture.dll"
    for required in (host, seed_module, module, duplicate):
        if not required.is_file():
            raise FileNotFoundError(f"Build the solution and fixtures first: {required}")

    def root_with(name: str, modules: list[tuple[str, Path]]) -> Path:
        root = output / name
        root.mkdir()
        for directory_name, source in modules:
            directory = root / directory_name
            directory.mkdir()
            shutil.copyfile(source, directory / f"{directory_name}.dll")
        return root

    normal = root_with("normal-root", [("MultiDefinitionModuleFixture", module)])
    repeated = root_with("duplicate-root", [("DuplicateDefinitionModuleFixture", duplicate)])
    cross = root_with("cross-root", [("left", module), ("right", module)])
    empty = root_with("empty-root", [])
    excluded = root_with("excluded-root", [("BubbleFighter.Fantasy.Loader.Native", seed_module)])
    ignored = root_with("ignored-directories-root", [])
    (ignored / "ignored-a").mkdir()
    (ignored / "ignored-b").mkdir()
    malformed = output / "malformed-root/BrokenModule"
    malformed.mkdir(parents=True)
    (malformed / "BrokenModule.dll").write_bytes(b"not a managed assembly")

    def profile(name: str, reserve: int = 2, maximum: int = 2, input_name: str = "plugin_roots",
                maximum_scanned_directories: int = 4096) -> Path:
        directory = output / name
        (directory / "list").mkdir(parents=True)
        (directory / "bundles").mkdir()
        (directory / "list/profile.yaml").write_text("id: list\nbundles: [configured]\n", encoding="utf-8")
        (directory / "bundles/configured.yaml").write_text(
            f"""- id: explicitly-configured-source
  plugin: loader.native
  lifecycle:
    effect_capacity: 64
    definition_source_reserve: 1
    definition_reserve: {reserve}
  config:
    roots: '@bootstrap:{input_name}'
    excluded_assembly_names: '@bootstrap:seed_modules'
    source_id: custom-catalog
    maximum_definitions: {maximum}
    maximum_scanned_directories: {maximum_scanned_directories}
""", encoding="utf-8")
        return directory

    results: list[dict[str, object]] = []

    def run(name: str, arguments: list[str], expected_exit: int, expected_ids: set[str] | None = None,
            required_error: str | None = None) -> None:
        case = output / name
        case.mkdir()
        command = ["dotnet", str(host), "plugins", "list", "--log-level", "Error", *arguments]
        failures: list[str] = []
        process = subprocess.Popen(command, cwd=repository, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

        def stop_owned_process() -> None:
            if process.poll() is not None:
                return
            try:
                process.kill()
            except OSError as error:
                if process.poll() is None:
                    failures.append(f"could not stop owned process: {type(error).__name__}")

        def copy_bounded(stream, destination: Path) -> None:
            remaining = args.output_limit_bytes
            try:
                with destination.open("wb") as sink:
                    while data := stream.read(65536):
                        sink.write(data[:remaining])
                        remaining -= len(data)
                        if remaining < 0:
                            failures.append(f"{destination.name} exceeded the output limit")
                            stop_owned_process()
                            break
            except Exception as error:
                failures.append(f"{destination.name} reader failed: {type(error).__name__}")
                stop_owned_process()
            finally:
                try:
                    stream.close()
                except Exception as error:
                    failures.append(f"{destination.name} close failed: {type(error).__name__}")

        readers = [threading.Thread(target=copy_bounded, args=(stream, case / filename), daemon=True)
                   for stream, filename in ((process.stdout, "stdout.log"), (process.stderr, "stderr.log"))]
        started = time.monotonic()
        for reader in readers:
            reader.start()
        try:
            process.wait(timeout=args.timeout_seconds)
        except subprocess.TimeoutExpired:
            failures.append("process timeout")
            stop_owned_process()
            process.wait(timeout=5)
        for reader in readers:
            reader.join(timeout=5)
            if reader.is_alive():
                failures.append("output reader did not finish")
        def read_log(filename: str) -> str:
            try:
                return (case / filename).read_text(encoding="utf-8", errors="replace")
            except OSError as error:
                failures.append(f"could not read {filename}: {type(error).__name__}")
                return ""

        stdout = read_log("stdout.log")
        stderr = read_log("stderr.log")
        ids = [line.split("\t", 1)[0] for line in stdout.splitlines() if "\t" in line]
        if process.returncode != expected_exit:
            failures.append(f"exit {process.returncode}, expected {expected_exit}")
        if expected_ids is not None and (set(ids) != expected_ids or len(ids) != len(expected_ids)):
            failures.append(f"definition identities {ids}, expected {sorted(expected_ids)} exactly once")
        if required_error is not None and required_error.casefold() not in stderr.casefold():
            failures.append(f"stderr did not contain {required_error!r}")
        if expected_exit == 0 and stderr.strip():
            failures.append("successful command wrote an error diagnostic")
        if expected_exit != 0 and ids:
            failures.append("failed bootstrap published a partial definition listing")
        results.append({"name": name, "command": command, "exitCode": process.returncode,
                        "elapsedSeconds": round(time.monotonic() - started, 3), "definitionIds": ids,
                        "failures": failures, "passed": not failures})

    # The kernel's core.* built-ins are seeded with the loader because every server profile composes them.
    built_ins = {"core.protocol-registry", "core.packet-router", "core.session-registry", "core.client-profiles",
                 "core.opcode-tables", "core.message-router", "core.wire-dialects"}
    expected = {"loader.native", "test.multi.alpha", "test.multi.beta"} | built_ins
    run("normal", ["--plugin-root", str(normal)], 0, expected)
    run("renamed-configured-row", ["--plugin-root", str(normal), "--bootstrap-dir", str(profile("custom-profile"))], 0, expected)
    run("duplicate-within-module", ["--plugin-root", str(repeated)], 1, required_error="test.multi.duplicate")
    run("duplicate-across-modules", ["--plugin-root", str(cross)], 1, required_error="test.multi.alpha")
    run("malformed-module", ["--plugin-root", str(malformed.parent)], 1, required_error="error:")
    run("unknown-bootstrap-input", ["--plugin-root", str(normal), "--bootstrap-dir", str(profile("unknown-input", input_name="missing"))], 1, required_error="unknown application input")
    run("capacity-exhausted", ["--plugin-root", str(normal), "--bootstrap-dir", str(profile("small-capacity", reserve=1))], 1, required_error="capacity")
    run("missing-configured-root", ["--plugin-root", str(output / "does-not-exist")], 1, required_error="does not exist")
    run("empty-configured-root", ["--plugin-root", str(empty)], 1, required_error="empty")
    run("all-seeds-excluded", ["--plugin-root", str(excluded)], 1, required_error="empty")
    run("ignored-directories-exhaust-scan-budget", ["--plugin-root", str(ignored), "--bootstrap-dir",
        str(profile("small-scan-budget", maximum_scanned_directories=1))], 1, required_error="scanned directory capacity")
    run("unknown-option", ["--unknown"], 1, required_error="Unknown plugin-list option")
    run("missing-option-value", ["--bootstrap-dir"], 1, required_error="error:")
    run("default-root-seed-exclusion", [], 0)
    final = results[-1]
    if final["definitionIds"].count("loader.native") != 1:
        final["failures"].append("default roots must expose the bootstrap seed exactly once")
        final["passed"] = False
    hashes = {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in (host, seed_module, module, duplicate, Path(__file__))}
    report = {"cases": results, "passed": sum(item["passed"] for item in results), "total": len(results), "sha256": hashes}
    (output / "verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"passed": report["passed"], "total": report["total"], "report": str(output / "verification.json")}))
    return 0 if report["passed"] == report["total"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
