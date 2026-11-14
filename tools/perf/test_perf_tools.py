#!/usr/bin/env python3
"""Unit tests for the parsing and decision helpers of the performance tools.

    python -m unittest discover -s tools/perf -p "test_*.py"

No test starts a Host or reads /proc: the operating-system formats are pinned with literal text.
"""

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import measure  # noqa: E402
import procfs_report  # noqa: E402

STATUS = """Name:\tdotnet
VmPeak:\t 3000000 kB
VmHWM:\t  621568 kB
VmRSS:\t  440320 kB
RssAnon:\t  239616 kB
RssFile:\t  104448 kB
Threads:\t27
"""

# The command name holds a space and a parenthesis on purpose: fields are counted after the last ")".
STAT = "4242 (.NET (TP) Worker) S 1 4242 4242 0 -1 4194560 100 0 0 0 1500 500 0 0 20 0 27 0 1000 0 0"

SMAPS = """7f0000000000-7f0000100000 rw-p 00000000 00:00 0
Rss:                 800 kB
Pss:                 800 kB
7f0000100000-7f0000200000 r--p 00000000 08:01 11 /app/plugins/Shop/BubbleFighter.Fantasy.Shop.dll
Rss:                 300 kB
7f0000200000-7f0000300000 r-xp 00000000 00:01 12 /memfd:doublemapper (deleted)
Rss:                 200 kB
7f0000300000-7f0000400000 r-xp 00000000 08:01 13 /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.0/libcoreclr.so
Rss:                 100 kB
7f0000400000-7f0000500000 r--p 00000000 08:01 14 /app/plugins/Lobby/BubbleFighter.Fantasy.Lobby.dll
Rss:                  50 kB
7ffc00000000-7ffc00021000 rw-p 00000000 00:00 0 [stack]
Rss:                  16 kB
"""


class ProcParsing(unittest.TestCase):
    def test_status_fields_are_read_in_kilobytes(self):
        status = measure.parse_proc_status(STATUS)
        self.assertEqual(status["VmRSS"], 440320)
        self.assertEqual(status["Threads"], 27)

    def test_cpu_is_counted_after_the_last_parenthesis(self):
        self.assertEqual(measure.parse_proc_stat_cpu(STAT, 100), 20.0)

    def test_sample_uses_anonymous_pages_as_private_memory(self):
        sample = measure.sample_from_status(measure.parse_proc_status(STATUS), 20.0)
        self.assertEqual(sample.working_set_mb, 430.0)
        self.assertEqual(sample.private_mb, 234.0)
        self.assertEqual(sample.peak_mb, 607.0)
        self.assertEqual(sample.threads, 27)

    def test_thread_stat_keeps_the_whole_name(self):
        self.assertEqual(procfs_report.parse_thread_stat(STAT, 100), (".NET (TP) Worker", 20.0))


