/**
 * Plugins the conformance fixtures in tests/fixtures/sdk-protocol are written against.
 *
 * Every SDK implements the same two behaviors; the fixtures' header lines describe them.
 */

import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

import { Context, plugin, run, Session } from "../src/index.js";

@plugin("conformance.echo", {
  version: "1.0.0",
  description: "Echoes a greeting on login.",
  supportedBuilds: ["kr:812"],
  requires: ["IMessageRouter"],
  handles: ["LoginRequested"],
  sends: ["LoginAccepted"],
  config: { protocol: "string", greeting: ["hello", "string"], extra_protocols: [[], "list"] },
})
class Echo {
  /** Registers LoginRequested on the protocol and each extra protocol; replies LoginAccepted. */
  async activate(ctx: Context): Promise<void> {
    const opts = ctx.config;

    const handlerFor =
      (protocol: string) =>
      async (session: Session, msg: Record<string, unknown>): Promise<void> => {
        ctx.logger.info(`${opts.greeting} ${msg.account}`);
        await ctx.messages.send(protocol, session, "LoginAccepted", {
          user_number: 1,
          account: msg.account,
          nickname: msg.account,
          server_time: "2026-01-01T00:00:00Z",
          character_item_id: 300001,
        });
      };

    for (const protocol of [opts.protocol as string, ...(opts.extra_protocols as string[])])
      ctx.effect(await ctx.messages.on(protocol, "LoginRequested", handlerFor(protocol)));
  }
}

@plugin("conformance.failing", {
  version: "1.0.0",
  description: "Registers a handler, then fails activation.",
  requires: ["IMessageRouter"],
  handles: ["LoginRequested"],
  config: { protocol: "string" },
})
class Failing {
  /** Fails after registering, so the SDK has to roll the registration back. */
  async activate(ctx: Context): Promise<void> {
    const never = async (_session: Session, _msg: Record<string, unknown>): Promise<void> => {
      throw new Error("unreachable");
    };
    ctx.effect(await ctx.messages.on(ctx.config.protocol as string, "LoginRequested", never));
    throw new Error("activation refused");
  }
}

@plugin("conformance.middleware", {
  version: "1.0.0",
  description: "Wraps LoginRequested and continues the native pipeline once.",
  supportedBuilds: ["kr:812"],
  requires: ["IMessageRouter"],
  middleware: ["LoginRequested"],
  sends: ["LoginAccepted"],
  config: { protocol: "string", behavior: ["once", "string"] },
})
class Middleware {
  /** Reports the intercepted body through the typed sender, then calls the continuation once. */
  async activate(ctx: Context): Promise<void> {
    const handle = await ctx.messages.use(
      ctx.config.protocol as string,
      "LoginRequested",
      async (session, body, next) => {
        await ctx.messages.send(ctx.config.protocol as string, session, "LoginAccepted", {
          user_number: 1,
          account: `middleware:${body.account as string}`,
          nickname: body.account,
          server_time: "2026-01-01T00:00:00Z",
          character_item_id: 300001,
        });
        await next();
        if (ctx.config.behavior === "twice") {
          try {
            await next();
          } catch {
            // The protocol must reject a second continuation without failing the activation.
          }
        }
      },
      7,
    );
    ctx.effect(handle);
  }
}

// Same check as Python's `if __name__ == "__main__"`: serve only when this file is the entry point.
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) run();
