import assert from "node:assert/strict";
import { test } from "node:test";
import { PassThrough } from "node:stream";
import { Runtime } from "../src/runtime.js";
import { PluginSpec } from "../src/declaration.js";
import { Context } from "../src/context.js";

test("readiness installs the signal before cancellation; EOF cancels remaining activations", { timeout: 5000 }, async () => {
  const contexts: Context[] = [];
  class Plugin { activate(ctx: Context): void { contexts.push(ctx); } }
  const spec = new PluginSpec("probe", "1.0.0", "", [], [], [], [], [], [], [], {}, Plugin);
  const input = new PassThrough();
  const output = new PassThrough();
  const runtime = new Runtime([spec], input, output);
  const serving = runtime.serve();
  const messages: Record<string, unknown>[] = [];
  const waiters: ((message: Record<string, unknown>) => void)[] = [];
  output.on("data", (line: Buffer) => {
    const message = JSON.parse(line.toString()) as Record<string, unknown>;
    const resolve = waiters.shift();
    if (resolve) resolve(message); else messages.push(message);
  });
  const receive = () => messages.length ? Promise.resolve(messages.shift()!) :
    new Promise<Record<string, unknown>>(resolve => waiters.push(resolve));
  const send = (message: object) => input.write(JSON.stringify(message) + "\n");
  try {
    send({ id: 1, method: "initialize", params: { protocol_versions: [2], features: ["lifecycle.cancel"] } });
    assert.equal((await receive()).id, 1);
    send({ id: 2, method: "activate", params: { activation: "a", plugin: "probe", config: {} } });
    const ready = await receive();
    assert.equal(ready.method, "lifecycle.ready");
    send({ method: "lifecycle.cancel", params: { activation: "a" } });
    send({ id: ready.id, result: {} });
    const failed = await receive();
    assert.equal(failed.id, 2);
    assert.ok(failed.error);
    assert.equal(contexts.length, 0);

    send({ id: 3, method: "activate", params: { activation: "b", plugin: "probe", config: {} } });
    const secondReady = await receive();
    send({ id: secondReady.id, result: {} });
    assert.equal((await receive()).id, 3);
    send({ method: "lifecycle.cancel", params: { activation: "missing" } });
    send({ method: "lifecycle.cancel", params: { activation: "a" } });
    assert.equal(contexts[0].lifetime.aborted, false);
    input.end();
    assert.equal(await serving, 0);
    assert.equal(contexts[0].lifetime.aborted, true);
  } finally {
    input.end();
    await serving;
  }
});
