/**
 * Write Bubble Fighter server plugins in TypeScript.
 *
 * A plugin is a class decorated with `@plugin`; `run()` serves it to the host over stdio:
 *
 * ```ts
 * import { Context, plugin, run } from "@bff/plugin";
 *
 * @plugin("example.echo-lobby", {
 *     version: "1.0.0",
 *     requires: ["IMessageRouter"],
 *     handles: ["LoginRequested"],
 *     config: { protocol: "string" },
 * })
 * class EchoLobby {
 *     async activate(ctx: Context): Promise<void> {
 *         const onLogin = async (_session: Session, msg: Record<string, unknown>) => {
 *             ctx.logger.info(`login ${msg.account}`);
 *         };
 *         ctx.effect(await ctx.messages.on(ctx.config.protocol as string, "LoginRequested", onLogin));
 *     }
 * }
 *
 * run();
 * ```
 */

export {
  ACTIVATION_NOT_LIVE,
  INTERNAL_ERROR,
  INVALID_PARAMS,
  METHOD_NOT_FOUND,
  NO_BINDING,
  PLUGIN_ERROR,
  ProtocolError,
  RpcError,
  SESSION_UNKNOWN,
  SERVICE_UNAVAILABLE,
  SERVICE_ERROR,
  TYPE_NOT_ALLOWED,
} from "./errors.js";
export { ActivationState, Context, Handle, Logger, Messages, Resources, ResourceCall, ResourceHandle, ResourceScope, Services, ServiceCall, ServiceCollection, Session, TaskCall, TaskHandle, Tasks } from "./context.js";
export type { EffectLike, MessageHandler, ServiceHandler } from "./context.js";
export { Events, EventCall } from "./events.js";
export type { EventSpec, EventHandler } from "./events.js";
export { plugin, PluginSpec, registeredPlugins } from "./declaration.js";
export type { ConfigField, ConfigType, ConfigTypeName, PluginClass, PluginInstance, PluginOptions } from "./declaration.js";
export {
  DEFAULT_MAX_IN_FLIGHT,
  DEFAULT_MAX_LINE_BYTES,
  PROTOCOL_VERSION,
  Runtime,
  run,
  SDK_NAME,
  SDK_VERSION,
} from "./runtime.js";
