/** The activation context handed to a plugin: config, logger, messages and effects. */

import type { PluginInstance } from "./declaration.js";
import type { Runtime } from "./runtime.js";
import { ACTIVATION_NOT_LIVE, METHOD_NOT_FOUND, RpcError } from "./errors.js";
import { Events, type EventHandler } from "./events.js";

/** The client session a message arrived on (spec section 6). */
export class Session {
  constructor(
    readonly id: string,
    readonly protocol: string,
    readonly player: number | bigint | null,
    readonly build: string,
  ) {}

  /** Builds a session from the `session` object of a `deliver` request. */
  static fromJson(value: Record<string, unknown>): Session {
    return new Session(
      value.id as string,
      value.protocol as string,
      (value.player ?? null) as number | bigint | null,
      value.build as string,
    );
  }
}

/** A host-side registration owned by this activation; disposing it releases it early. */
export class Handle {
  protected readonly runtime: Runtime;
  private readonly activation: string;
  readonly id: string;
  private disposed = false;

  constructor(runtime: Runtime, activation: string, handle: string) {
    this.runtime = runtime;
    this.activation = activation;
    this.id = handle;
  }

  /** Releases the registration. Disposing twice is a no-op. */
  async dispose(): Promise<void> {
    await this.disposeOwned();
  }

  /** @internal Validates callback ownership before restoring the native self-removal context. */
  async disposeFromEvent(runtime: Runtime, activation: string, invocation: string): Promise<void> {
    if (runtime !== this.runtime || activation !== this.activation) throw new Error("foreign event registration handle");
    await this.disposeOwned(invocation);
  }

  protected async disposeOwned(eventInvocation?: string): Promise<void> {
    if (this.disposed) return;
    this.disposed = true;
    await this.runtime.call("effect.dispose", { activation: this.activation, handle: this.id,
      ...(eventInvocation ? { event_invocation: eventInvocation } : {}) });
  }
}

/** One host-owned background task callback with cooperative cancellation. */
export class TaskCall {
  constructor(readonly signal: AbortSignal) {}

  /** Reports whether the host has requested this task to stop. */
  get cancelled(): boolean { return this.signal.aborted; }
}

/** A native-owned background task; waiting observes work and disposal cancels and joins it. */
export class TaskHandle extends Handle {
  constructor(runtime: Runtime, private readonly state: ActivationState, id: string, private readonly callback: string) {
    super(runtime, state.id, id);
  }

  /** Waits for callback work without dropping its native owner. */
  wait(): Promise<unknown> {
    return this.runtime.call("tasks.wait", { activation: this.state.id, task: this.id });
  }

  /** Requests cancellation and joins native cleanup. */
  override async dispose(): Promise<void> {
    await super.dispose();
    this.state.tasks.delete(this);
    this.state.taskCallbacks.delete(this.callback);
  }
}

/** Starts background work under the host's native task owner and shared effect budget. */
export class Tasks {
  constructor(private readonly runtime: Runtime, private readonly state: ActivationState) {}

  /** Starts work and returns a handle; pass it to `ctx.effect` for teardown ownership. */
  async run(name: string, work: (call: TaskCall) => unknown): Promise<TaskHandle> {
    if (!this.state.live || this.runtime.protocolVersion < 2) throw new Error("tasks require a live protocol v2 activation");
    if (!name) throw new TypeError("task name must be a nonempty string");
    if (typeof work !== "function") throw new TypeError("task work must be callable");
    const callback = `t${++this.state.nextTask}`;
    this.state.taskCallbacks.set(callback, work);
    try {
      const result = await this.runtime.call("tasks.start", { activation: this.state.id, name, callback }) as { task: string };
      const handle = new TaskHandle(this.runtime, this.state, result.task, callback);
      this.state.tasks.add(handle);
      return handle;
    } catch (error) {
      this.state.taskCallbacks.delete(callback);
      throw error;
    }
  }
}

