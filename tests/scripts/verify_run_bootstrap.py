"""Verify real host run bootstrap and shutdown using isolated local fixture processes."""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
import shutil
import signal
import subprocess
import sys
import threading
import time
from pathlib import Path


def signal_owned_console(pid: int) -> int:
    """Attach this otherwise console-free helper only to the recorded child's private console."""
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.AttachConsole.argtypes = [ctypes.c_uint]
    kernel.AttachConsole.restype = ctypes.c_int
    kernel.FreeConsole.restype = ctypes.c_int
    callback_type = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_uint)
    ignore = callback_type(lambda _: 1)
    kernel.SetConsoleCtrlHandler.argtypes = [callback_type, ctypes.c_int]
    kernel.SetConsoleCtrlHandler.restype = ctypes.c_int
    kernel.GenerateConsoleCtrlEvent.argtypes = [ctypes.c_uint, ctypes.c_uint]
    kernel.GenerateConsoleCtrlEvent.restype = ctypes.c_int
    kernel.GetConsoleProcessList.argtypes = [ctypes.POINTER(ctypes.c_uint), ctypes.c_uint]
    kernel.GetConsoleProcessList.restype = ctypes.c_uint
    attached = (ctypes.c_uint * 64)()
    attached_count = kernel.GetConsoleProcessList(attached, len(attached))
    if attached_count:
        if attached_count != 1 or attached[0] != os.getpid():
            raise RuntimeError("Signal helper unexpectedly inherited a shared console; refusing to detach or signal it.")
        if not kernel.FreeConsole():
            raise ctypes.WinError(ctypes.get_last_error())
    if not kernel.AttachConsole(pid):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        target_count = kernel.GetConsoleProcessList(attached, len(attached))
        if target_count < 1 or target_count > len(attached):
            raise RuntimeError("Unable to verify the private target console process list.")
        target_processes = set(attached[:target_count])
        if pid not in target_processes or not target_processes.issubset({pid, os.getpid()}):
            raise RuntimeError("Target console is not exclusive to the owned Host and signal helper.")
        if not kernel.SetConsoleCtrlHandler(ignore, 1):
            raise ctypes.WinError(ctypes.get_last_error())
        if not kernel.GenerateConsoleCtrlEvent(1, 0):
            raise ctypes.WinError(ctypes.get_last_error())
        time.sleep(0.2)
    finally:
        kernel.FreeConsole()
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--timeout-seconds", type=int, default=30)
    parser.add_argument("--output-limit-bytes", type=int, default=1048576)
    parser.add_argument("--managed-only", action="store_true", help="Run only pipe-managed cases, suitable for an isolated container.")
    parser.add_argument("--host-prefix-json", type=Path, help="JSON argument array replacing the default dotnet executable.")
    args = parser.parse_args()
    if not 1 <= args.timeout_seconds <= 30 or args.output_limit_bytes < 1024:
        parser.error("Timeout must be 1..30 seconds and output limit at least 1024 bytes.")
    repository = args.repository.resolve()
    prefix = json.loads(args.host_prefix_json.read_text(encoding="utf-8")) if args.host_prefix_json else ["dotnet"]
    if not isinstance(prefix, list) or not prefix or any(not isinstance(value, str) for value in prefix):
        parser.error("Host prefix must be a nonempty JSON array of strings.")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    host = repository / "src/BubbleFighter.Fantasy.Host/bin" / args.configuration / "net10.0/BubbleFighter.Fantasy.Host.dll"
    fixture = repository / "tests/fixtures/RunBootstrapFixture/bin" / args.configuration / "net10.0/RunBootstrapFixture.dll"
    for path in (host, fixture):
        if not path.is_file():
            raise FileNotFoundError(f"Build the host and dedicated fixture first: {path}")
    module_directory = output / "plugin-root/RunBootstrapFixture"
    module_directory.mkdir(parents=True)
    shutil.copyfile(fixture, module_directory / "RunBootstrapFixture.dll")
    results: list[dict[str, object]] = []

    def run(name: str, kinds: list[str], expected_exit: int, required_markers: list[str],
            signal_after: str | None = None, required_error: str | None = None,
            extra: list[str] | None = None, disabled: bool = False,
            managed_action: str | None = None) -> None:
        case = output / name
        profile = case / "profiles/scenario"
        profile.mkdir(parents=True)
        (case / "profiles/bundles").mkdir()
        markers = case / "markers"
        markers.mkdir()
        (profile / "profile.yaml").write_text("id: scenario\nbundles: [game]\n", encoding="utf-8")
        rows: list[str] = []
        for index, kind in enumerate(kinds):
            parent = "  parent_id: game-0\n" if index and kinds[0] == "success" else ""
            disabled_line = "  disabled: true\n" if disabled and kind == "failure" else ""
            rows.append(f"""- id: game-{index}
  plugin: test.run.{kind}
{parent}{disabled_line}  lifecycle:
    effect_capacity: 16
    entry_reserve: 2
    service_metadata_reserve: 8
    definition_source_reserve: 1
    definition_reserve: 4
  config:
    evidence_directory: {json.dumps(str(markers))}
    maximum_marker_bytes: 1024
""")
        (case / "profiles/bundles/game.yaml").write_text("".join(rows), encoding="utf-8")
        command = [*prefix, str(host), "run", "--profile", "scenario", "--profiles-dir",
                   str(case / "profiles"), "--plugin-root", str(module_directory.parent), "--log-level", "Information", *(extra or [])]
        options: dict[str, object] = {}
        managed = extra is not None and "--managed-stdio" in extra
        if os.name == "nt" and not managed:
            startup = subprocess.STARTUPINFO()
            startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startup.wShowWindow = subprocess.SW_HIDE
            options = {"creationflags": subprocess.CREATE_NEW_CONSOLE, "startupinfo": startup}
        elif os.name == "nt":
            options = {"creationflags": subprocess.CREATE_NO_WINDOW}
        process = subprocess.Popen(command, cwd=repository, stdin=subprocess.PIPE if managed else subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, **options)
        failures: list[str] = []
        started = time.monotonic()
        ready = threading.Event()
        signaled = False
        killed = False
        helper_exit: int | None = None

        def stop_owned() -> None:
            nonlocal killed
            if process.poll() is None:
                killed = True
                try:
                    process.kill()
                except OSError as error:
                    if process.poll() is None:
                        failures.append(f"could not kill owned process: {type(error).__name__}")

        def drain(stream, filename: str) -> None:
            remaining = args.output_limit_bytes
            suffix = b""
            try:
                with (case / filename).open("wb") as sink:
                    while data := stream.read1(4096):
                        sink.write(data[:remaining])
                        remaining -= len(data)
                        combined = suffix + data
                        if ((managed and b"FT_HOST_READY 1\n" in combined.replace(b"\r\n", b"\n"))
                                or (not managed and b"Server started with " in combined)):
                            ready.set()
                        suffix = combined[-64:]
                        if remaining < 0:
                            failures.append(f"{filename} exceeded its byte cap")
                            stop_owned()
                            break
            except Exception as error:
                failures.append(f"{filename} reader failed: {type(error).__name__}")
                stop_owned()
            finally:
                try:
                    stream.close()
                except Exception as error:
                    failures.append(f"{filename} close failed: {type(error).__name__}")

        readers = [threading.Thread(target=drain, args=(stream, filename), daemon=True)
                   for stream, filename in ((process.stdout, "stdout.log"), (process.stderr, "stderr.log"))]
        for reader in readers:
            reader.start()
        deadline = started + args.timeout_seconds
        while process.poll() is None and time.monotonic() < deadline:
            target_ready = ready.is_set() if signal_after == "started" else signal_after is not None and (markers / signal_after).is_file()
            if target_ready and not signaled:
                if process.poll() is not None:
                    break
                signaled = True
                if managed_action is not None:
                    if managed_action == "eof":
                        process.stdin.close()
                    else:
                        body = {"stop": b"stop\n", "crlf": b"stop\r\n", "unknown": b"go\n",
                                "overflow": b"s" * 65}[managed_action]
                        process.stdin.write(body)
                        process.stdin.flush()
                elif os.name == "nt":
                    helper_command = [sys.executable, str(Path(__file__).resolve()), "--signal-owned-console", str(process.pid)]
                    with (case / "signal-helper.log").open("wb") as helper_log:
                        helper = subprocess.Popen(helper_command, stdin=subprocess.DEVNULL, stdout=helper_log, stderr=helper_log,
                                                  creationflags=subprocess.DETACHED_PROCESS)
                        try:
                            helper_exit = helper.wait(timeout=5)
                        except subprocess.TimeoutExpired:
                            helper.kill()
                            helper.wait(timeout=5)
                            failures.append("owned signal helper timed out")
                            stop_owned()
                    if helper_exit != 0:
                        failures.append(f"owned signal helper failed: {helper_exit}")
                        stop_owned()
                else:
                    process.send_signal(signal.SIGTERM)
            time.sleep(0.02)
        if process.poll() is None:
            failures.append("owned host timed out")
            stop_owned()
        process.wait(timeout=5)
        if process.stdin is not None and not process.stdin.closed:
            process.stdin.close()
        for reader in readers:
            reader.join(timeout=5)
            if reader.is_alive():
                failures.append("output reader failed to finish")
        stdout = (case / "stdout.log").read_text(encoding="utf-8", errors="replace")
        stderr = (case / "stderr.log").read_text(encoding="utf-8", errors="replace")
        actual_markers = sorted(path.name for path in markers.iterdir())
        if process.returncode != expected_exit:
            failures.append(f"exit {process.returncode}, expected {expected_exit}")
        if killed:
            failures.append("host was forcibly killed; graceful completion is unproven")
        if signal_after is not None and not signaled:
            failures.append("expected signal point was never reached")
        for marker in required_markers:
            path = markers / marker
            if not path.is_file() or path.stat().st_size > 1024 or not path.read_text(encoding="utf-8").startswith("game-"):
                failures.append(f"missing, oversized, or invalid owned marker {marker}")
        if required_error is not None and required_error.casefold() not in stderr.casefold():
            failures.append(f"missing stderr diagnostic {required_error!r}")
        if (expected_exit != 0 and not (managed and signal_after == "started")) or signal_after == "cancellable.entered":
            if "Server started with " in stdout:
                failures.append("server reported startup despite failure or interrupted activation")
        protocol_markers = stdout.splitlines().count("FT_HOST_READY 1")
        expected_ready = managed and signal_after == "started"
        if protocol_markers != int(expected_ready):
            failures.append(f"managed readiness count {protocol_markers}, expected {int(expected_ready)}")
        if (signal_after is not None or required_markers) and "Shutdown complete." not in stdout:
            failures.append("graceful shutdown completion was not reported")
        if expected_exit == 0 and stderr.strip():
            failures.append("successful command emitted an error")
        if disabled and any(marker.startswith("failure.") for marker in actual_markers):
            failures.append("disabled game entry acquired ownership")
        if extra and "--dump-config" in extra and expected_exit == 0:
            if actual_markers or "plugin: test.run.success" not in stdout:
                failures.append("dump did not preserve no-load composition")
        results.append({"name": name, "command": command, "pid": process.pid, "exitCode": process.returncode,
                        "signaled": signaled, "signalHelperExit": helper_exit, "forcedKill": killed,
                        "markers": actual_markers, "elapsedSeconds": round(time.monotonic() - started, 3),
                        "failures": failures, "passed": not failures})

    if not args.managed_only:
        run("startup-and-owned-console-shutdown", ["success"], 0, ["success.active", "success.released"], signal_after="started")
        run("failure-after-active-parent", ["success", "failure"], 1,
            ["success.active", "success.released", "failure.entered", "failure.released"], required_error="RUN_FIXTURE_ACTIVATION_FAILURE")
        run("pending-service-is-not-startup", ["success", "pending"], 1, ["success.active", "success.released"], required_error="did not converge")
        run("cancel-in-progress-activation", ["cancellable"], 0, ["cancellable.entered", "cancellable.released"], signal_after="cancellable.entered")
        run("disabled-entry-remains-inactive", ["success", "failure"], 0, ["success.active", "success.released"], signal_after="started", disabled=True)
        run("dump-does-not-load-bootstrap", ["success"], 0, [], extra=["--bootstrap-dir", str(output / "missing-bootstrap"), "--dump-config"])
        run("missing-bootstrap-fails-before-game", ["success"], 1, [], required_error="not found", extra=["--bootstrap-dir", str(output / "missing-bootstrap")])
    for action in ("stop", "crlf", "eof"):
        run(f"managed-ready-{action}", ["success"], 0, ["success.active", "success.released"],
            signal_after="started", extra=["--managed-stdio"], managed_action=action)
    for action, diagnostic in (("unknown", "Unknown managed-stdio command"), ("overflow", "exceeds 64 bytes")):
        run(f"managed-startup-{action}", ["cancellable"], 1, ["cancellable.entered", "cancellable.released"],
            signal_after="cancellable.entered", extra=["--managed-stdio"], managed_action=action, required_error=diagnostic)
        run(f"managed-ready-{action}", ["success"], 1, ["success.active", "success.released"],
            signal_after="started", extra=["--managed-stdio"], managed_action=action, required_error=diagnostic)
    for action in ("stop", "eof"):
        run(f"managed-cancel-activation-{action}", ["cancellable"], 0, ["cancellable.entered", "cancellable.released"],
            signal_after="cancellable.entered", extra=["--managed-stdio"], managed_action=action)
    run("managed-failure-joins-idle-input", ["success", "failure"], 1,
        ["success.active", "success.released", "failure.entered", "failure.released"],
        required_error="RUN_FIXTURE_ACTIVATION_FAILURE", extra=["--managed-stdio"])
    run("managed-pending-never-ready", ["success", "pending"], 1, ["success.active", "success.released"],
        required_error="did not converge", extra=["--managed-stdio"])
    run("managed-dump-rejected", ["success"], 1, [], required_error="cannot be combined", extra=["--managed-stdio", "--dump-config"])
    report = {"cases": results, "passed": sum(case["passed"] for case in results), "total": len(results),
              "sha256": {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in (host, fixture, Path(__file__))}}
    (output / "verification.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"passed": report["passed"], "total": report["total"], "report": str(output / "verification.json")}))
    return 0 if report["passed"] == report["total"] else 1


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--signal-owned-console":
        raise SystemExit(signal_owned_console(int(sys.argv[2])))
    raise SystemExit(main())
