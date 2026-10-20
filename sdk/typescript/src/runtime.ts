/** The stdio JSON-RPC runtime: serves registered plugins to a host (spec sections 3, 4 and 8). */

import { format as formatArgs } from "node:util";
import type { Readable, Writable } from "node:stream";

import * as errors from "./errors.js";
import { ActivationState, Context, Session, ServiceCall, TaskCall, ResourceCall } from "./context.js";
import { EventCall } from "./events.js";
import { PluginSpec, registeredPlugins } from "./declaration.js";
import { parseWireJson, stringifyWireJson } from "./wire-json.js";

export const SDK_NAME = "bff-plugin-typescript";
export const SDK_VERSION = "0.1.0";
export const PROTOCOL_VERSION = 2;
export const DEFAULT_MAX_LINE_BYTES = 1_048_576;
export const DEFAULT_MAX_IN_FLIGHT = 64;
/** Teardown must reach a plugin whose callback slots are all in flight; only the lifecycle control methods use these slots. */
const CONTROL_RESERVE = 4;

type JsonObject = Record<string, unknown>;

interface PendingCall {
  resolve: (result: unknown) => void;
  reject: (error: unknown) => void;
  done: boolean;
}

/** Reads requests from `input`, dispatches them to plugin classes and writes responses to `output`. */
export class Runtime {
  /** The protocol selected during initialization; service calls require version 2. */
  protocolVersion = 1;
  private earlyLifetime = false;
  /** Whether the host can signal activation stop before asking for deactivation. */
  get supportsLifetimeCancellation(): boolean { return this.earlyLifetime; }
  private readonly specs = new Map<string, PluginSpec>();
  private readonly order: PluginSpec[];
  private readonly maxLine: number;
  private nextId = 0;
  private readonly pending = new Map<number, PendingCall>();
  private readonly activations = new Map<string, ActivationState>();
  /** Deactivated rows retained only while native-owned resource callbacks can still arrive. */
  private readonly retiredActivations = new Map<string, ActivationState>();
  private readonly tasks = new Set<Promise<void>>();
  private stopped = false;
  private exitCode = 0;
  private maxInFlight = DEFAULT_MAX_IN_FLIGHT;
  private inbound = 0;
  private readonly serviceCalls = new Map<string, AbortController>();
  private readonly taskCalls = new Map<string, AbortController>();
  private readonly resourceCalls = new Map<string, AbortController>();
  private readonly messageCalls = new Map<string, AbortController>();
  private inputBuffer: Buffer = Buffer.alloc(0);
  private servePromise: Promise<number> | null = null;
  private resolveServe: (() => void) | null = null;

  constructor(
    specs: PluginSpec[],
    private readonly input: Readable,
    private readonly output: Writable,
    maxLineBytes: number = DEFAULT_MAX_LINE_BYTES,
  ) {
    for (const spec of specs) this.specs.set(spec.id, spec);
    this.order = [...specs];
    this.maxLine = maxLineBytes;
  }

  // ----- output -----------------------------------------------------------------------------------------------

  private write(message: JsonObject): void {
    if (this.stopped) return; // the host is gone; late writes are dropped
    const data = Buffer.from(stringifyWireJson(message), "utf-8");
    if (data.length > this.maxLine)
      throw new errors.RpcError(
        errors.INTERNAL_ERROR,
        `message of ${data.length} bytes exceeds the ${this.maxLine}-byte line limit`,
      );
    this.output.write(Buffer.concat([data, Buffer.from("\n")]));
  }

  /** Sends a notification to the host. */
  notify(method: string, params: JsonObject): void {
    this.write({ jsonrpc: "2.0", method, params });
  }

  /** Sends a request to the host and returns its result; an error object rejects with `RpcError`. */
  async call(method: string, params: JsonObject): Promise<unknown> {
    if (this.stopped) throw new errors.ProtocolError("the host went away");
    if (this.pending.size >= this.maxInFlight)
      throw new errors.RpcError(errors.INTERNAL_ERROR, "host call exceeds max_in_flight");
    const requestId = ++this.nextId;
    const response = new Promise<unknown>((resolve, reject) => {
      this.pending.set(requestId, { resolve, reject, done: false });
    });
    try {
      this.write({ jsonrpc: "2.0", id: requestId, method, params });
      return await response;
    } finally {
      this.pending.delete(requestId);
    }
  }

