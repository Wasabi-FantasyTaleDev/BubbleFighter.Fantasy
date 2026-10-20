"""Verify independently loaded SQLite saves across bounded, redirected Host processes."""
from __future__ import annotations

import argparse
import json
import os
import shutil
import sqlite3
import subprocess
import threading
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host', type=Path, required=True)
    parser.add_argument('--plugin', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    root = output / 'plugins'
    shutil.copytree(args.plugin, root / 'BubbleFighter.Fantasy.Persistence.Sqlite')
    (root / 'SqliteSaveFixture').mkdir()
    shutil.copyfile(args.fixture, root / 'SqliteSaveFixture/SqliteSaveFixture.dll')
    save = output / 'persistent/save.sqlite'
    processes = []
    results = []

    def launch(name: str, mode: str):
        case = output / name
        (case / 'profiles/scenario').mkdir(parents=True)
        (case / 'profiles/bundles').mkdir()
        (case / 'profiles/scenario/profile.yaml').write_text('id: scenario\nbundles: [game]\n', encoding='utf-8')
        (case / 'profiles/bundles/game.yaml').write_text(
            '- id: persistence\n  plugin: persistence.sqlite\n  config:\n'
            f'    database_path: {json.dumps(str(save))}\n    max_database_bytes: 1048576\n'
            '- id: fixture\n  plugin: test.sqlite.save\n  config:\n'
            f'    mode: {mode}\n', encoding='utf-8')
        options = {'creationflags': subprocess.CREATE_NO_WINDOW} if os.name == 'nt' else {}
        process = subprocess.Popen([args.dotnet, str(args.host.resolve()), 'run', '--profile', 'scenario',
            '--profiles-dir', str(case / 'profiles'), '--plugin-root', str(root), '--managed-stdio',
            '--log-level', 'Information'], cwd=case, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE, **options)
        processes.append(process)
        ready = threading.Event()
        overflow = threading.Event()

        def drain(stream, path):
            total = 0
            suffix = b''
            with path.open('wb') as sink:
                while data := stream.read1(4096):
                    total += len(data)
                    if total > 1048576:
                        overflow.set()
                        process.kill()
                        break
                    sink.write(data)
                    joined = suffix + data
                    if b'FT_HOST_READY 1\n' in joined.replace(b'\r\n', b'\n'):
                        ready.set()
                    suffix = joined[-64:]
        threads = [threading.Thread(target=drain, args=(stream, case / filename), daemon=True)
            for stream, filename in ((process.stdout, 'stdout.log'), (process.stderr, 'stderr.log'))]
        for thread in threads:
            thread.start()
        return process, ready, threads, overflow, case

    def stop(handle):
        process, ready, threads, overflow, case = handle
        process.stdin.write(b'stop\n')
        process.stdin.flush()
        assert process.wait(timeout=30) == 0, case
        for thread in threads:
            thread.join(timeout=3)
            assert not thread.is_alive()
        assert not overflow.is_set()
        assert not Path(str(save) + '-wal').exists(), 'WAL remained after owned shutdown'
        results.append({'case': case.name, 'ready': ready.is_set(), 'exit': process.returncode})

    try:
        seed = launch('seed-and-hold', 'seed')
        assert seed[1].wait(30), 'seed did not reach readiness'
        collision = launch('same-save-second-process', 'verify')
        assert collision[0].wait(timeout=30) != 0, 'second owner acquired same save'
        assert not collision[1].is_set(), 'conflicting owner emitted readiness'
        for thread in collision[2]:
            thread.join(timeout=3)
        results.append({'case': collision[4].name, 'ready': False, 'exit': collision[0].returncode})
        stop(seed)
        verify = launch('restart-restores-domain-state', 'verify')
        assert verify[1].wait(30), 'durable verification did not reach readiness'
        stop(verify)
        with sqlite3.connect(save) as connection:
            assert connection.execute('pragma user_version').fetchone()[0] == 1
            assert connection.execute('select count(*) from players').fetchone()[0] == 1
            connection.execute('pragma user_version=99')
        bad = launch('unsupported-schema-refused', 'verify')
        assert bad[0].wait(timeout=30) != 0, 'unsupported schema started'
        assert not bad[1].is_set()
        for thread in bad[2]:
            thread.join(timeout=3)
        results.append({'case': bad[4].name, 'ready': False, 'exit': bad[0].returncode})
        (output / 'verification.json').write_text(json.dumps({'passed': len(results), 'cases': results}, indent=2), encoding='utf-8')
        print(json.dumps({'passed': len(results), 'report': str(output / 'verification.json')}))
        return 0
    finally:
        for process in processes:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=10)


if __name__ == '__main__':
    raise SystemExit(main())
