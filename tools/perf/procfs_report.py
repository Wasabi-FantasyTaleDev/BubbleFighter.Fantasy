#!/usr/bin/env python3
"""Describe a running server process on Linux from /proc, without changing anything.

    python3 tools/perf/procfs_report.py --pid 12345
    python3 tools/perf/procfs_report.py --pid "$(docker inspect -f '{{.State.Pid}}' <container>)" --window 20

It reads files under /proc and /sys/fs/cgroup only: no signal is sent, nothing is attached to the
process and nothing is written. Run it as a user that may read the process (root for a container).

The report answers three questions a memory or CPU number alone does not:

    what backs the resident memory   anonymous pages (managed heap, native allocations), the runtime's
                                     code mappings, mapped assemblies, native libraries
    which threads use the CPU        grouped by thread name over a sampling window
    what the container is charged    the cgroup v2 counters, including the peak since start

Standard library only.
"""

from __future__ import annotations

import argparse
import collections
import os
import re
import sys
import time
from pathlib import Path
from typing import Iterable, Mapping, Optional, Sequence

# Instruction sets of the x86-64 microarchitecture levels, as /proc/cpuinfo names them.
X86_64_V2 = ("cx16", "popcnt", "pni", "ssse3", "sse4_1", "sse4_2")
X86_64_V3 = ("avx", "avx2", "bmi1", "bmi2", "f16c", "fma", "abm", "movbe")

_MAPPING = re.compile(r"^[0-9a-f]+-[0-9a-f]+ \S+ \S+ \S+ \S+\s*(.*)$")
_RSS = re.compile(r"^Rss:\s+(\d+) kB")


def read(path: str) -> str:
    """Return a file's text, or an empty string when it cannot be read."""
    try:
        return Path(path).read_text()
    except OSError:
        return ""


def backing_of(path: str) -> str:
    """Name what backs one mapping of /proc/<pid>/smaps."""
    if not path:
        return "anonymous (managed heap, native allocations)"
    if path.startswith("["):
        return path
    if "doublemapper" in path or path.startswith("/memfd"):
        return "runtime code and type data (memfd)"
    if path.endswith(".dll"):
        return "mapped assemblies (.dll)"
    if ".so" in path:
        return "native libraries (.so)"
    if path.endswith((".pkn", ".bfz")):
        return "client archives"
    return "other files"


def group_smaps(text: str) -> dict[str, int]:
    """Sum resident kB of /proc/<pid>/smaps by what backs each mapping."""
    groups: dict[str, int] = {}
    current: Optional[str] = None
    for line in text.splitlines():
        mapping = _MAPPING.match(line)
        if mapping:
            current = backing_of(mapping.group(1).strip())
            continue
        rss = _RSS.match(line)
        if rss and current is not None:
            groups[current] = groups.get(current, 0) + int(rss.group(1))
    return groups


def thread_group(name: str) -> str:
    """Group numbered threads of one pool under one name (".NET TP Worker-12" and "-3" are one group)."""
    return re.sub(r"[-\s]*\d+$", "", name) or name


def parse_thread_stat(text: str, ticks_per_second: int) -> tuple[str, float]:
    """Return a thread's name and CPU seconds from /proc/<pid>/task/<tid>/stat."""
    name = text[text.index("(") + 1:text.rindex(")")]
    fields = text.rsplit(")", 1)[-1].split()
    return name, (int(fields[11]) + int(fields[12])) / ticks_per_second


def missing_flags(flags: Iterable[str], level: Sequence[str]) -> list[str]:
    """Return the instruction sets of a level that the CPU does not report."""
    present = set(flags)
    return [flag for flag in level if flag not in present]


def cpu_flags(cpuinfo: str) -> Optional[set[str]]:
    """Return the CPU flags of /proc/cpuinfo, or None when the file has no x86 flags line."""
    match = re.search(r"^flags\s*:\s*(.*)$", cpuinfo, re.M)
    return set(match.group(1).split()) if match else None


def snapshot_threads(pid: int, ticks: int) -> dict[str, tuple[str, float]]:
    threads = {}
    for tid in os.listdir(f"/proc/{pid}/task"):
        text = read(f"/proc/{pid}/task/{tid}/stat")
        if text:
            threads[tid] = parse_thread_stat(text, ticks)
    return threads