  private respond(requestId: unknown, result?: unknown, error?: errors.RpcError): void {
    try {
      if (error) {
        const body: JsonObject = { code: error.code, message: error.message };
        if (error.data !== null && error.data !== undefined) body.data = error.data;
        this.write({ jsonrpc: "2.0", id: requestId, error: body });
      } else {
        this.write({ jsonrpc: "2.0", id: requestId, result: result ?? null });
      }
    } catch {
      // the pipe is already gone; nothing to report on
    }
  }

  // ----- input ------------------------------------------------------------------------------------------------

  /** Serves until the host sends `shutdown` or closes stdin; returns the process exit code. */
  async serve(): Promise<number> {
    if (!this.servePromise) {
      this.servePromise = (async () => {
        await new Promise<void>((resolve) => {
          this.resolveServe = resolve;
        });
        await this.shutdown();
        return this.exitCode;
      })();
      this.input.on("data", (chunk: Buffer | string) =>
        this.onData(typeof chunk === "string" ? Buffer.from(chunk, "utf-8") : chunk),
      );
      this.input.once("end", () => this.finish());
      this.input.once("error", () => this.finish());
      this.input.resume();
    }
    return this.servePromise;
  }

  private onData(chunk: Buffer): void {
    if (this.stopped) return;
    this.inputBuffer = this.inputBuffer.length ? Buffer.concat([this.inputBuffer, chunk]) : chunk;
    for (;;) {
      const newline = this.inputBuffer.indexOf(0x0a);
      if (newline < 0) {
        if (this.inputBuffer.length > this.maxLine) {
          this.failLineTooLong();
          return;
        }
        break;
      }
      const line = this.inputBuffer.subarray(0, newline);
      this.inputBuffer = this.inputBuffer.subarray(newline + 1);
      if (line.length > this.maxLine) {
        this.failLineTooLong();
        return;
      }
      if (!this.handleLine(line)) return;
    }
  }

  private failLineTooLong(): void {
    this.stderr(`bff_plugin: a host line exceeded ${this.maxLine} bytes; exiting`);
    this.exitCode = 2;
    this.finish();
  }

  /** Handles one complete line; returns false when the runtime should stop. */
  private handleLine(line: Buffer): boolean {
    const text = line.toString("utf-8");
    if (!text.trim()) return true;
    let message: unknown;
    try {
      message = parseWireJson(text);
      if (!isRecord(message)) throw new Error("a protocol line must be a JSON object");
    } catch (error) {
      this.stderr(`bff_plugin: unparseable host line (${errorMessage(error)}); exiting`);
      this.exitCode = 2;
      this.finish();
      return false;
    }
    this.dispatch(message);
    return !this.stopped;
  }

  private finish(): void {
    if (this.stopped) return;
    this.stopped = true;
    this.input.removeAllListeners("data");
    this.input.pause();
    const resolve = this.resolveServe;
    this.resolveServe = null;
    resolve?.();
  }

  private async shutdown(): Promise<void> {
    for (const state of this.activations.values()) state.lifetime.abort();
    for (const signal of this.serviceCalls.values()) signal.abort();
    for (const signal of this.taskCalls.values()) signal.abort();
    for (const signal of this.resourceCalls.values()) signal.abort();
    for (const signal of this.messageCalls.values()) signal.abort();
    // In-flight handlers cannot be cancelled the way asyncio cancels tasks; pending calls are
    // rejected so their callers unwind, and their writes are dropped because `stopped` is set.
    for (const entry of this.pending.values()) {
      if (!entry.done) {
        entry.done = true;
        entry.reject(new errors.ProtocolError("the host went away"));
      }
    }
    if (this.tasks.size) await Promise.allSettled([...this.tasks]);
  }

