/**
 * Runs the shared sdk-protocol fixtures against the TypeScript SDK.
 *
 * Usage: `node dist/tests/run-conformance.js [--fixtures DIR] [--command EXE ARG...]`
 *
 * A fixture is a JSON-lines file. The first line is a header `{"ignore": [...], "note": ...}` whose
 * `ignore` entries are dotted paths (from the message root, for example `result.sdk`) removed from both
 * the expected and the actual message before comparing. Every later line is `{"dir": "h2p"|"p2h",
 * "msg": {...}}` or `{"expect_exit": N}`. The runner starts one plugin process per fixture, writes each
 * `h2p` message to its stdin and reads one line from its stdout for each `p2h` message, requiring an
 * exact match. Ids and handles are compared exactly. Without `expect_exit` the runner closes stdin and
 * requires a clean exit with no further output. Exit status 0 means every fixture passed.
 */

import { spawn, type ChildProcess } from "node:child_process";
import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const DEFAULT_FIXTURES = resolve(HERE, "../../../..", "tests/fixtures/sdk-protocol");
const TIMEOUT_MS = 10_000;

type JsonObject = Record<string, unknown>;

/** Returns a copy of `message` with every ignored dotted path removed. */
function strip(message: JsonObject, ignore: string[]): JsonObject {
  const result = JSON.parse(JSON.stringify(message)) as JsonObject;
  for (const path of ignore) {
    const parts = path.split(".");
    let node: unknown = result;
    for (const part of parts.slice(0, -1))
      node = isRecord(node) ? node[part] : null;
    if (isRecord(node)) delete node[parts[parts.length - 1]];
  }
  return result;
}

