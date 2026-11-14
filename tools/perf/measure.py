#!/usr/bin/env python3
"""Start one isolated Host, wait until it reports ready, and record what the process costs.

    python tools/perf/measure.py --profile kr-812
    python tools/perf/measure.py --profile all --budget tools/perf/budgets.json --json artifacts/perf/all.json

The Host always runs with tools/local-checks/isolated-memory.patch.yaml as its last patch, so players
live in memory and no database is touched. Only the process this script started is stopped.

A start after the first reads the catalogs the first one compiled from the client data, so by default
the script has the Host compile them into a scratch directory (bff data compile, not measured) and then
measures a start that reads them. --compiled-data cold measures the first start instead.

Three samples are taken: when the Host logs that it started, after --settle seconds, and after a
further --idle seconds in which nothing connects. Memory is read from the operating system, not from
the runtime, because that is what a small server runs out of:

    working set  resident memory (Windows working set, Linux VmRSS)
    private      memory only this process holds (Windows private bytes, Linux RssAnon)
    peak         the highest working set since the process started (Windows peak working set, Linux VmHWM)

Standard library only. Windows and Linux.
"""

from __future__ import annotations

import argparse
import collections
import json
import os
import re
import secrets
import signal
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Callable, Mapping, NamedTuple, Optional, Sequence

REPO = Path(__file__).resolve().parents[2]
ISOLATED_PATCH = "tools/local-checks/isolated-memory.patch.yaml"
READY_PATTERN = re.compile(r"Server started with (\d+) active row")
# The Host's own account of its start, logged just before it reports ready.
STARTUP_PATTERN = re.compile(r"Startup allocated (\d+) MB in (\d+) collection\(s\); "
                             r"the managed heap was compacted from (\d+) MB to (\d+) MB committed in (\d+) ms")
# Each clientdata row's account of its compiled client data file, logged when the start has converged.
COMPILED_PATTERN = re.compile(r"compiled client data '.*': (\d+) catalog\(s\) read, (\d+) built from the client data")
MB = 1024 * 1024


class Sample(NamedTuple):
    """One reading of the Host process."""

    working_set_mb: float
    private_mb: float
    peak_mb: float
    cpu_seconds: float
    threads: Optional[int]


def parse_proc_status(text: str) -> dict[str, int]:
    """Return the integer fields of /proc/<pid>/status (memory fields are in kB)."""
    return {name: int(value) for name, value in re.findall(r"^(\w+):\s+(\d+)", text, re.M)}


def parse_proc_stat_cpu(text: str, ticks_per_second: int) -> float:
    """Return user plus system CPU seconds from /proc/<pid>/stat.

    The command name is in parentheses and may contain spaces, so fields are counted after the
    last closing parenthesis: utime and stime are the 12th and 13th fields from there.
    """
    fields = text.rsplit(")", 1)[-1].split()
    return (int(fields[11]) + int(fields[12])) / ticks_per_second


def sample_from_status(status: Mapping[str, int], cpu_seconds: float) -> Sample:
    """Build a sample from parsed /proc status fields."""
    return Sample(
        working_set_mb=status["VmRSS"] / 1024,
        private_mb=status["RssAnon"] / 1024,
        peak_mb=status["VmHWM"] / 1024,
        cpu_seconds=cpu_seconds,
        threads=status.get("Threads"),
    )


def _linux_sampler(pid: int) -> Callable[[], Sample]:
    ticks = os.sysconf("SC_CLK_TCK")

    def read() -> Sample:
        status = parse_proc_status(Path(f"/proc/{pid}/status").read_text())
        cpu = parse_proc_stat_cpu(Path(f"/proc/{pid}/stat").read_text(), ticks)
        return sample_from_status(status, cpu)

    return read