  private dispatch(message: JsonObject): void {
    const method = message.method;
    if (typeof method === "string") {
      if ("id" in message) {
        const control = method === "deactivate" || method === "shutdown";
        if (this.inbound >= this.maxInFlight + (control ? CONTROL_RESERVE : 0)) {
          this.respond(message.id, undefined, new errors.RpcError(errors.INTERNAL_ERROR,
            "host request exceeds max_in_flight or max_reentry_depth"));
          return;
        }
        this.inbound++;
        const params = isRecord(message.params) ? message.params : {};
        if (method === "services.invoke" || method === "events.invoke") {
          try {
            const key = JSON.stringify([method.split(".")[0], str(params, "activation"), str(params, "invocation")]);
            if (this.serviceCalls.has(key)) throw new errors.RpcError(errors.INVALID_PARAMS, "duplicate service invocation");
            this.serviceCalls.set(key, new AbortController());
          } catch (error) {
            this.inbound--;
            this.respond(message.id, undefined, error as errors.RpcError);
            return;
          }
        }
        if (method === "tasks.invoke") {
          try {
            const key = JSON.stringify([str(params, "activation"), str(params, "invocation")]);
            if (this.taskCalls.has(key)) throw new errors.RpcError(errors.INVALID_PARAMS, "duplicate task invocation");
            this.taskCalls.set(key, new AbortController());
          } catch (error) {
            this.inbound--;
            this.respond(message.id, undefined, error as errors.RpcError);
            return;
          }
        }
        if (method === "resources.invoke") {
          try {
            const key = JSON.stringify([str(params, "activation"), str(params, "invocation")]);
            if (this.resourceCalls.has(key)) throw new errors.RpcError(errors.INVALID_PARAMS, "duplicate resource invocation");
            this.resourceCalls.set(key, new AbortController());
          } catch (error) {
            this.inbound--;
            this.respond(message.id, undefined, error as errors.RpcError);
            return;
          }
        }
        if (method === "messages.invoke") {
          try {
            const key = JSON.stringify([str(params, "activation"), str(params, "invocation")]);
            if (this.messageCalls.has(key)) throw new errors.RpcError(errors.INVALID_PARAMS, "duplicate message middleware invocation");
            this.messageCalls.set(key, new AbortController());
          } catch (error) {
            this.inbound--;
            this.respond(message.id, undefined, error as errors.RpcError);
            return;
          }
        }
        this.spawn(this.serveRequest(message.id, method, params));
      } else if (method === "shutdown") {
        this.finish();
      } else if (method === "lifecycle.cancel" && isRecord(message.params)) {
        this.activations.get(message.params.activation as string)?.lifetime.abort();
      } else if ((method === "services.cancel" || method === "events.cancel") && isRecord(message.params)) {
        this.serviceCalls.get(JSON.stringify([method.split(".")[0], message.params.activation, message.params.invocation]))?.abort();
      } else if (method === "tasks.cancel" && isRecord(message.params)) {
        this.taskCalls.get(JSON.stringify([message.params.activation, message.params.invocation]))?.abort();
      } else if (method === "resources.cancel" && isRecord(message.params)) {
        this.resourceCalls.get(JSON.stringify([message.params.activation, message.params.invocation]))?.abort();
      } else if (method === "messages.cancel" && isRecord(message.params)) {
        this.messageCalls.get(JSON.stringify([message.params.activation, message.params.invocation]))?.abort();
      }
      return;
    }
    const requestId = message.id;
    const entry =
      typeof requestId === "number" && Number.isInteger(requestId) ? this.pending.get(requestId) : undefined;
    if (!entry || entry.done) {
      this.stderr(`bff_plugin: response to unknown request ${repr(requestId)} ignored`);
      return;
    }
    entry.done = true;
    if ("error" in message) {
      const error = isRecord(message.error) ? message.error : {};
      entry.reject(
        new errors.RpcError(toInt(error.code, errors.INTERNAL_ERROR), String(error.message ?? ""), error.data),
      );
    } else {
      entry.resolve(message.result);
    }
  }

  private spawn(work: Promise<void>): void {
    const task = work.catch((error) => this.stderr(`bff_plugin: request handler failed: ${describeError(error)}`));
    this.tasks.add(task);
    void task.finally(() => this.tasks.delete(task));
  }

  // ----- host-to-plugin methods -------------------------------------------------------------------------------