function isRecord(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Structural equality matching Python's dict/list comparison. */
function deepEqual(want: unknown, got: unknown): boolean {
  if (want === got) return true;
  if (Array.isArray(want) || Array.isArray(got)) {
    return (
      Array.isArray(want) &&
      Array.isArray(got) &&
      want.length === got.length &&
      want.every((item, index) => deepEqual(item, got[index]))
    );
  }
  if (!isRecord(want) || !isRecord(got)) return false;
  const wantKeys = Object.keys(want);
  const gotKeys = Object.keys(got);
  return (
    wantKeys.length === gotKeys.length &&
    wantKeys.every((key) => key in got && deepEqual(want[key], got[key]))
  );
}

/** JSON.stringify with object keys sorted, for readable diff output. */
function sortedJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(sortedJson).join(",")}]`;
  if (isRecord(value))
    return `{${Object.keys(value)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${sortedJson(value[key])}`)
      .join(",")}}`;
  return JSON.stringify(value) ?? "null";
}

/** One running plugin process with stdout lines fed into a queue. */
class Plugin {
  private readonly child: ChildProcess;
  private readonly lines: (string | null)[] = [];
  private readonly waiters: ((line: string | null) => void)[] = [];
  private readonly stderrChunks: Buffer[] = [];
  private readonly exited: Promise<number | null>;
  private inputBuffer: Buffer = Buffer.alloc(0);
  private stdoutEnded = false;

  constructor(command: string[]) {
    this.child = spawn(command[0], command.slice(1), { stdio: ["pipe", "pipe", "pipe"] });
    this.child.once("error", () => {}); // a spawn failure surfaces through the exit path instead
    this.child.stdin!.once("error", () => {}); // writes to a dead child fail silently, like the Python runner
    this.child.stdout!.on("data", (chunk: Buffer) => {
      this.inputBuffer = this.inputBuffer.length ? Buffer.concat([this.inputBuffer, chunk]) : chunk;
      let newline: number;
      while ((newline = this.inputBuffer.indexOf(0x0a)) >= 0) {
        const line = this.inputBuffer.subarray(0, newline).toString("utf-8");
        this.inputBuffer = this.inputBuffer.subarray(newline + 1);
        this.pushLine(line);
      }
    });
    this.child.stdout!.once("end", () => {
      this.stdoutEnded = true;
      this.pushLine(null);
    });
    this.child.stderr!.on("data", (chunk: Buffer) => this.stderrChunks.push(chunk));
    this.exited = new Promise((resolveExit) => this.child.once("exit", (code) => resolveExit(code)));
  }

  /** Queues a line, or resolves the pending receive() if one is waiting. */
  private pushLine(line: string | null): void {
    const waiter = this.waiters.shift();
    if (waiter) waiter(line);
    else this.lines.push(line);
  }

  private nextLine(timeoutMs: number): Promise<string | null> {
    const queued = this.lines.shift();
    if (queued !== undefined) return Promise.resolve(queued);
    return new Promise((resolveLine, reject) => {
      const deliver = (line: string | null) => {
        clearTimeout(timer);
        resolveLine(line);
      };
      const timer = setTimeout(() => {
        const index = this.waiters.indexOf(deliver);
        if (index >= 0) this.waiters.splice(index, 1);
        reject(new Error("timed out waiting for the plugin to write a line"));
      }, timeoutMs);
      this.waiters.push(deliver);
    });
  }

  send(message: JsonObject): void {
    this.child.stdin!.write(JSON.stringify(message) + "\n");
  }

  async receive(): Promise<JsonObject> {
    const line = await this.nextLine(TIMEOUT_MS);
    if (line === null) throw new Error("the plugin closed stdout before writing the expected line");
    return JSON.parse(line) as JsonObject;
  }

  async finish(expectedCode: number): Promise<void> {
    this.child.stdin!.end();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const code = await Promise.race([
      this.exited,
      new Promise<"timeout">((resolveTimeout) => {
        timer = setTimeout(() => resolveTimeout("timeout"), TIMEOUT_MS);
      }),
    ]);
    clearTimeout(timer);
    if (code === "timeout") {
      this.kill();
      throw new Error("the plugin did not exit");
    }
    if (code !== expectedCode) throw new Error(`exit code ${code}, expected ${expectedCode}`);
    // Any output written after the script's last expected line is a failure; the null
    // sentinel marks stdout's end of stream and is not output.
    const deadline = Date.now() + 1000;
    while (!this.stdoutEnded && Date.now() < deadline) await delay(10);
    const extra: string[] = [];
    for (const line of this.lines) {
      if (line === null) break;
      extra.push(line);
    }
    if (extra.length) throw new Error(`unexpected extra output after the script: ${JSON.stringify(extra[0])}`);
  }

  kill(): void {
    if (this.child.exitCode === null && !this.child.killed) this.child.kill();
  }

  stderrText(): string {
    return Buffer.concat(this.stderrChunks).toString("utf-8").trim();
  }
}

const delay = (ms: number) => new Promise((resolveDelay) => setTimeout(resolveDelay, ms));

/** Plays one fixture against a fresh plugin process; throws on the first mismatch. */
async function runFixture(path: string, command: string[]): Promise<void> {
  const lines = readFileSync(path, "utf-8")
    .split(/\r?\n/)
    .filter((text) => text.trim())
    .map((text) => JSON.parse(text) as JsonObject);
  const [header, ...script] = lines;
  if (!isRecord(header) || !("ignore" in header) || "dir" in header)
    throw new Error("the first line must be the header with an 'ignore' list");
  const ignore = header.ignore as string[];
  const plugin = new Plugin(command);
  let expectedCode = 0;
  try {
    let number = 1;
    for (const step of script) {
      number++;
      if ("expect_exit" in step) {
        expectedCode = step.expect_exit as number;
        continue;
      }
      if (step.dir === "h2p") {
        plugin.send(step.msg as JsonObject);
        continue;
      }
      const actual = await plugin.receive();
      const want = strip(step.msg as JsonObject, ignore);
      const got = strip(actual, ignore);
      if (!deepEqual(want, got))
        throw new Error(`line ${number}: expected ${sortedJson(want)}\n          got      ${sortedJson(got)}`);
    }
    await plugin.finish(expectedCode);
  } catch (error) {
    plugin.kill();
    const stderr = plugin.stderrText();
    const cause = error instanceof Error ? error.message : String(error);
    throw new Error(cause + (stderr ? `\n--- plugin stderr ---\n${stderr}` : ""));
  } finally {
    plugin.kill();
  }
}

function parseArgs(argv: string[]): { fixtures: string; command: string[] } {
  let fixtures = DEFAULT_FIXTURES;
  let command = [process.execPath, join(HERE, "conformance-plugins.js")];
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--fixtures" && i + 1 < argv.length) fixtures = resolve(argv[++i]);
    else if (argv[i] === "--command" && i + 1 < argv.length) {
      command = argv.slice(i + 1);
      break;
    }
  }
  return { fixtures, command };
}

async function main(): Promise<number> {
  const { fixtures, command } = parseArgs(process.argv.slice(2));
  const files = readdirSync(fixtures)
    .filter((name) => name.endsWith(".jsonl"))
    .sort();
  if (!files.length) {
    process.stderr.write(`no fixtures found in ${fixtures}\n`);
    return 1;
  }
  let failed = 0;
  for (const name of files) {
    try {
      await runFixture(join(fixtures, name), command);
      console.log(`PASS ${name}`);
    } catch (error) {
      failed++;
      console.log(`FAIL ${name}: ${error instanceof Error ? error.message : error}`);
    }
  }
  console.log(`${files.length - failed}/${files.length} fixtures passed`);
  return failed ? 1 : 0;
}

process.exitCode = await main();