def _windows_sampler(process: subprocess.Popen) -> Callable[[], Sample]:
    import ctypes
    from ctypes import wintypes

    class Counters(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD),
            ("PageFaultCount", wintypes.DWORD),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
            ("PrivateUsage", ctypes.c_size_t),
        ]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.K32GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
    kernel32.K32GetProcessMemoryInfo.restype = wintypes.BOOL
    kernel32.GetProcessTimes.argtypes = [wintypes.HANDLE] + [ctypes.POINTER(wintypes.FILETIME)] * 4
    kernel32.GetProcessTimes.restype = wintypes.BOOL
    handle = wintypes.HANDLE(int(process._handle))  # The handle Popen keeps open for the child.

    def filetime_seconds(value: wintypes.FILETIME) -> float:
        return ((value.dwHighDateTime << 32) | value.dwLowDateTime) / 10_000_000

    def read() -> Sample:
        counters = Counters()
        counters.cb = ctypes.sizeof(Counters)
        if not kernel32.K32GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
            raise ctypes.WinError(ctypes.get_last_error())
        created, exited, kernel, user = (wintypes.FILETIME() for _ in range(4))
        if not kernel32.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited), ctypes.byref(kernel), ctypes.byref(user)):
            raise ctypes.WinError(ctypes.get_last_error())
        return Sample(
            working_set_mb=counters.WorkingSetSize / MB,
            private_mb=counters.PrivateUsage / MB,
            peak_mb=counters.PeakWorkingSetSize / MB,
            cpu_seconds=filetime_seconds(kernel) + filetime_seconds(user),
            threads=None,
        )

    return read


def sampler_for(process: subprocess.Popen) -> Callable[[], Sample]:
    """Return a function that reads the given process on this operating system."""
    if sys.platform == "win32":
        return _windows_sampler(process)
    if sys.platform.startswith("linux"):
        return _linux_sampler(process.pid)
    raise SystemExit(f"error: process measurement is not implemented for {sys.platform}; use Windows or Linux.")


def platform_key() -> str:
    """Name the budget table of this operating system."""
    if sys.platform == "win32":
        return "windows"
    if sys.platform.startswith("linux"):
        return "linux"
    return sys.platform


def host_environment(base: Mapping[str, str], client_data: Optional[Path], overrides: Sequence[str]) -> dict[str, str]:
    """Return the environment of the measured Host.

    Values already set by the caller win. The defaults only fill what an isolated start needs:
    open sign-in removes the need for a launcher row. The isolated patch disables the database
    row, so its variables need not be set.
    """
    env = dict(base)
    env.setdefault("BFF_LOGIN_AUTHENTICATION", "open")
    env.setdefault("BFF_ADMIN_TOKEN", secrets.token_hex(24))
    if client_data is not None:
        env["BFF_CLIENT_DATA"] = str(client_data)
    elif "BFF_CLIENT_DATA" not in env and (REPO / "client-data").is_dir():
        env["BFF_CLIENT_DATA"] = str(REPO / "client-data")
    for item in overrides:
        name, separator, value = item.partition("=")
        if not separator or not name:
            raise SystemExit(f"error: --env expects NAME=VALUE, got '{item}'.")
        env[name] = value
    return env


def host_command(host_dll: Path, profile: str, patches: Sequence[str], compile_only: bool = False) -> list[str]:
    """Return the Host command line. The isolated patch is last so that no other patch can undo it.

    compile_only gives the command that starts the profile the same way, writes the compiled client
    data files and stops.
    """
    command = ["dotnet", str(host_dll)] + (["data", "compile", profile] if compile_only else ["run", "--profile", profile])
    for patch in patches:
        command += ["--patch", patch]
    return command + ["--patch", ISOLATED_PATCH]


def parse_compiled_line(line: str) -> Optional[tuple[int, int]]:
    """Return (catalogs read, catalogs built) of one clientdata row's summary line, or None for any other line."""
    match = COMPILED_PATTERN.search(line)
    return (int(match.group(1)), int(match.group(2))) if match else None


def budget_name(profile: str, compiled_data: str) -> str:
    """Name the budget table of a measurement: a cold start has its own, named '<profile> cold'."""
    return f"{profile} cold" if compiled_data == "cold" else profile


