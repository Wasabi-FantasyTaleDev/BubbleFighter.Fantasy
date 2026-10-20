"""Runs the shared sdk-protocol fixtures against the Python SDK.

Usage: ``python sdk/python/tests/run_conformance.py [--fixtures DIR] [--command EXE ARG...]``

A fixture is a JSON-lines file. The first line is a header ``{"ignore": [...], "note": ...}`` whose
``ignore`` entries are dotted paths (from the message root, for example ``result.sdk``) removed from both
the expected and the actual message before comparing. Every later line is ``{"dir": "h2p"|"p2h", "msg": {...}}``
or ``{"expect_exit": N}``. The runner starts one plugin process per fixture, writes each ``h2p`` message to its
stdin and reads one line from its stdout for each ``p2h`` message, requiring an exact match. Ids and handles are
compared exactly. Without ``expect_exit`` the runner closes stdin and requires a clean exit with no further output.
Exit status 0 means every fixture passed.
"""

from __future__ import annotations

import argparse
import copy
import json
import queue
import subprocess
import sys
import threading
from pathlib import Path
from typing import Any

HERE = Path(__file__).resolve().parent
DEFAULT_FIXTURES = HERE.parents[2] / "tests" / "fixtures" / "sdk-protocol"
TIMEOUT = 10.0


def _strip(message: dict[str, Any], ignore: list[str]) -> dict[str, Any]:
    result = copy.deepcopy(message)
    for path in ignore:
        parts = path.split(".")
        node: Any = result
        for part in parts[:-1]:
            node = node.get(part) if isinstance(node, dict) else None
        if isinstance(node, dict):
            node.pop(parts[-1], None)
    return result


class Plugin:
    """One running plugin process with a reader thread feeding stdout lines into a queue."""

    def __init__(self, command: list[str]) -> None:
        self.process = subprocess.Popen(
            command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE
        )
        self.lines: "queue.Queue[bytes | None]" = queue.Queue()
        self.stderr = bytearray()
        threading.Thread(target=self._pump_stdout, daemon=True).start()
        threading.Thread(target=self._pump_stderr, daemon=True).start()

    def _pump_stdout(self) -> None:
        assert self.process.stdout is not None
        for line in self.process.stdout:
            self.lines.put(line)
        self.lines.put(None)

    def _pump_stderr(self) -> None:
        assert self.process.stderr is not None
        for line in self.process.stderr:
            self.stderr.extend(line)

    def send(self, message: dict[str, Any]) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(message, separators=(",", ":")).encode("utf-8") + b"\n")
        self.process.stdin.flush()

    def receive(self) -> dict[str, Any]:
        try:
            line = self.lines.get(timeout=TIMEOUT)
        except queue.Empty:
            raise AssertionError("timed out waiting for the plugin to write a line") from None
        if line is None:
            raise AssertionError("the plugin closed stdout before writing the expected line")
        return json.loads(line)

    def finish(self, expected_code: int) -> None:
        if self.process.stdin and not self.process.stdin.closed:
            try:
                self.process.stdin.close()
            except OSError:
                pass
        try:
            code = self.process.wait(timeout=TIMEOUT)
        except subprocess.TimeoutExpired:
            self.process.kill()
            raise AssertionError("the plugin did not exit") from None
        if code != expected_code:
            raise AssertionError(f"exit code {code}, expected {expected_code}")
        extra = []
        while True:
            try:
                line = self.lines.get(timeout=1.0)
            except queue.Empty:
                break
            if line is None:
                break
            extra.append(line)
        if extra:
            raise AssertionError(f"unexpected extra output after the script: {extra[0]!r}")

    def kill(self) -> None:
        if self.process.poll() is None:
            self.process.kill()


def run_fixture(path: Path, command: list[str]) -> None:
    """Plays one fixture against a fresh plugin process; raises AssertionError on the first mismatch."""
    lines = [json.loads(text) for text in path.read_text(encoding="utf-8").splitlines() if text.strip()]
    header, script = lines[0], lines[1:]
    if "ignore" not in header or "dir" in header:
        raise AssertionError("the first line must be the header with an 'ignore' list")
    ignore = header["ignore"]
    plugin = Plugin(command)
    expected_code = 0
    try:
        for number, step in enumerate(script, start=2):
            if "expect_exit" in step:
                expected_code = step["expect_exit"]
                continue
            if step["dir"] == "h2p":
                plugin.send(step["msg"])
                continue
            actual = plugin.receive()
            want, got = _strip(step["msg"], ignore), _strip(actual, ignore)
            if want != got:
                raise AssertionError(
                    f"line {number}: expected {json.dumps(want, sort_keys=True)}\n          got      {json.dumps(got, sort_keys=True)}"
                )
        plugin.finish(expected_code)
    except AssertionError as error:
        plugin.kill()
        stderr = bytes(plugin.stderr).decode("utf-8", "replace").strip()
        raise AssertionError(f"{error}" + (f"\n--- plugin stderr ---\n{stderr}" if stderr else "")) from None
    finally:
        plugin.kill()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fixtures", type=Path, default=DEFAULT_FIXTURES)
    parser.add_argument("--command", nargs="+", default=[sys.executable, str(HERE / "conformance_plugins.py")])
    args = parser.parse_args()
    files = sorted(args.fixtures.glob("*.jsonl"))
    if not files:
        print(f"no fixtures found in {args.fixtures}", file=sys.stderr)
        return 1
    failed = 0
    for path in files:
        try:
            run_fixture(path, args.command)
            print(f"PASS {path.name}")
        except AssertionError as error:
            failed += 1
            print(f"FAIL {path.name}: {error}")
    print(f"{len(files) - failed}/{len(files)} fixtures passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