  private async serveRequest(requestId: unknown, method: string, params: JsonObject): Promise<void> {
    try {
      const handler = (
        {
          initialize: this.initialize,
          describe: this.describe,
          activate: this.activate,
          deactivate: this.deactivate,
          deliver: this.deliver,
          "services.invoke": this.invokeService,
          "events.invoke": this.invokeEvent,
          "tasks.invoke": this.invokeTask,
          "resources.invoke": this.invokeResource,
          "messages.invoke": this.invokeMessage,
        } as Record<string, (p: JsonObject) => Promise<unknown>>
      )[method];
      if (!handler) throw new errors.RpcError(errors.METHOD_NOT_FOUND, `unknown method ${repr(method)}`);
      const result = await handler.call(this, params);
      this.respond(requestId, result);
    } catch (error) {
      if (error instanceof errors.RpcError) this.respond(requestId, undefined, error);
      else this.respond(requestId, undefined, pluginError(error));
    } finally {
      if (method === "services.invoke" || method === "events.invoke")
        this.serviceCalls.delete(JSON.stringify([method.split(".")[0], params.activation, params.invocation]));
      if (method === "tasks.invoke")
        this.taskCalls.delete(JSON.stringify([params.activation, params.invocation]));
      if (method === "resources.invoke")
        this.resourceCalls.delete(JSON.stringify([params.activation, params.invocation]));
      if (method === "messages.invoke")
        this.messageCalls.delete(JSON.stringify([params.activation, params.invocation]));
      this.inbound--;
    }
  }

  private async initialize(params: JsonObject): Promise<JsonObject> {
    const offered = Array.isArray(params.protocol_versions) ? params.protocol_versions : [params.protocol_version];
    const selected = [2, 1].find(version => offered.includes(version));
    if (selected === undefined)
      throw new errors.RpcError(
        errors.INVALID_PARAMS,
        `this SDK speaks protocol version ${PROTOCOL_VERSION}, host asked for ${repr(params.protocol_version)}`,
      );
    const limit = "max_in_flight" in params ? params.max_in_flight : DEFAULT_MAX_IN_FLIGHT;
    const depth = "max_reentry_depth" in params ? params.max_reentry_depth : 8;
    for (const [name, value, maximum] of [["max_in_flight", limit, 4096], ["max_reentry_depth", depth, 64]] as const) {
      if (typeof value !== "number" || !Number.isInteger(value) || value < 1 || value > maximum)
        throw new errors.RpcError(errors.INVALID_PARAMS, `${name} must be an integer from 1 to ${maximum}`);
    }
    this.maxInFlight = limit as number;
    this.protocolVersion = selected;
    this.earlyLifetime = selected >= 2 && Array.isArray(params.features) && params.features.includes("lifecycle.cancel");
    return { protocol_version: selected, sdk: { name: SDK_NAME, version: SDK_VERSION } };
  }

  private async describe(_params: JsonObject): Promise<JsonObject> {
    return { definitions: this.order.map((spec) => spec.definition()) };
  }

  private async activate(params: JsonObject): Promise<JsonObject> {
    const activationId = str(params, "activation");
    const pluginId = str(params, "plugin");
    const spec = this.specs.get(pluginId);
    if (!spec) throw new errors.RpcError(errors.INVALID_PARAMS, `this process does not serve plugin ${repr(pluginId)}`);
    if (this.activations.has(activationId))
      throw new errors.RpcError(errors.INVALID_PARAMS, `activation ${repr(activationId)} is already active`);
    const config = params.config;
    if (!isRecord(config)) throw new errors.RpcError(errors.INVALID_PARAMS, "'config' must be an object");
    const state = new ActivationState(activationId, new spec.cls());
    const capacity = params.max_provider_callbacks ?? 64;
    if (typeof capacity !== "number" || !Number.isInteger(capacity) || capacity < 1 || capacity > 2147483647)
      throw new errors.RpcError(errors.INVALID_PARAMS, "max_provider_callbacks must be a positive int32");
    state.maxProviderCallbacks = capacity;
    state.context = new Context(
      this,
      state,
      String(params.row_id ?? ""),
      pluginId,
      config,
      Boolean(params.reload ?? false),
    );
    this.activations.set(activationId, state);
    try {
      // Install the signal before acknowledging readiness, so cancellation cannot overtake activation.
      if (this.earlyLifetime) await this.call("lifecycle.ready", { activation: activationId });
      state.lifetime.signal.throwIfAborted();
      await state.instance.activate(state.context);
    } catch (error) {
      state.lifetime.abort();
      // A failed activation leaves nothing behind: release what it registered, then report the failure.
      const failures = await state.disposeEffects();
      for (const failure of failures)
        this.stderr(`bff_plugin: rollback after failed activate also failed: ${describeError(failure)}`);
      this.activations.delete(activationId);
      state.live = false;
      throw error;
    }
    return {};
  }