def find_host_dll() -> Path:
    """Return the newest Release build of the Host."""
    found = sorted((REPO / "src/BubbleFighter.Fantasy.Host/bin/Release").glob("net*/BubbleFighter.Fantasy.Host.dll"),
                   key=lambda path: path.stat().st_mtime)
    if not found:
        raise SystemExit("error: no Release build of the Host; run 'dotnet build BubbleFighter.Fantasy.sln -c Release' first.")
    return found[-1]


def parse_startup_line(line: str) -> Optional[dict[str, float]]:
    """Return the numbers of the Host's startup line, or None for any other line."""
    match = STARTUP_PATTERN.search(line)
    if not match:
        return None
    allocated, collections, _before, committed, pause = (int(value) for value in match.groups())
    return {"startup_allocated_mb": allocated, "startup_collections": collections,
            "heap_committed_mb": committed, "compaction_pause_ms": pause}


def summarize(ready_wall_s: float, ready: Sample, settled: Sample, idle: Sample, idle_seconds: float,
              startup: Optional[Mapping[str, float]] = None,
              compiled: Optional[tuple[int, int]] = None) -> dict[str, Optional[float]]:
    """Reduce the three samples and the Host's startup line to the numbers a budget can name."""
    reported = startup or {}
    return {
        "compiled_catalogs_read": compiled[0] if compiled else None,
        "compiled_catalogs_built": compiled[1] if compiled else None,
        "startup_allocated_mb": reported.get("startup_allocated_mb"),
        "startup_collections": reported.get("startup_collections"),
        "heap_committed_mb": reported.get("heap_committed_mb"),
        "compaction_pause_ms": reported.get("compaction_pause_ms"),
        "start_wall_s": round(ready_wall_s, 1),
        "start_cpu_s": round(ready.cpu_seconds, 2),
        "peak_mb": round(idle.peak_mb, 1),
        "working_set_idle_mb": round(idle.working_set_mb, 1),
        "private_idle_mb": round(idle.private_mb, 1),
        "idle_cpu_percent": round(100 * (idle.cpu_seconds - settled.cpu_seconds) / idle_seconds, 3) if idle_seconds > 0 else None,
        "threads_idle": idle.threads,
    }


def load_budget(path: Path, platform: str, profile: str) -> dict[str, float]:
    """Return the limits of one profile on one operating system; a missing table is an error, not a pass."""
    document = json.loads(path.read_text(encoding="utf-8"))
    try:
        budget = document[platform][profile]
    except KeyError:
        raise SystemExit(f"error: {path} has no budget for profile '{profile}' on {platform}.") from None
    if not budget:
        raise SystemExit(f"error: the budget for profile '{profile}' on {platform} in {path} is empty.")
    return budget


def evaluate_budget(metrics: Mapping[str, Optional[float]], budget: Mapping[str, float]) -> list[tuple[str, Optional[float], float, bool]]:
    """Compare every budgeted metric with its limit. A metric that was not measured fails."""
    rows = []
    for name, limit in budget.items():
        value = metrics.get(name)
        rows.append((name, value, limit, value is not None and value <= limit))
    return rows


class HostOutput:
    """Reads the Host's output on a thread, keeps its tail, and notes the moment it reports ready."""

    def __init__(self, process: subprocess.Popen, log: Optional[Path]):
        self.ready = threading.Event()
        self.ready_at: Optional[float] = None
        self.startup: Optional[dict[str, float]] = None
        # Catalogs read from and built for the compiled client data, summed over the clientdata rows; None when no row says.
        self.compiled: Optional[tuple[int, int]] = None
        self.tail: collections.deque[str] = collections.deque(maxlen=60)
        self._log = log.open("w", encoding="utf-8") if log else None
        self._thread = threading.Thread(target=self._read, args=(process,), daemon=True)
        self._thread.start()

    def _read(self, process: subprocess.Popen) -> None:
        assert process.stdout is not None
        for line in process.stdout:
            now = time.monotonic()
            self.tail.append(line.rstrip())
            if self._log:
                self._log.write(line)
                self._log.flush()
            if self.startup is None:
                self.startup = parse_startup_line(line)
            counts = parse_compiled_line(line)
            if counts:
                before = self.compiled or (0, 0)
                self.compiled = (before[0] + counts[0], before[1] + counts[1])
            if not self.ready.is_set() and READY_PATTERN.search(line):
                self.ready_at = now
                self.ready.set()

    def close(self) -> None:
        self._thread.join(timeout=5)
        if self._log:
            self._log.close()