/** One host-owned resource release callback with cooperative cancellation. */
export class ResourceCall {
  constructor(readonly signal: AbortSignal, readonly invocation: string) {}
  /** Reports whether the host has requested release to stop. */
  get cancelled(): boolean { return this.signal.aborted; }
}

/** A native-owned resource; disposal runs its registered release callback. */
export class ResourceHandle {
  private disposed = false;
  constructor(private readonly runtime: Runtime, private readonly state: ActivationState, readonly id: string,
    private readonly callback: string) {}
  /** Releases this resource early; repeated disposal is a no-op. */
  async dispose(releaseInvocation?: string): Promise<void> {
    if (this.disposed) return;
    const result = await this.runtime.call("resources.dispose", { activation: this.state.id, resource: this.id,
      ...(releaseInvocation ? { release_invocation: releaseInvocation } : {}) });
    this.disposed = !(isDeferred(result));
    if (this.disposed) {
      this.state.resourceCallbacks.delete(this.callback);
      this.state.resources.delete(this);
    }
  }
}

/** A child resource lifetime sharing the activation's bounded capacity. */
export class ResourceScope {
  private disposed = false;
  constructor(private readonly runtime: Runtime, private readonly state: ActivationState, readonly id: string) {}
  /** Creates a child scope. */
  async create(name: string): Promise<ResourceScope> {
    const result = await this.runtime.call("resources.scope", { activation: this.state.id, parent: this.id, name }) as { scope: string };
    const scope = new ResourceScope(this.runtime, this.state, result.scope);
    this.state.scopes.add(scope);
    return scope;
  }
  /** Acquires a resource whose release callback is owned by this scope. */
  async acquire(name: string, release: (call: ResourceCall) => unknown): Promise<ResourceHandle> {
    if (this.disposed) throw new Error("resource scope is disposed");
    if (!name) throw new TypeError("resource name must be a nonempty string");
    if (typeof release !== "function") throw new TypeError("resource release must be callable");
    const callback = `r${++this.state.nextResource}`;
    this.state.resourceCallbacks.set(callback, release);
    try {
      const result = await this.runtime.call("resources.acquire", { activation: this.state.id, scope: this.id, name, callback }) as { resource: string };
      const handle = new ResourceHandle(this.runtime, this.state, result.resource, callback);
      this.state.resources.add(handle);
      return handle;
    } catch (error) {
      this.state.resourceCallbacks.delete(callback);
      throw error;
    }
  }
  /** Disposes this scope and its native descendants. */
  async dispose(): Promise<void> {
    if (this.disposed) return;
    await this.runtime.call("resources.scope.dispose", { activation: this.state.id, scope: this.id });
    this.disposed = true;
    this.state.scopes.delete(this);
  }
}

/** Creates native-owned resource scopes for an activation. */
export class Resources {
  constructor(private readonly runtime: Runtime, private readonly state: ActivationState) {}
  /** Creates a root resource scope. */
  async scope(name: string): Promise<ResourceScope> {
    if (!this.state.live || this.runtime.protocolVersion < 2) throw new Error("resource scopes require a live protocol v2 activation");
    if (!name) throw new TypeError("resource scope name must be a nonempty string");
    const result = await this.runtime.call("resources.scope", { activation: this.state.id, name }) as { scope: string };
    const scope = new ResourceScope(this.runtime, this.state, result.scope);
    this.state.scopes.add(scope);
    return scope;
  }
}

/** Sends log notifications to the host, which writes them through the row's logger. */
export class Logger {
  constructor(
    private readonly runtime: Runtime,
    private readonly activation: string,
  ) {}

  private log(level: string, message: string, fields?: Record<string, unknown>): void {
    const params: Record<string, unknown> = { activation: this.activation, level, message: String(message) };
    if (fields && Object.keys(fields).length) params.fields = fields;
    this.runtime.notify("log", params);
  }

  /** Logs at trace level. */
  trace(message: string, fields?: Record<string, unknown>): void {
    this.log("trace", message, fields);
  }

  /** Logs at debug level. */
  debug(message: string, fields?: Record<string, unknown>): void {
    this.log("debug", message, fields);
  }