def summarize_threads(before: Mapping[str, tuple[str, float]], after: Mapping[str, tuple[str, float]],
                      seconds: float) -> list[tuple[str, int, float, float]]:
    """Return (group, thread count, lifetime CPU seconds, percent of one core in the window), busiest first."""
    count: collections.Counter[str] = collections.Counter()
    lifetime: collections.Counter[str] = collections.Counter()
    window: collections.Counter[str] = collections.Counter()
    for tid, (name, cpu) in after.items():
        group = thread_group(name)
        count[group] += 1
        lifetime[group] += cpu
        if tid in before:
            window[group] += cpu - before[tid][1]
    rows = [(group, count[group], lifetime[group], 100 * window[group] / seconds if seconds > 0 else 0.0) for group in count]
    return sorted(rows, key=lambda row: (-row[3], -row[2]))


def report(pid: int, window: float) -> None:
    ticks = os.sysconf("SC_CLK_TCK")
    status = dict(re.findall(r"^(\w+):\s+(\d+)", read(f"/proc/{pid}/status"), re.M))
    if not status:
        raise SystemExit(f"error: cannot read /proc/{pid}/status; is the process running and readable?")

    flags = cpu_flags(read("/proc/cpuinfo"))
    if flags is not None:
        for label, level in (("x86-64-v2", X86_64_V2), ("x86-64-v3", X86_64_V3)):
            missing = missing_flags(flags, level)
            print(f"cpu {label}: {'yes' if not missing else 'no, missing ' + ' '.join(missing)}")

    print(f"process {pid}: " + " ".join(
        f"{key}={int(status[key]) // 1024}MB" for key in ("VmRSS", "VmHWM", "RssAnon", "RssFile", "RssShmem", "VmSwap") if key in status)
        + f" Threads={status.get('Threads', '?')}")

    print("resident memory by backing:")
    for backing, kilobytes in sorted(group_smaps(read(f"/proc/{pid}/smaps")).items(), key=lambda item: -item[1])[:10]:
        print(f"  {kilobytes / 1024:8.1f} MB  {backing}")

    cgroup = read(f"/proc/{pid}/cgroup").strip().split("::")[-1]
    base = "/sys/fs/cgroup" + cgroup
    current = read(base + "/memory.current").strip()
    if current:
        peak = read(base + "/memory.peak").strip()
        limit = read(base + "/memory.max").strip()
        parts = dict(re.findall(r"^(anon|file|kernel|sock|file_mapped) (\d+)$", read(base + "/memory.stat"), re.M))
        print(f"cgroup: current={int(current) // 1048576}MB peak={int(peak) // 1048576 if peak.isdigit() else '?'}MB limit={limit} "
              + " ".join(f"{key}={int(value) // 1048576}MB" for key, value in parts.items()))
        print("cgroup events: " + read(base + "/memory.events").replace("\n", " ").strip())

    kinds: collections.Counter[str] = collections.Counter()
    try:
        for descriptor in os.listdir(f"/proc/{pid}/fd"):
            try:
                target = os.readlink(f"/proc/{pid}/fd/{descriptor}")
            except OSError:
                continue
            kinds["socket" if target.startswith("socket:") else "pipe" if target.startswith("pipe:")
                  else "anon_inode" if target.startswith("anon_inode") else "client archive" if target.endswith((".pkn", ".bfz"))
                  else "file"] += 1
        print("open descriptors: " + ", ".join(f"{kind} {number}" for kind, number in kinds.most_common()))
    except OSError:
        print("open descriptors: not readable by this user")

    before = snapshot_threads(pid, ticks)
    started = time.monotonic()
    time.sleep(window)
    after = snapshot_threads(pid, ticks)
    seconds = time.monotonic() - started
    rows = summarize_threads(before, after, seconds)
    print(f"threads by name over {seconds:.0f} s (count, lifetime cpu s, percent of one core in the window):")
    for group, number, lifetime, percent in rows[:16]:
        print(f"  {group:<30}{number:>4}{lifetime:>10.1f} s{percent:>8.2f} %")
    print(f"  total in the window: {sum(row[3] for row in rows):.2f} % of one core")


def main(argv: Optional[Sequence[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Read-only memory and thread report of a running process on Linux.")
    parser.add_argument("--pid", type=int, required=True, help="Process id as the host sees it.")
    parser.add_argument("--window", type=float, default=20, help="Seconds over which thread CPU is sampled.")
    args = parser.parse_args(argv)
    if not sys.platform.startswith("linux"):
        raise SystemExit("error: this report reads /proc and runs on Linux only.")
    report(args.pid, args.window)
    return 0


if __name__ == "__main__":
    sys.exit(main())