def stop(process: subprocess.Popen) -> None:
    """Stop the Host this script started: an interrupt where the platform has one, then a kill."""
    if process.poll() is not None:
        return
    if sys.platform != "win32":
        process.send_signal(signal.SIGINT)
        try:
            process.wait(timeout=30)
            return
        except subprocess.TimeoutExpired:
            pass
    process.kill()
    process.wait(timeout=30)


def compile_client_data(host_dll: Path, args: argparse.Namespace, env: Mapping[str, str]) -> None:
    """Start the profile once so that its compiled client data files exist, and wait for it to stop by itself."""
    command = host_command(host_dll, args.profile, args.patch, compile_only=True)
    try:
        done = subprocess.run(command, cwd=REPO, env=dict(env), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              text=True, encoding="utf-8", errors="replace", timeout=args.start_timeout)
    except subprocess.TimeoutExpired:
        raise SystemExit(f"error: compiling the client data did not finish in {args.start_timeout} s.") from None
    if done.returncode != 0:
        raise SystemExit("error: compiling the client data failed:\n  " + "\n  ".join(done.stdout.splitlines()[-60:]))


def measure(args: argparse.Namespace) -> dict:
    host_dll = Path(args.host_dll).resolve() if args.host_dll else find_host_dll()
    env = host_environment(os.environ, Path(args.client_data).resolve() if args.client_data else None, args.env)
    if args.compiled_data == "as-is":
        return measure_start(host_dll, args, env)
    # The compiled files of a measurement are its own: a warm start reads what this script just had the same Host
    # write, a cold start finds nothing. Neither reads or leaves files in the checkout's state directory.
    if "BFF_COMPILED_DATA" in env:
        raise SystemExit("error: BFF_COMPILED_DATA is set; use --compiled-data as-is to measure with that directory.")
    with tempfile.TemporaryDirectory(prefix="bff-perf-compiled-") as scratch:
        env["BFF_COMPILED_DATA"] = scratch
        if args.compiled_data == "warm":
            compile_client_data(host_dll, args, env)
        result = measure_start(host_dll, args, env)
    built = result["metrics"]["compiled_catalogs_built"]
    if args.compiled_data == "warm" and built:
        raise SystemExit(f"error: the measured start built {built} catalog(s) from the client data although the "
                         "same Host had just compiled them; the compiled client data does not stay valid.")
    return result


def measure_start(host_dll: Path, args: argparse.Namespace, env: Mapping[str, str]) -> dict:
    command = host_command(host_dll, args.profile, args.patch)
    log = Path(args.log).resolve() if args.log else None
    if log:
        log.parent.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    process = subprocess.Popen(command, cwd=REPO, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, encoding="utf-8", errors="replace")
    output = HostOutput(process, log)
    try:
        read = sampler_for(process)
        deadline = started + args.start_timeout
        while not output.ready.wait(timeout=0.2):
            if process.poll() is not None:
                raise SystemExit("error: the Host exited before it was ready:\n  " + "\n  ".join(output.tail))
            if time.monotonic() > deadline:
                raise SystemExit(f"error: the Host was not ready after {args.start_timeout} s:\n  " + "\n  ".join(output.tail))
        ready = read()
        assert output.ready_at is not None
        ready_wall = output.ready_at - started
        time.sleep(args.settle)
        settled = read()
        time.sleep(args.idle)
        idle = read()
        if process.poll() is not None:
            raise SystemExit("error: the Host exited during the measurement:\n  " + "\n  ".join(output.tail))
    finally:
        stop(process)
        output.close()
    return {
        "profile": args.profile,
        "label": args.label or args.profile,
        "platform": platform_key(),
        "compiled_data": args.compiled_data,
        "host": str(host_dll),
        "settle_seconds": args.settle,
        "idle_seconds": args.idle,
        "samples": {"ready": ready._asdict(), "settled": settled._asdict(), "idle": idle._asdict()},
        "metrics": summarize(ready_wall, ready, settled, idle, args.idle, output.startup, output.compiled),
    }