  private async deactivate(params: JsonObject): Promise<JsonObject> {
    const state = this.live(params);
    state.live = false;
    state.lifetime.abort();
    const failures: unknown[] = [];
    const hook = state.instance.deactivate;
    if (typeof hook === "function") {
      try {
        await hook.call(state.instance, state.context!);
      } catch (error) {
        failures.push(error);
      }
    }
    failures.push(...(await state.disposeEffects()));
    this.activations.delete(state.id);
    if (state.resourceCallbacks.size) this.retiredActivations.set(state.id, state);
    if (failures.length) {
      const first = failures[0];
      throw new errors.RpcError(
        errors.PLUGIN_ERROR,
        `${failures.length} failure(s) while deactivating; first: ${errorName(first)}: ${errorMessage(first)}`,
      );
    }
    return {};
  }

  private async deliver(params: JsonObject): Promise<JsonObject> {
    const state = this.live(params);
    const handle = str(params, "handle");
    const handler = state.handlers.get(handle);
    if (!handler) throw new errors.RpcError(errors.INVALID_PARAMS, `unknown handle ${repr(handle)}`);
    const session = params.session;
    const message = params.message;
    if (!isRecord(session) || !isRecord(message) || !isRecord(message.body))
      throw new errors.RpcError(errors.INVALID_PARAMS, "'session' and 'message' {type, body} must be objects");
    await handler(Session.fromJson(session), message.body);
    return { handled: true };
  }

  private live(params: JsonObject): ActivationState {
    const activationId = str(params, "activation");
    const state = this.activations.get(activationId);
    if (!state || !state.live)
      throw new errors.RpcError(errors.ACTIVATION_NOT_LIVE, `activation ${repr(activationId)} is not live`);
    return state;
  }

  private async invokeService(params: JsonObject): Promise<unknown> {
    const state = this.live(params);
    const methods = state.providers.get(str(params, "callback"));
    const handler = methods?.get(str(params, "method")) ?? methods?.get(params.method_name as string);
    if (!handler || !isRecord(params.args)) throw new errors.RpcError(errors.INVALID_PARAMS, "unknown provider method or invalid args");
    const signal = this.serviceCalls.get(JSON.stringify(["services", state.id, str(params, "invocation")]))!;
    const call = new ServiceCall(this, params, signal.signal);
    try { return await handler(call); }
    finally { call.live = false; }
  }

  private stderr(text: string): void {
    process.stderr.write(text + "\n");
  }

  private async invokeEvent(params: JsonObject): Promise<unknown> {
    const state = this.live(params);
    const handler = state.eventCallbacks.get(str(params, "callback"));
    if (!handler || !isRecord(params.body)) throw new errors.RpcError(errors.INVALID_PARAMS, "unknown event callback or invalid body");
    const signal = this.serviceCalls.get(JSON.stringify(["events", state.id, str(params, "invocation")]))!;
    const call = new EventCall(this, params, signal.signal);
    try { return await handler(call); }
    finally { call.live = false; }
  }

  private async invokeTask(params: JsonObject): Promise<JsonObject> {
    const state = this.live(params);
    const callback = str(params, "callback");
    const work = state.taskCallbacks.get(callback);
    if (!work || typeof params.name !== "string") throw new errors.RpcError(errors.INVALID_PARAMS, "unknown task callback or invalid name");
    const signal = this.taskCalls.get(JSON.stringify([str(params, "activation"), str(params, "invocation")]))?.signal;
    if (!signal) throw new errors.RpcError(errors.INVALID_PARAMS, "unknown task invocation");
    await work(new TaskCall(signal));
    return {};
  }

