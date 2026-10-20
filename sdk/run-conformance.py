"""Runs the shared sdk-protocol fixtures against every SDK in sdk/.

Usage: ``python sdk/run-conformance.py``. Each SDK whose toolchain is missing is reported as SKIP with the reason;
any FAIL makes the exit status nonzero. The fixtures are tests/fixtures/sdk-protocol; the per-fixture runner is
sdk/python/tests/run_conformance.py, which every SDK shares through its --command option.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RUNNER = ROOT / "sdk" / "python" / "tests" / "run_conformance.py"
EXE = ".exe" if sys.platform == "win32" else ""


def build(cmd: list[str], cwd: Path) -> str | None:
    """Runs a build command; returns None on success or the reason it failed."""
    if shutil.which(cmd[0]) is None:
        return f"{cmd[0]} not on PATH"
    done = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return None if done.returncode == 0 else f"{' '.join(cmd)} failed:\n{done.stderr[-2000:]}"


def main() -> int:
    out = Path(tempfile.mkdtemp(prefix="bff-sdk-"))
    try:
        return run(out)
    finally:
        # Build output and copied fixtures are scratch; left behind, every run adds to the system temp drive.
        shutil.rmtree(out, ignore_errors=True)


def run(out: Path) -> int:
    """Builds and checks every SDK with its build output under out; returns the process exit code."""
    rust_target = out / "rust-target"
    sdks: list[tuple[str, list[list[str]], Path, list[str], list[str]]] = [
        ("python", [[sys.executable, "-m", "unittest", "discover", "-s", "sdk/python/tests", "-p", "test_*.py"]], ROOT, [sys.executable, str(ROOT / "sdk/python/tests/conformance_plugins.py")],
         [sys.executable, str(ROOT / "sdk/python/examples/services_wallet.py")]),
        ("typescript", [["npm", "ci"], ["npm", "run", "build"], ["node", "--test", "dist/tests/wire-json.js", "dist/tests/lifetime.js"]], ROOT / "sdk/typescript",
         ["node", str(ROOT / "sdk/typescript/dist/tests/conformance-plugins.js")],
         ["node", str(ROOT / "sdk/typescript/dist/examples/services-wallet.js")]),
        ("go", [["go", "test", "./..."], ["go", "build", "-o", str(out / f"go-conformance{EXE}"), "./conformance"],
                ["go", "build", "-o", str(out / f"go-wallet{EXE}"), "./examples/services-wallet"]], ROOT / "sdk/go",
         [str(out / f"go-conformance{EXE}")], [str(out / f"go-wallet{EXE}")]),
        ("rust", [["cargo", "test", "--release", "--target-dir", str(rust_target)],
                  ["cargo", "build", "--release", "--examples", "--target-dir", str(rust_target)]],
         ROOT / "sdk/rust", [str(rust_target / "release/examples" / f"conformance{EXE}")],
         [str(rust_target / "release/examples" / f"services_wallet{EXE}")]),
    ]
    failed = 0

    def run_fixtures(name: str, fixture_dir: Path, filenames: list[str], plugin: list[str]) -> None:
        nonlocal failed
        selected = out / f"{name}-fixtures"
        selected.mkdir(exist_ok=True)
        for filename in filenames:
            shutil.copy2(fixture_dir / filename, selected / filename)
        done = subprocess.run([sys.executable, str(RUNNER), "--fixtures", str(selected), "--command", *plugin],
                              capture_output=True, text=True, encoding="utf-8", errors="replace")
        summary = (done.stdout.strip().splitlines() or ["no output"])[-1]
        print(f"{'PASS' if done.returncode == 0 else 'FAIL'} {name}: {summary}")
        if done.returncode != 0:
            print(done.stdout + done.stderr)
            failed += 1

    for name, steps, cwd, command, wallet in sdks:
        reason = None
        for step in steps:
            if name == "typescript" and step[0] == "npm":
                step = [shutil.which("npm") or "npm", *step[1:]]
            reason = build(step, cwd)
            if reason:
                break
        if reason and ("not on PATH" in reason):
            print(f"SKIP {name}: {reason}")
            continue
        if reason:
            print(f"FAIL {name}: {reason}")
            failed += 1
            continue
        run_fixtures(f"{name} sdk-protocol", ROOT / "tests/fixtures/sdk-protocol",
                     [path.name for path in (ROOT / "tests/fixtures/sdk-protocol").glob("*.jsonl")], command)
        v2 = ROOT / "tests/fixtures/sdk-protocol-v2"
        run_fixtures(f"{name} sdk-protocol-v2 wallet", v2, ["01-service-wallet.jsonl"], wallet)
        run_fixtures(f"{name} sdk-protocol-v2 middleware", v2,
                     ["02-message-middleware.jsonl", "03-message-middleware-single-use.jsonl"], command)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