class HostStart(unittest.TestCase):
    def test_isolated_patch_is_always_last(self):
        command = measure.host_command(Path("Host.dll"), "kr-812", ["a.yaml", "b.yaml"])
        self.assertEqual(command[-2:], ["--patch", measure.ISOLATED_PATCH])
        self.assertEqual(command[:5], ["dotnet", "Host.dll", "run", "--profile", "kr-812"])
        self.assertLess(command.index("b.yaml"), command.index(measure.ISOLATED_PATCH))

    def test_compile_command_starts_the_same_profile_with_the_same_patches(self):
        command = measure.host_command(Path("Host.dll"), "kr-812", ["a.yaml"], compile_only=True)
        self.assertEqual(command, ["dotnet", "Host.dll", "data", "compile", "kr-812", "--patch", "a.yaml",
                                   "--patch", measure.ISOLATED_PATCH])

    def test_compiled_data_line_is_read_per_clientdata_row(self):
        line = ("info: plugin:clientdata[0] clientdata: compiled client data 'D:\\state\\kr-812.bffdata': "
                "11 catalog(s) read, 2 built from the client data.")
        self.assertEqual(measure.parse_compiled_line(line), (11, 2))
        self.assertIsNone(measure.parse_compiled_line("info: clientdata: no compiled client data at 'x'; catalogs are built"))
        sample = measure.Sample(1, 1, 1, 1, None)
        metrics = measure.summarize(1, sample, sample, sample, 1, None, (11, 2))
        self.assertEqual((metrics["compiled_catalogs_read"], metrics["compiled_catalogs_built"]), (11, 2))
        self.assertIsNone(measure.summarize(1, sample, sample, sample, 1)["compiled_catalogs_built"])

    def test_a_cold_start_has_its_own_budget(self):
        self.assertEqual(measure.budget_name("kr-812", "warm"), "kr-812")
        self.assertEqual(measure.budget_name("kr-812", "as-is"), "kr-812")
        self.assertEqual(measure.budget_name("kr-812", "cold"), "kr-812 cold")

    def test_environment_keeps_caller_values_and_fills_the_rest(self):
        env = measure.host_environment({"BFF_ADMIN_TOKEN": "mine"}, Path("data"), ["DOTNET_GCConserveMemory=9", "EMPTY="])
        self.assertEqual(env["BFF_ADMIN_TOKEN"], "mine")
        self.assertEqual(env["BFF_LOGIN_AUTHENTICATION"], "open")
        self.assertEqual(env["BFF_CLIENT_DATA"], "data")
        self.assertEqual(env["DOTNET_GCConserveMemory"], "9")
        self.assertEqual(env["EMPTY"], "")
        self.assertNotIn("BFF_MYSQL_PASSWORD", env)

    def test_generated_admin_token_differs_per_run(self):
        first = measure.host_environment({}, None, [])["BFF_ADMIN_TOKEN"]
        second = measure.host_environment({}, None, [])["BFF_ADMIN_TOKEN"]
        self.assertNotEqual(first, second)

    def test_malformed_environment_override_is_refused(self):
        with self.assertRaises(SystemExit):
            measure.host_environment({}, None, ["NOVALUE"])

    def test_ready_line_is_recognized(self):
        self.assertTrue(measure.READY_PATTERN.search("info: Host[0] Server started with 46 active row(s)."))
        self.assertFalse(measure.READY_PATTERN.search("info: Host[0] Server is ready; launcher sign-in is accepted."))