  /** Logs at information level. */
  info(message: string, fields?: Record<string, unknown>): void {
    this.log("info", message, fields);
  }

  /** Logs at warning level. */
  warn(message: string, fields?: Record<string, unknown>): void {
    this.log("warn", message, fields);
  }

  /** Alias for {@link warn}. */
  warning(message: string, fields?: Record<string, unknown>): void {
    this.warn(message, fields);
  }

  /** Logs at error level. */
  error(message: string, fields?: Record<string, unknown>): void {
    this.log("error", message, fields);
  }
}

/** A message handler: `(session, messageBody)`; plain or `async`. */
export type MessageHandler = (session: Session, body: Record<string, unknown>) => unknown;
/** A message middleware callback. `next` is single-use and continues the native pipeline. */
export type MessageMiddleware = (session: Session, body: Record<string, unknown>, next: () => Promise<void>, signal: AbortSignal) => unknown;

/** The typed message router and senders, as the plugin's activation sees them. */
export class Messages {
  constructor(
    private readonly runtime: Runtime,
    private readonly activation: ActivationState,
  ) {}

  /**
   * Registers `handler(session, messageBody)` for a message type on a protocol.
   *
   * The registration is an effect of the activation; pass the result to `ctx.effect` so it is
   * released when the plugin deactivates. The handler may be `async` or plain.
   */
  async on(protocol: string, type: string, handler: MessageHandler): Promise<Handle> {
    const result = (await this.runtime.call("messages.on", {
      activation: this.activation.id,
      protocol,
      type,
    })) as Record<string, unknown>;
    const handle = result.handle as string;
    this.activation.handlers.set(handle, handler);
    return new HandlerHandle(this.runtime, this.activation, handle);
  }

  /** Replaces a declared handler owned by `targetRowId`; `targetRowId:MessageType` must be declared in `overrides`. */
  async override(protocol: string, type: string, targetRowId: string, handler: MessageHandler): Promise<Handle> {
    const result = (await this.runtime.call("messages.override", {
      activation: this.activation.id,
      protocol,
      type,
      target_row_id: targetRowId,
    })) as Record<string, unknown>;
    const handle = result.handle as string;
    this.activation.handlers.set(handle, handler);
    return new HandlerHandle(this.runtime, this.activation, handle);
  }

  /** Installs middleware for a declared message contract; higher priorities run outermost. */
  async use(protocol: string, type: string, middleware: MessageMiddleware, priority = 0): Promise<Handle> {
    if (typeof middleware !== "function") throw new TypeError("message middleware must be a function");
    const result = await this.runtime.call("messages.use", {
      activation: this.activation.id, protocol, type, priority,
    }) as Record<string, unknown>;
    const handle = result.handle as string;
    this.activation.middleware.set(handle, middleware);
    return new MiddlewareHandle(this.runtime, this.activation, handle);
  }

  /** Sends a message of type `type` to a session the host delivered to this activation. */
  async send(protocol: string, session: Session | string, type: string, body: Record<string, unknown>): Promise<void> {
    const sessionId = session instanceof Session ? session.id : session;
    await this.runtime.call("messages.send", {
      activation: this.activation.id,
      protocol,
      session_id: sessionId,
      type,
      body,
    });
  }
}

/** A handle that also forgets its handler locally when disposed. */
class HandlerHandle extends Handle {
  constructor(
    runtime: Runtime,
    private readonly state: ActivationState,
    handle: string,
  ) {
    super(runtime, state.id, handle);
  }

  protected override async disposeOwned(eventInvocation?: string): Promise<void> {
    await super.disposeOwned(eventInvocation);
    this.state.handlers.delete(this.id);
  }
}

/** A handle that also forgets its middleware callback when disposed. */
class MiddlewareHandle extends Handle {
  constructor(runtime: Runtime, private readonly state: ActivationState, handle: string) {
    super(runtime, state.id, handle);
  }

