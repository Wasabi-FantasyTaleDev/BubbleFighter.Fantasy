# Performance tools

Two scripts that put a number on what the server costs, so that a change which claims to save memory or CPU can be
checked and a later change cannot quietly take the saving back. Standard-library Python 3.10+, no packages.

## measure.py: one isolated start

```sh
dotnet build BubbleFighter.Fantasy.sln -c Release
python tools/perf/measure.py --profile kr-812
python tools/perf/measure.py --profile all --budget tools/perf/budgets.json --json artifacts/perf/all.json
```

The script starts the newest Release build of the Host, waits for `Server started with N active row(s)`, and reads
the process from the operating system three times: at ready, after `--settle` seconds (default 15) and after
`--idle` further seconds (default 30) in which nothing connects. It then stops the process it started and nothing else.

The Host always runs with `tools/local-checks/isolated-memory.patch.yaml` as its **last** patch, so no other patch,
`.env` or `local.patch.yaml` can point it at a database. Sign-in is open and the admin token is random for the run
unless the caller's environment already sets them. Client data comes from `--client-data`, then `BFF_CLIENT_DATA`,
then `./client-data`.

### Warm and cold starts

The first start of a build parses the client's workbooks and writes what the rows built from them to a compiled
client data file; every later start reads that file. The two cost very different amounts, so the script says which
one it measured:

- `--compiled-data warm` (default): the script first runs `bff data compile <profile>` with the same patches into a
  scratch directory, which is not measured, and then measures a start that reads those files. This is what a server
  costs on every start but its first. The run fails if the measured start still built a catalog from the client data.
- `--compiled-data cold`: the scratch directory is empty, so the measured start parses everything and writes the
  files. Its budget is the entry named `<profile> cold`.
- `--compiled-data as-is`: `BFF_COMPILED_DATA` is left alone (the checkout's `state/compiled-data` by default).

The scratch directory is removed afterwards; the checkout's own compiled files are neither read nor written.

| Number | Meaning |
| --- | --- |
| `compiled_catalogs_read`, `compiled_catalogs_built` | Catalogs the start read from the compiled client data and built from the client data, summed over the builds. A warm start builds none. |
| `peak_mb` | Highest resident memory since the process started. This is what decides whether a small server survives the start. |
| `private_idle_mb` | Memory only this process holds, at the last sample (Windows private bytes, Linux `RssAnon`). |
| `working_set_idle_mb` | Resident memory at the last sample, including mapped program files the system can drop under pressure. |
| `startup_allocated_mb` | Managed memory allocated until ready, from the Host's own startup line. The best single measure of wasted start work: it does not depend on timing or on the collector's mood. |
| `startup_collections` | Garbage collections that ran until ready. |
| `heap_committed_mb` | Managed heap committed after the Host compacted it at ready: close to what the loaded profile really keeps. |
| `compaction_pause_ms` | How long that compaction blocked. |
| `start_cpu_s` | CPU seconds used until ready. Comparable between runs on one machine; wall time is not when the machine is busy. |
| `start_wall_s` | Seconds from process start to the ready line. |
| `idle_cpu_percent` | CPU in the idle window, as a percentage of one core. |
| `threads_idle` | Thread count at the last sample (Linux only). |

Useful options: `--env NAME=VALUE` sets a runtime variable for the measured Host (for example
`--env DOTNET_GCConserveMemory=9`), `--patch FILE` adds a profile patch before the isolated one, `--log FILE` keeps
the Host's output, `--label` names the run.

A run is one sample. Idle memory in particular depends on when the runtime last collected, so compare several runs
before drawing a conclusion from a difference of a few megabytes; the peak and the CPU seconds are steadier.

### Budgets

`budgets.json` holds, per operating system and profile, the highest value each number may reach. With `--budget` the
script prints every comparison and exits with code 2 when one is exceeded. A profile or operating system without an
entry is an error, and so is a budgeted number that could not be measured: a budget never passes by being absent.

The budgets are a ratchet. They start slightly above what `main` measures today; a change that lowers a number
lowers its budget in the same pull request, and a change that needs to raise one says why. Memory budgets carry over
between machines of one operating system and architecture family. `start_cpu_s` depends on the processor; its
budgets were taken on an 8-core laptop and are generous on purpose.

Budgets are checked on demand, not by the validation gate: a start of the `all` profile takes about a minute, and a
warm measurement starts it twice.

## procfs_report.py: a running process on Linux

```sh
python3 tools/perf/procfs_report.py --pid 12345
python3 tools/perf/procfs_report.py --pid "$(docker inspect -f '{{.State.Pid}}' <container>)" --window 20
```

Read-only: it opens files under `/proc` and `/sys/fs/cgroup`, sends no signal, attaches nothing and writes nothing.
It prints:

- whether the processor meets the x86-64-v2 and x86-64-v3 levels;
- resident memory grouped by what backs it: anonymous pages (managed heap and native allocations), the runtime's
  code and type data, mapped assemblies, native libraries, client archives;
- the cgroup v2 counters of the process's container, including the peak since start and the out-of-memory events;
- open descriptors by kind;
- CPU by thread name over `--window` seconds, which separates timers, thread-pool work and garbage collection.

## Going deeper

When a number moves and the reason is not obvious, the runtime's own diagnostic tools attribute it:

```sh
dotnet-counters monitor -p <pid> --counters System.Runtime      # heap by generation, committed bytes, collections
dotnet-gcdump collect -p <pid>                                  # live objects by type
dotnet-trace collect --profile gc-verbose -- dotnet <Host.dll> run --profile kr-812 --patch tools/local-checks/isolated-memory.patch.yaml
```

The last command records every sampled allocation of a start with its call stack; grouping those by the plugin on
the stack shows which row's activation allocates what.

## Tests

```sh
python -m unittest discover -s tools/perf -p "test_*.py"
```

The tests pin the `/proc` formats, the Host command lines (the isolated patch stays last), the log lines the script
reads and the budget rules. They
start no process.
