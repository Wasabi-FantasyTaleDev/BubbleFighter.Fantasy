/** Adapters for the native owned event bus; no SDK-side dispatcher reorders listeners. */
import type { Runtime } from "./runtime.js";
import { Handle, type ActivationState } from "./context.js";

/** An exact shared event/result identity and its native subscription or publication mode. */
export interface EventSpec {
  type: string;
  mode: "sync" | "async" | "emit" | "serial" | "parallel" | "waterfall" | "bail";
  /** Required for waterfall/bail; use a full schema name when short names are ambiguous. */
  resultType?: string;
}

/** Converts declaration spelling to the wire format shared by every execution loader. */
export function eventDefinition(event: EventSpec): Record<string, unknown> {
  return { type: event.type, mode: event.mode, ...(event.resultType ? { result_type: event.resultType } : {}) };
}

/** A listener or terminal; notification modes return void, result modes return the declared value. */
export type EventHandler = (call: EventCall) => unknown;

/** One admitted callback; continuation and self-disposal expire when the callback returns. */
export class EventCall {
  live = true;
  readonly body: Record<string, unknown>;
  readonly type: string;
  readonly mode: string;

  constructor(private readonly runtime: Runtime, private readonly params: Record<string, unknown>, readonly signal: AbortSignal) {
    this.body = params.body as Record<string, unknown>;
    this.type = params.type as string;
    this.mode = params.mode as string;
  }

  /** Calls the selected downstream pipeline; native semantics allow repeated calls while live. */
  async next(): Promise<unknown> {
    if (!this.live || !this.params.has_next) throw new Error("this event invocation has no live continuation");
    return this.runtime.call("events.next", { activation: this.params.activation, invocation: this.params.invocation });
  }

  /** Removes a registration in the current callback context, so self-removal never waits on itself. */
  async dispose(handle: Handle): Promise<void> {
    if (!this.live) throw new Error("the event invocation has finished");
    await handle.disposeFromEvent(this.runtime, this.params.activation as string, this.params.invocation as string);
  }
}

/** Access to the same event priorities, policies and ownership used by native plugins. */
export class Events {
  constructor(private readonly runtime: Runtime, private readonly state: ActivationState) {}

  private checkLive(): void {
    if (!this.state.live || this.runtime.protocolVersion < 2) throw new Error("events require a live protocol v2 activation");
  }

  private reserve(handler: EventHandler): string {
    this.checkLive();
    if (typeof handler !== "function") throw new TypeError("an event callback must be callable");
    if (this.state.providers.size + this.state.eventCallbacks.size >= this.state.maxProviderCallbacks)
      throw new Error("callbacks exceed max_provider_callbacks");
    const callback = `e${++this.state.nextProvider}`;
    this.state.eventCallbacks.set(callback, handler);
    return callback;
  }

  /** Registers an owned listener; higher priority runs first, equal priorities keep registration order. */
  async on(event: EventSpec, handler: EventHandler, priority = 0): Promise<Handle> {
    const callback = this.reserve(handler);
    try {
      const result = await this.runtime.call("events.on", {
        ...eventDefinition(event), activation: this.state.id, callback, priority,
      }) as { handle: string };
      return new Handle(this.runtime, this.state.id, result.handle);
    } catch (error) {
      this.state.eventCallbacks.delete(callback);
      throw error;
    }
  }

  /** Publishes in an explicit native mode. Waterfall retains its terminal until actual dispatch completion. */
  async publish(event: EventSpec, body: Record<string, unknown>, terminal?: EventHandler): Promise<unknown> {
    this.checkLive();
    if ((event.mode === "waterfall") !== (terminal !== undefined))
      throw new Error("only waterfall publication requires a terminal callback");
    const callback = terminal === undefined ? undefined : this.reserve(terminal);
    try {
      return await this.runtime.call("events.publish", {
        ...eventDefinition(event), activation: this.state.id, body, ...(callback ? { terminal: callback } : {}),
      });
    } finally {
      if (callback !== undefined) this.state.eventCallbacks.delete(callback);
    }
  }
}