  protected override async disposeOwned(eventInvocation?: string): Promise<void> {
    await super.disposeOwned(eventInvocation);
    this.state.middleware.delete(this.id);
  }
}

/** Something `ctx.effect` accepts: an object with `dispose()` or a callable, sync or async. */
export type EffectLike = { dispose(): unknown } | (() => unknown);

/** Calls declared shared host service methods through the activation's service registry. */
export class Services {
  constructor(private readonly runtime: Runtime, private readonly state: ActivationState) {}

  /** Calls a method by name or full inventory id; args use snake_case names and exact bigint integers. */
  async call(service: string, method: string, args: Record<string, unknown> = {}): Promise<unknown> {
    if (!this.state.live) throw new RpcError(ACTIVATION_NOT_LIVE, "the activation is not live");
    if (this.runtime.protocolVersion < 2) throw new RpcError(METHOD_NOT_FOUND, "services.call requires protocol v2");
    return this.runtime.call("services.call", { activation: this.state.id, service, method, args });
  }

  /** Selects declared ordered providers; own the returned collection with ctx.effect or dispose it early. */
  async getAll(service: string): Promise<ServiceCollection> {
    if (!this.state.live || this.runtime.protocolVersion < 2) throw new Error("service collections require a live protocol v2 activation");
    const result = await this.runtime.call("services.get_all", { activation: this.state.id, service }) as { handle: string; collection: string; count: number };
    return new ServiceCollection(this.runtime, this.state.id, service, result.handle, result.collection, result.count);
  }

  /** Publishes all methods under an explicit registration mode; keys are names or complete schema ids. */
  async provide(service: string, methods: Record<string, ServiceHandler>, mode: "singleton" | "decoratable" | "many" | "decorate" = "singleton", priority = 0): Promise<Handle> {
    if (!this.state.live || this.runtime.protocolVersion < 2) throw new Error("service providers require a live protocol v2 activation");
    const entries = Object.entries(methods);
    if (!entries.length || entries.some(([key, value]) => !key || typeof value !== "function"))
      throw new TypeError("methods must map method names or ids to callbacks");
    if (this.state.providers.size + this.state.eventCallbacks.size >= this.state.maxProviderCallbacks) throw new Error("callbacks exceed max_provider_callbacks");
    const callback = `p${++this.state.nextProvider}`;
    this.state.providers.set(callback, new Map(entries));
    try {
      const result = await this.runtime.call("services.provide", {
        activation: this.state.id, service, mode, priority, callback, methods: entries.map(([key]) => key),
      }) as { handle: string };
      return new Handle(this.runtime, this.state.id, result.handle);
    } catch (error) {
      this.state.providers.delete(callback);
      throw error;
    }
  }

  /** Adds an owned decorator whose callbacks receive an invocation-scoped inner service. */
  decorate(service: string, methods: Record<string, ServiceHandler>, priority = 0): Promise<Handle> {
    return this.provide(service, methods, "decorate", priority);
  }
}

/** An owned ordered snapshot; each index keeps its selected provider identity until disposal. */
export class ServiceCollection extends Handle {
  constructor(runtime: Runtime, private readonly owner: string, private readonly service: string,
    handle: string, private readonly collection: string, readonly count: number) {
    super(runtime, owner, handle);
  }

  /** Calls one member; the host rejects expired collections, foreign tokens and invalid indices. */
  call(index: number, method: string, args: Record<string, unknown> = {}): Promise<unknown> {
    return this.runtime.call("services.call", { activation: this.owner, service: this.service,
      collection: this.collection, index, method, args });
  }
}

/** A service implementation receives the exact method, arguments, cancellation and optional inner service. */
export type ServiceHandler = (call: ServiceCall) => unknown;

/** One admitted provider call; the inner service expires when the callback returns. */
export class ServiceCall {
  live = true;
  readonly method: string;
  readonly args: Record<string, unknown>;

  constructor(private readonly runtime: Runtime, private readonly params: Record<string, unknown>, readonly signal: AbortSignal) {
    this.method = params.method as string;
    this.args = params.args as Record<string, unknown>;
  }