  private async invokeMessage(params: JsonObject): Promise<JsonObject> {
    const state = this.live(params);
    const handle = str(params, "handle");
    const middleware = state.middleware.get(handle);
    const session = params.session;
    const message = params.message;
    if (!middleware || !isRecord(session) || !isRecord(message) || !isRecord(message.body))
      throw new errors.RpcError(errors.INVALID_PARAMS, "unknown middleware callback or invalid message");
    const invocation = str(params, "invocation");
    const signal = this.messageCalls.get(JSON.stringify([state.id, invocation]));
    if (!signal) throw new errors.RpcError(errors.INVALID_PARAMS, "message middleware invocation is not admitted");
    let used = false;
    const next = async (): Promise<void> => {
      if (used) throw new errors.RpcError(errors.INVALID_PARAMS, "message middleware next is single-use");
      used = true;
      await this.call("messages.next", { activation: state.id, invocation });
    };
    const result = middleware(Session.fromJson(session), message.body, next, signal.signal);
    if (isPromiseLike(result)) await result;
    return {};
  }

  private async invokeResource(params: JsonObject): Promise<JsonObject> {
    const activation = str(params, "activation");
    const state = this.activations.get(activation) ?? this.retiredActivations.get(activation);
    if (!state) throw new errors.RpcError(errors.ACTIVATION_NOT_LIVE, `activation ${repr(activation)} is not known`);
    const callback = str(params, "callback");
    const work = state.resourceCallbacks.get(callback);
    if (!work) throw new errors.RpcError(errors.INVALID_PARAMS, "unknown resource callback");
    const signal = this.resourceCalls.get(JSON.stringify([str(params, "activation"), str(params, "invocation")]))?.signal;
    if (!signal) throw new errors.RpcError(errors.INVALID_PARAMS, "resource invocation is not admitted");
    try {
      await work(new ResourceCall(signal, str(params, "invocation")));
      return {};
    } finally {
      state.resourceCallbacks.delete(callback);
      if (!state.resourceCallbacks.size) this.retiredActivations.delete(state.id);
    }
  }
}

function isRecord(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isPromiseLike(value: unknown): value is PromiseLike<unknown> {
  return typeof value === "object" && value !== null && typeof (value as { then?: unknown }).then === "function";
}

function str(params: JsonObject, name: string): string {
  const value = params[name];
  if (typeof value !== "string" || !value)
    throw new errors.RpcError(errors.INVALID_PARAMS, `'${name}' must be a nonempty string`);
  return value;
}

function toInt(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? Math.trunc(value) : fallback;
}

function repr(value: unknown): string {
  if (typeof value === "string") return `'${value}'`;
  try {
    return JSON.stringify(value) ?? String(value);
  } catch {
    return String(value);
  }
}

function errorName(error: unknown): string {
  return error instanceof Error ? error.constructor.name : typeof error;
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function describeError(error: unknown): string {
  return `${errorName(error)}: ${errorMessage(error)}`;
}

function pluginError(error: unknown): errors.RpcError {
  const trace = error instanceof Error && error.stack ? error.stack : String(error);
  return new errors.RpcError(errors.PLUGIN_ERROR, describeError(error), { traceback: trace });
}

/**
 * Serves every `@plugin` class over stdio until the host shuts the process down.
 *
 * stdout carries the protocol, so `console.log` and friends are redirected to
 * stderr (which the host logs).
 */
export function run(plugins?: PluginSpec[]): void {
  const specs = plugins ?? registeredPlugins();
  if (!specs.length)
    throw new Error("no @plugin classes are declared; import the module that declares them before run()");
  const protocolOut = process.stdout;
  const toStderr = (...args: unknown[]) => process.stderr.write(formatArgs(...args) + "\n");
  console.log = toStderr;
  console.info = toStderr;
  console.debug = toStderr;
  const runtime = new Runtime(specs, process.stdin, protocolOut);
  void runtime
    .serve()
    .then((code) => {
      // Flush queued protocol output before exiting; the callback fires after it drains.
      protocolOut.write("", () => process.exit(code));
    })
    .catch((error: unknown) => {
      process.stderr.write(`bff_plugin: serve failed: ${describeError(error)}\n`);
      process.exit(1);
    });
}