def print_report(result: dict) -> None:
    print(f"{result['label']} ({result['platform']}, compiled client data {result['compiled_data']})")
    print(f"  {'at':<10}{'working set':>13}{'private':>10}{'peak':>10}{'cpu s':>9}{'threads':>9}")
    for name, sample in result["samples"].items():
        threads = "-" if sample["threads"] is None else str(sample["threads"])
        print(f"  {name:<10}{sample['working_set_mb']:>10.1f} MB{sample['private_mb']:>7.1f} MB{sample['peak_mb']:>7.1f} MB"
              f"{sample['cpu_seconds']:>9.2f}{threads:>9}")
    for name, value in result["metrics"].items():
        print(f"  {name} = {'not measured' if value is None else value}")


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Measure the memory and CPU of one isolated Host start.")
    parser.add_argument("--profile", required=True, help="Profile to run, for example kr-812 or all.")
    parser.add_argument("--label", help="Name printed with the result; defaults to the profile.")
    parser.add_argument("--host-dll", help="Host assembly to run; defaults to the newest Release build in this checkout.")
    parser.add_argument("--client-data", help="Client data directory; defaults to BFF_CLIENT_DATA, then ./client-data.")
    parser.add_argument("--patch", action="append", default=[], metavar="FILE",
                        help="Extra patch applied before the isolated-memory patch; repeatable.")
    parser.add_argument("--env", action="append", default=[], metavar="NAME=VALUE",
                        help="Environment variable of the Host, for example DOTNET_GCConserveMemory=9; repeatable.")
    parser.add_argument("--settle", type=float, default=15, help="Seconds between ready and the second sample.")
    parser.add_argument("--idle", type=float, default=30, help="Seconds of idle time before the last sample.")
    parser.add_argument("--start-timeout", type=float, default=900, help="Seconds to wait for the Host to report ready.")
    parser.add_argument("--log", help="File that receives the Host's output.")
    parser.add_argument("--json", help="File that receives the result as JSON.")
    parser.add_argument("--budget", help="Budget file; the exit code is 2 when a budgeted number is exceeded.")
    parser.add_argument("--compiled-data", choices=["warm", "cold", "as-is"], default="warm",
                        help="warm (default): compile the client data in a scratch directory first, then measure a start that "
                             "reads it, as every start after the first is. cold: measure the first start, which parses the "
                             "client data and writes the files; its budget is '<profile> cold'. as-is: leave "
                             "BFF_COMPILED_DATA alone.")
    args = parser.parse_args(argv)

    budget = load_budget(Path(args.budget), platform_key(), budget_name(args.profile, args.compiled_data)) if args.budget else None
    result = measure(args)
    print_report(result)
    exit_code = 0
    if budget is not None:
        rows = evaluate_budget(result["metrics"], budget)
        result["budget"] = [{"metric": name, "value": value, "limit": limit, "ok": ok} for name, value, limit, ok in rows]
        for name, value, limit, ok in rows:
            print(f"  budget {name}: {'not measured' if value is None else value} <= {limit}: {'ok' if ok else 'EXCEEDED'}")
        if not all(ok for _, _, _, ok in rows):
            exit_code = 2
    if args.json:
        target = Path(args.json).resolve()
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