class Budgets(unittest.TestCase):
    def sample(self, working_set, private, peak, cpu):
        return measure.Sample(working_set, private, peak, cpu, None)

    def test_summary_reports_idle_cpu_of_the_idle_window_only(self):
        metrics = measure.summarize(12.44, self.sample(260, 170, 560, 12.2), self.sample(258, 168, 564, 12.3),
                                    self.sample(255, 165, 564, 12.6), 30)
        self.assertEqual(metrics["start_wall_s"], 12.4)
        self.assertEqual(metrics["start_cpu_s"], 12.2)
        self.assertEqual(metrics["peak_mb"], 564)
        self.assertEqual(metrics["private_idle_mb"], 165)
        self.assertEqual(metrics["idle_cpu_percent"], 1.0)
        self.assertIsNone(metrics["threads_idle"])

    def test_startup_line_supplies_allocation_and_heap_numbers(self):
        line = ("info: BubbleFighter.Fantasy.Host.Program[0] Startup allocated 2283 MB in 360 collection(s); "
                "the managed heap was compacted from 158 MB to 47 MB committed in 141 ms.")
        startup = measure.parse_startup_line(line)
        self.assertEqual(startup, {"startup_allocated_mb": 2283, "startup_collections": 360,
                                   "heap_committed_mb": 47, "compaction_pause_ms": 141})
        self.assertIsNone(measure.parse_startup_line("info: Host[0] Server started with 46 active row(s)."))
        sample = self.sample(1, 1, 1, 1)
        metrics = measure.summarize(1, sample, sample, sample, 1, startup)
        self.assertEqual(metrics["startup_allocated_mb"], 2283)
        self.assertEqual(metrics["heap_committed_mb"], 47)
        self.assertIsNone(measure.summarize(1, sample, sample, sample, 1)["startup_allocated_mb"])

    def test_budget_passes_at_the_limit_and_fails_above_it(self):
        rows = measure.evaluate_budget({"peak_mb": 620.0, "start_cpu_s": 20.1}, {"peak_mb": 620, "start_cpu_s": 20})
        self.assertEqual(rows, [("peak_mb", 620.0, 620, True), ("start_cpu_s", 20.1, 20, False)])

    def test_unmeasured_metric_fails_its_budget(self):
        self.assertEqual(measure.evaluate_budget({"threads_idle": None}, {"threads_idle": 30}), [("threads_idle", None, 30, False)])
        self.assertEqual(measure.evaluate_budget({}, {"peak_mb": 1}), [("peak_mb", None, 1, False)])

    def test_missing_or_empty_budget_is_an_error(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "budgets.json"
            path.write_text(json.dumps({"windows": {"kr-812": {"peak_mb": 620}, "all": {}}}), encoding="utf-8")
            self.assertEqual(measure.load_budget(path, "windows", "kr-812"), {"peak_mb": 620})
            for platform, profile in (("linux", "kr-812"), ("windows", "cn-324"), ("windows", "all")):
                with self.assertRaises(SystemExit):
                    measure.load_budget(path, platform, profile)

    def test_shipped_budgets_name_only_reported_metrics(self):
        known = set(measure.summarize(1, self.sample(1, 1, 1, 1), self.sample(1, 1, 1, 1), self.sample(1, 1, 1, 1), 1))
        document = json.loads((Path(measure.__file__).parent / "budgets.json").read_text(encoding="utf-8"))
        for platform, profiles in document.items():
            for profile, budget in profiles.items():
                self.assertTrue(budget, f"{platform}/{profile} is empty")
                self.assertLessEqual(set(budget), known, f"{platform}/{profile} names an unknown metric")


class ProcfsReport(unittest.TestCase):
    def test_resident_memory_is_grouped_by_backing(self):
        self.assertEqual(procfs_report.group_smaps(SMAPS), {
            "anonymous (managed heap, native allocations)": 800,
            "mapped assemblies (.dll)": 350,
            "runtime code and type data (memfd)": 200,
            "native libraries (.so)": 100,
            "[stack]": 16,
        })

    def test_numbered_pool_threads_share_a_group(self):
        self.assertEqual(procfs_report.thread_group(".NET TP Worker-12"), ".NET TP Worker")
        self.assertEqual(procfs_report.thread_group(".NET Timer"), ".NET Timer")
        self.assertEqual(procfs_report.thread_group("12"), "12")

    def test_threads_are_ranked_by_cpu_in_the_window(self):
        before = {"1": (".NET Timer", 10.0), "2": (".NET TP Worker-1", 5.0)}
        after = {"1": (".NET Timer", 10.5), "2": (".NET TP Worker-1", 7.0), "3": (".NET TP Worker-2", 1.0)}
        rows = procfs_report.summarize_threads(before, after, 10)
        self.assertEqual(rows[0], (".NET TP Worker", 2, 8.0, 20.0))
        self.assertEqual(rows[1], (".NET Timer", 1, 10.5, 5.0))

    def test_cpu_levels_report_what_is_missing(self):
        flags = procfs_report.cpu_flags("processor\t: 0\nflags\t\t: fpu cx16 popcnt pni ssse3 sse4_1 sse4_2 avx\n")
        self.assertEqual(procfs_report.missing_flags(flags, procfs_report.X86_64_V2), [])
        self.assertEqual(procfs_report.missing_flags(flags, procfs_report.X86_64_V3),
                         ["avx2", "bmi1", "bmi2", "f16c", "fma", "abm", "movbe"])
        self.assertIsNone(procfs_report.cpu_flags("Features\t: fp asimd\n"))


if __name__ == "__main__":
    unittest.main()
