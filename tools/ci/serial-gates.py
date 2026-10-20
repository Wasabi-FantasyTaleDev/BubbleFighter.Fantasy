"""Run Release, SDK, serial test and packet/config gates in an isolated environment.

The default runs the complete gate and requires locally provisioned client data.
--public runs every tracked test project but excludes Category=LocalClientData
cases. It writes the exclusion into scope.json and must not be called a release
gate. Test counters are cross-checked against unique populated TRX result rows.

Independent test projects run at the same time, at most --jobs of them; the
timing-sensitive projects run one at a time afterwards. --jobs 1 runs every
project one at a time in the old serial way.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
TRX_NAMESPACE = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

# Projects whose tests assert timing, share network ports or need a quiet machine: they run alone,
# after the parallel batch, so they never compete with another test host.
SERIAL_PROJECTS = (
    "BubbleFighter.Fantasy.Scenario.Tests",
    "BubbleFighter.Fantasy.Architecture.Tests",
    "BubbleFighter.Fantasy.Loader.Remote.Tests",
    "BubbleFighter.Fantasy.Net.Tcp.Tests",
)

# These fixtures are required by specific LocalClientData cases, never an entire project.
LOCAL_DATA_PREREQUISITES: dict[str, tuple[str, str]] = {
    "BubbleFighter.Fantasy.Equipment.Tests": (
        "client-data/kr-812/data/Config/Config.bfz",
        "EquipmentSelectionTests reads each build's shop table from its client-data archives",
    ),
    "BubbleFighter.Fantasy.Fighters.Tests": (
        "client-data/kr-1256/data/Packs/Config_00000.pkn",
        "FighterCatalogTests and StoreBackedFighterRosterTests read kr-1256 Config/FighterConfig.xml from its packs",
    ),
    "BubbleFighter.Fantasy.Architecture.Tests": (
        "client-data/kr-1256/data/Packs/Config_00000.pkn",
        "FeatureProfileTests mounts the fighters row on kr-1256 Config/FighterConfig.xml from its packs",
    ),
    "BubbleFighter.Fantasy.Shop.Tests": (
        "client-data/kr-1256/data/Packs/Config_00000.pkn",
        "ShopRealCatalogTests reads Config/ShopItem2.xml from the kr-1256 packs",
    ),
    "BubbleFighter.Fantasy.Shop.Tests (pet overlay)": (
        "client-data/kr-1256/data/Packs/ConfigSh_00000.pkn",
        "PetRealCatalogTests reads Config/ShopItemPet.xml from the kr-1256 ConfigSh pack",
    ),
    "BubbleFighter.Fantasy.Shop.Tests (id-400 dump)": (
        "artifacts/shop-tables-survey/id400/xml/Config/ShopItem2.xml",
        "Id400EnterShopWireDumpTests asserts that the id-400 shop table exists",
    ),
}


def missing_local_data() -> list[dict[str, str]]:
    """Check every locally provisioned client-data and fixture path the LocalClientData cases read."""
    return [
        {"test": project, "missing": relative, "reason": reason}
        for project, (relative, reason) in LOCAL_DATA_PREREQUISITES.items()
        if not (ROOT / relative).is_file()
    ]


def run(command: list[str], log: Path, environment: dict[str, str]) -> None:
    """Preserve a command's complete output and fail on a nonzero exit code."""
    print("RUN", " ".join(command), flush=True)
    with log.open("w", encoding="utf-8") as stream:
        result = subprocess.run(command, cwd=ROOT, env=environment,
                                stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f"Exit {result.returncode}; see {log}")


def projects() -> list[Path]:
    """Discover tracked test projects, including projects outside the solution."""
    paths = subprocess.check_output(
        ["git", "ls-files", "tests/*/*.csproj"], cwd=ROOT, text=True).splitlines()
    result = sorted(ROOT / path for path in paths if Path(path).parent.name.endswith(".Tests"))
    if not result:
        raise RuntimeError("No tracked test projects were found.")
    return result


