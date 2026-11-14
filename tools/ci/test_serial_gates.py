"""Guard the gate runner against false success from empty, missing or skipped reports."""

import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("serial_gates", Path(__file__).with_name("serial-gates.py"))
assert SPEC is not None and SPEC.loader is not None
GATES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATES)


class Reports(unittest.TestCase):
    """Require actual execution from every tracked test project."""

    def test_only_complete_passing_reports_are_accepted(self):
        """Missing, empty, skipped and failing output cannot be reported as a green gate."""
        with tempfile.TemporaryDirectory() as directory:
            trx = Path(directory) / "result.trx"
            with self.assertRaises(RuntimeError):
                GATES.counters(trx)
            for counts in (
                'total="0" executed="0" passed="0"',
                'total="2" executed="1" passed="1"',
                'total="2" executed="2" passed="1"',
                'total="2" executed="2" passed="2" failed="1"',
            ):
                with self.subTest(counts=counts):
                    trx.write_text(f'<TestRun xmlns="{GATES.TRX_NAMESPACE[1:-1]}"><ResultSummary>'
                                   f'<Counters {counts}/></ResultSummary></TestRun>', encoding="utf-8")
                    with self.assertRaises(RuntimeError):
                        GATES.counters(trx)
            passing = (f'<TestRun xmlns="{GATES.TRX_NAMESPACE[1:-1]}"><Results>'
                       '<UnitTestResult executionId="a" outcome="Passed"/>'
                       '<UnitTestResult executionId="b" outcome="Passed"/></Results><ResultSummary>'
                       '<Counters total="2" executed="2" passed="2"/></ResultSummary></TestRun>')
            for malformed in (passing.replace('<UnitTestResult executionId="b" outcome="Passed"/>', ''),
                              passing.replace('executionId="b"', 'executionId="a"'),
                              passing.replace('executionId="b" outcome="Passed"', 'executionId="b" outcome="Failed"')):
                trx.write_text(malformed, encoding="utf-8")
                with self.assertRaises(RuntimeError):
                    GATES.counters(trx)
            trx.write_text(passing, encoding="utf-8")
            self.assertEqual("2", GATES.counters(trx)["passed"])

    def test_public_mode_keeps_equipment_and_shop_projects(self):
        """Only local-data cases are filtered; self-contained coverage always runs."""
        for name in ('Equipment', 'Shop'):
            project = Path(f'tests/BubbleFighter.Fantasy.{name}.Tests/Test.csproj')
            command = GATES.test_command(project, Path('results'), public=True)
            self.assertIn(str(project), command)
            self.assertEqual('Category!=LocalClientData', command[-1])
            self.assertNotIn('--filter', GATES.test_command(project, Path('results'), public=False))

    def test_discovery_keeps_tracked_projects_outside_the_solution(self):
        """Solution membership and absent prior build outputs cannot exclude a tracked test project."""
        files = '\n'.join([
            "tests/Example.Tests/Example.Tests.csproj",
            "tests/Example.Tools.Tests/Example.Tools.Tests.csproj",
            "tests/Example.Helper/Example.Helper.csproj",
        ])
        with patch.object(GATES.subprocess, "check_output", return_value=files):
            self.assertEqual(["Example.Tests", "Example.Tools.Tests"],
                             [path.parent.name for path in GATES.projects()])

    def test_complete_mode_requires_every_local_client_data_file(self):
        """Every LocalClientData prerequisite is checked by its own path, not by a containing directory."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(GATES, 'ROOT', root), patch.dict(GATES.os.environ, {}, clear=True):
                self.assertEqual(len(GATES.LOCAL_DATA_PREREQUISITES) + len(GATES.LOCAL_DATA_ENV_PREREQUISITES),
                                 len(GATES.missing_local_data()))
                for relative, _ in GATES.LOCAL_DATA_PREREQUISITES.values():
                    file = root / relative
                    file.parent.mkdir(parents=True, exist_ok=True)
                    file.touch()
                self.assertEqual(set(GATES.LOCAL_DATA_ENV_PREREQUISITES),
                                 {item['missing'] for item in GATES.missing_local_data()})
                for variable in GATES.LOCAL_DATA_ENV_PREREQUISITES:
                    file = root / variable / 'Config.bfz'
                    GATES.os.environ[variable] = str(file)
                    file.parent.mkdir(parents=True)
                    self.assertIn(variable, {item['missing'] for item in GATES.missing_local_data()})
                    file.touch()
                self.assertEqual([], GATES.missing_local_data())

    def test_discovery_includes_new_untracked_projects_once(self):
        """A fresh plugin test project cannot disappear from a pre-commit gate."""
        files = '\n'.join([
            "tests/Example.Tests/Example.Tests.csproj",
            "tests/New.Tests/New.Tests.csproj",
            "tests/New.Tests/New.Tests.csproj",
        ])
        with patch.object(GATES.subprocess, "check_output", return_value=files) as discover:
            self.assertEqual(["Example.Tests", "New.Tests"], [path.parent.name for path in GATES.projects()])
            command = discover.call_args.args[0]
            self.assertIn("--cached", command)
            self.assertIn("--others", command)
            self.assertIn("--exclude-standard", command)


class Ordering(unittest.TestCase):
    """Independent projects run together and timing-sensitive projects run afterwards, alone."""

    @staticmethod
    def _projects() -> list[Path]:
        names = ("Archives", "Scenario", "Kernel", "Net.Tcp", "Net.Udp", "Lobby", "Architecture",
                 "Loader.Remote", "Shop", "WebUi", "Cluster")
        return [Path(f"tests/BubbleFighter.Fantasy.{name}.Tests/BubbleFighter.Fantasy.{name}.Tests.csproj")
                for name in names]

    @staticmethod
    def _fake(calls: list[tuple[str, bool]], fail_suffix: str | None = None):
        def run_project(project, output, public, no_build, environment):
            calls.append((project.parent.name, no_build))
            if fail_suffix is not None and project.parent.name.endswith(fail_suffix):
                raise RuntimeError("Exit 1; see console.log")
            return {"project": project.parent.name, "counters": {"passed": "1"}}
        return run_project

    def test_serial_projects_run_after_the_parallel_batch(self):
        """A timing-sensitive project never shares the machine with a parallel one."""
        calls: list[tuple[str, bool]] = []
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(GATES, "run_project", side_effect=self._fake(calls)):
                reports = GATES.run_tests(self._projects(), Path(directory), False, 4, {})
        self.assertEqual(11, len(reports))
        self.assertEqual(set(GATES.SERIAL_PROJECTS), {name for name, _ in calls[4:]})
        self.assertTrue(all(name not in GATES.SERIAL_PROJECTS for name, _ in calls[:4]))

    def test_every_project_relies_on_the_single_release_build(self):
        """Neither the parallel nor the serial batch compiles a second time."""
        calls: list[tuple[str, bool]] = []
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(GATES, "run_project", side_effect=self._fake(calls)):
                GATES.run_tests(self._projects(), Path(directory), False, 2, {})
        self.assertTrue(all(no_build for _, no_build in calls))

    def test_scenario_runs_alone_with_a_few_collections_and_others_one_at_a_time(self):
        """Quiet projects stay alone; only their own collections may overlap where allowed."""
        scenario = GATES.test_command(Path("tests/BubbleFighter.Fantasy.Scenario.Tests/x.csproj"), Path("r"), True, True)
        self.assertIn("xUnit.MaxParallelThreads=4", scenario)
        self.assertNotIn("xUnit.ParallelizeTestCollections=false", scenario)
        cluster = GATES.test_command(Path("tests/BubbleFighter.Fantasy.Cluster.Tests/x.csproj"), Path("r"), True, True)
        self.assertIn("xUnit.MaxParallelThreads=1", cluster)

    def test_affected_selection_follows_references_and_falls_back_to_everything(self):
        """A change reaches every project that references it; an unowned change selects all."""
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            def project(path, refs=()):
                file = root / path
                file.parent.mkdir(parents=True, exist_ok=True)
                items = "".join(f'<ProjectReference Include="{ref}" />' for ref in refs)
                file.write_text(f"<Project><ItemGroup>{items}</ItemGroup></Project>", encoding="utf-8")
                return file
            core = project("src/Core/Core.csproj")
            plugin = project("src/Plugins/A/A.csproj", ["..\\..\\Core\\Core.csproj"])
            other = project("src/Plugins/B/B.csproj")
            uses_plugin = project("tests/A.Tests/A.Tests.csproj", ["..\\..\\src\\Plugins\\A\\A.csproj"])
            uses_all = project("tests/All.Tests/All.Tests.csproj", ["..\\..\\src\\Plugins\\*\\*.csproj"])
            uses_other = project("tests/B.Tests/B.Tests.csproj", ["..\\..\\src\\Plugins\\B\\B.csproj"])
            tests = [uses_plugin, uses_all, uses_other]
            listing = "\n".join(path.relative_to(root).as_posix() for path in (core, plugin, other, *tests))
            with patch.object(GATES, "ROOT", root), patch.object(GATES.subprocess, "check_output", return_value=listing):
                self.assertEqual([uses_plugin, uses_all], GATES.affected_projects(tests, ["src/Core/Thing.cs"]))
                self.assertEqual([uses_all, uses_other], GATES.affected_projects(tests, ["src/Plugins/B/B.cs"]))
                self.assertEqual([], GATES.affected_projects(tests, ["docs/guide.md", "CHANGELOG.md"]))
                self.assertIsNone(GATES.affected_projects(tests, ["profiles/all/patch.yaml"]))

    def test_jobs_one_runs_every_project_one_at_a_time_in_the_old_order(self):
        """--jobs 1 is the previous serial behavior, discovery order included."""
        calls: list[tuple[str, bool]] = []
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(GATES, "run_project", side_effect=self._fake(calls)):
                GATES.run_tests(self._projects(), Path(directory), False, 1, {})
        self.assertEqual([project.parent.name for project in self._projects()],
                         [name for name, _ in calls])
        self.assertTrue(all(not no_build for _, no_build in calls))

    def test_a_failing_parallel_project_fails_the_run_before_the_serial_batch(self):
        """A failure cannot be hidden by the serial projects that would come after it."""
        calls: list[tuple[str, bool]] = []
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(GATES, "run_project",
                              side_effect=self._fake(calls, fail_suffix="Lobby.Tests")):
                with self.assertRaises(RuntimeError) as raised:
                    GATES.run_tests(self._projects(), Path(directory), False, 2, {})
        self.assertIn("BubbleFighter.Fantasy.Lobby.Tests", str(raised.exception))
        self.assertFalse(any(name in GATES.SERIAL_PROJECTS for name, _ in calls))

    def test_no_build_is_only_added_when_requested(self):
        """The parallel command must never compile a second time."""
        project = Path("tests/BubbleFighter.Fantasy.Kernel.Tests/Test.csproj")
        self.assertIn("--no-build", GATES.test_command(project, Path("results"), False, True))
        self.assertNotIn("--no-build", GATES.test_command(project, Path("results"), False, False))


if __name__ == "__main__":
    unittest.main()