  /** Invokes the selected native inner service, optionally changing the method or argument object. */
  async inner(method = this.method, args = this.args): Promise<unknown> {
    if (!this.live || !this.params.has_inner) throw new Error("this invocation has no live inner service");
    return this.runtime.call("services.inner", {
      activation: this.params.activation, invocation: this.params.invocation, method, args,
    });
  }
}

/** Everything a plugin may use while active. */
export class Context {
  readonly activation: string;
  readonly rowId: string;
  readonly plugin: string;
  readonly config: Record<string, unknown>;
  readonly reload: boolean;
  readonly logger: Logger;
  readonly messages: Messages;
  readonly services: Services;
  readonly events: Events;
  readonly tasks: Tasks;
  readonly resources: Resources;
  /** Cancelled when this activation starts stopping; cleanup and admitted work may still be running. */
  readonly lifetime: AbortSignal;
  /** False with older hosts, whose signal is only cancelled during SDK cleanup or transport loss. */
  readonly supportsLifetimeCancellation: boolean;
  private readonly state: ActivationState;

  constructor(
    runtime: Runtime,
    state: ActivationState,
    rowId: string,
    plugin: string,
    config: Record<string, unknown>,
    reload: boolean,
  ) {
    this.activation = state.id;
    this.rowId = rowId;
    this.plugin = plugin;
    this.config = config;
    this.reload = reload;
    this.logger = new Logger(runtime, state.id);
    this.messages = new Messages(runtime, state);
    this.services = new Services(runtime, state);
    this.events = new Events(runtime, state);
    this.tasks = new Tasks(runtime, state);
    this.resources = new Resources(runtime, state);
    this.lifetime = state.lifetime.signal;
    this.supportsLifetimeCancellation = runtime.supportsLifetimeCancellation;
    this.state = state;
  }

  /**
   * Registers something to release on deactivate, in reverse order, and returns it.
   *
   * Accepts an object with a `dispose()` method (plain or `async`), or a plain/async callable.
   */
  effect<T>(disposable: T): T {
    const candidate = disposable as { dispose?: unknown } | null;
    if (!(typeof candidate?.dispose === "function" || typeof disposable === "function"))
      throw new TypeError("ctx.effect needs an object with dispose() or a callable");
    this.state.effects.push(disposable);
    return disposable;
  }
}

/** The SDK's record of one live activation. */
export class ActivationState {
  readonly lifetime = new AbortController();
  readonly effects: unknown[] = [];
  readonly handlers = new Map<string, MessageHandler>();
  readonly middleware = new Map<string, MessageMiddleware>();
  readonly providers = new Map<string, Map<string, ServiceHandler>>();
  readonly eventCallbacks = new Map<string, EventHandler>();
  readonly taskCallbacks = new Map<string, (call: TaskCall) => unknown>();
  readonly tasks = new Set<TaskHandle>();
  readonly resourceCallbacks = new Map<string, (call: ResourceCall) => unknown>();
  readonly resources = new Set<ResourceHandle>();
  readonly scopes = new Set<ResourceScope>();
  maxProviderCallbacks = 64;
  nextProvider = 0;
  nextTask = 0;
  nextResource = 0;
  context: Context | null = null;
  live = true;

  constructor(
    readonly id: string,
    readonly instance: PluginInstance,
  ) {}

  /** Disposes every effect in reverse order, continuing past failures; returns the failures. */
  async disposeEffects(): Promise<unknown[]> {
    const failures: unknown[] = [];
    while (this.effects.length) {
      const effect = this.effects.pop();
      try {
        const dispose = (effect as { dispose?: () => unknown }).dispose;
        const result = typeof dispose === "function" ? dispose.call(effect) : (effect as () => unknown)();
        await result;
      } catch (error) {
        failures.push(error);
      }
    }
    return failures;
  }
}

function isDeferred(value: unknown): boolean {
  return typeof value === "object" && value !== null && (value as { deferred?: unknown }).deferred === true;
}