def counters(trx: Path) -> dict[str, str]:
    """Require passing counters and matching unique result rows, including no skips."""
    if not trx.is_file():
        raise RuntimeError(f"Missing test report: {trx}")
    root = ET.parse(trx).getroot()
    node = root.find(f".//{TRX_NAMESPACE}Counters")
    if node is None:
        raise RuntimeError(f"Missing test counters: {trx}")
    values = node.attrib
    total = int(values.get("total", "0"))
    incomplete = any(int(values.get(name, "0")) for name in
                     ("failed", "error", "timeout", "aborted", "notExecuted", "notRunnable", "disconnected"))
    rows = root.findall(f"./{TRX_NAMESPACE}Results/{TRX_NAMESPACE}UnitTestResult")
    identities = [row.get("executionId") for row in rows]
    if (total <= 0 or incomplete or int(values.get("executed", "0")) != total
            or int(values.get("passed", "0")) != total or len(rows) != total
            or any(row.get("outcome") != "Passed" for row in rows)
            or any(not identity for identity in identities) or len(set(identities)) != total):
        raise RuntimeError(f"Tests failed, skipped or did not execute: {trx}: {values}")
    return values


def is_serial(project: Path) -> bool:
    """Return true when a discovered project is one of the timing-sensitive ones."""
    return any(project.parent.name.endswith(name) for name in SERIAL_PROJECTS)


def test_command(project: Path, directory: Path, public: bool, no_build: bool = False) -> list[str]:
    """Filter local-data cases in public mode without omitting their project."""
    command = ["dotnet", "test", str(project), "-c", "Release", "-m:1", "-nr:false",
               "-p:UseSharedCompilation=false"]
    if no_build:
        # The single Release build already compiled every tracked project; two concurrent builds of
        # one project would corrupt its obj/ directory.
        command.append("--no-build")
    command += ["--logger", "trx;LogFileName=result.trx",
                "--results-directory", str(directory)]
    if public:
        command += ["--filter", "Category!=LocalClientData"]
    return command


def run_project(project: Path, output: Path, public: bool, no_build: bool,
                environment: dict[str, str]) -> dict[str, object]:
    """Run one project into its own results folder and return its validated counters."""
    directory = output / project.parent.name
    directory.mkdir()
    run(test_command(project, directory, public, no_build), directory / "console.log", environment)
    return {"project": project.parent.name, "counters": counters(directory / "result.trx")}


def run_tests(all_projects: list[Path], output: Path, public: bool, jobs: int,
              environment: dict[str, str]) -> list[dict[str, object]]:
    """Run independent projects concurrently, then the serial projects one at a time."""
    serial = [project for project in all_projects if is_serial(project)]
    parallel = [project for project in all_projects if not is_serial(project)]
    reports: list[dict[str, object]] = []
    failures: list[str] = []

    def record(report: dict[str, object]) -> None:
        reports.append(report)
        (output / "test-results.json").write_text(json.dumps(reports, indent=2), encoding="utf-8")
        counts = report["counters"]
        print("PASS", report["project"], counts["passed"] if isinstance(counts, dict) else "?", flush=True)

    if jobs == 1:
        # The old behavior: every project one at a time in discovery order, each allowed to build.
        for project in all_projects:
            record(run_project(project, output, public, False, environment))
        return reports

    with concurrent.futures.ThreadPoolExecutor(max_workers=jobs) as pool:
        futures = {pool.submit(run_project, project, output, public, True, environment): project
                   for project in parallel}
        for future in concurrent.futures.as_completed(futures):
            project = futures[future]
            try:
                record(future.result())
            except (OSError, RuntimeError, ET.ParseError, subprocess.SubprocessError) as error:
                failures.append(f"{project.parent.name}: {error}")
    if failures:
        raise RuntimeError("Parallel test projects failed: " + "; ".join(failures))
    for project in serial:
        record(run_project(project, output, public, False, environment))
    return reports


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-directory", required=True,
                        help="New directory for this attempt; an existing directory is refused.")
    parser.add_argument("--public", action="store_true",
                        help="Exclude only Category=LocalClientData cases in every project; "
                             "this is not the complete release gate.")
    parser.add_argument("--jobs", type=int, default=None,
                        help="Maximum independent test projects to run at once "
                             "(default: max(1, cpu_count // 2)).")
    args = parser.parse_args()
    if args.jobs is not None and args.jobs < 1:
        parser.error("--jobs must be at least 1")
    jobs = args.jobs if args.jobs is not None else max(1, (os.cpu_count() or 2) // 2)
    output = Path(args.results_directory).resolve()
    output.mkdir(parents=True, exist_ok=False)
    environment = dict(os.environ, BFF_MYSQL_PASSWORD="isolated-memory-unused")
    environment.pop("BFF_MYSQL_VERIFY_CONNECTION", None)
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    if environment.get("CI", "").lower() == "true":
        local_files = [ROOT / ".env", *ROOT.glob("profiles/*/local.patch.yaml")]
        if any(path.exists() for path in local_files):
            raise RuntimeError("CI must use a clean checkout without deployment-local environment or patches.")
    required = ["dotnet", "node", "npm", "go", "cargo", "rustup"]
    missing = [name for name in required if shutil.which(name) is None]
    if missing:
        raise RuntimeError("Missing validation toolchains: " + ", ".join(missing))

    all_projects = projects()
    missing_data = missing_local_data()
    if missing_data:
        print("LOCAL PREREQUISITES ABSENT (projects still run in public mode):", flush=True)
        for item in missing_data:
            print(f"  {item['test']}: needs {item['missing']} - {item['reason']}", flush=True)
        (output / "missing-local-data.json").write_text(
            json.dumps(missing_data, indent=2), encoding="utf-8")
        if not args.public:
            raise RuntimeError(
                "Local game data is missing for "
                + ", ".join(item["test"] for item in missing_data)
                + "; provision it or pass --public to run the "
                  "publicly reproducible gates only.")

    (output / "scope.json").write_text(json.dumps({
        "mode": "public" if args.public else "complete",
        "excluded_test_category": "LocalClientData" if args.public else None,
        "release_gate": not args.public,
        "jobs": jobs,
        "serial_projects": list(SERIAL_PROJECTS),
    }, indent=2), encoding="utf-8")

    run(["dotnet", "build", "BubbleFighter.Fantasy.sln", "-c", "Release", "-m:1",
         "-nr:false", "-p:UseSharedCompilation=false"], output / "release.log", environment)
    run([sys.executable, "sdk/run-conformance.py"], output / "sdk-conformance.log", environment)
    if any(line.startswith("SKIP ") for line in (output / "sdk-conformance.log").read_text(encoding="utf-8").splitlines()):
        raise RuntimeError("An SDK conformance gate was skipped.")

    reports = run_tests(all_projects, output, args.public, jobs, environment)

    host = ROOT / "src/BubbleFighter.Fantasy.Host/bin/Release/net10.0/BubbleFighter.Fantasy.Host.dll"
    run(["dotnet", str(host), "packets", "check"], output / "packets.log", environment)
    run(["dotnet", str(host), "run", "--profile", "kr-1256", "--patch",
         "tools/local-checks/isolated-memory.patch.yaml", "--dump-config"], output / "dump-config.log", environment)
    total = sum(int(report["counters"]["passed"]) for report in reports)  # type: ignore[index]
    mode = "public" if args.public else "complete"
    print(f"PASS {mode} gates: {len(reports)} populated test reports, {total} tests, no skips", flush=True)
    if args.public:
        print("EXCLUDED: Category=LocalClientData; complete release validation remains required", flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, RuntimeError, ET.ParseError, subprocess.SubprocessError) as error:
        print(f"FAIL {error}", file=sys.stderr, flush=True)
        sys.exit(1)
