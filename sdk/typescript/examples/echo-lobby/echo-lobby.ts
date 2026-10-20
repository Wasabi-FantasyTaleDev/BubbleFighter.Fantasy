/** Example SDK plugin: greets every login the router delivers and answers with LoginAccepted. */

// Running from a source checkout without installing the SDK: ../../src is the package root.
import { Context, plugin, Session } from "../../src/index.js";

@plugin("example.echo-lobby-ts", {
  version: "1.0.0",
  description: "Echoes a greeting on login.",
  supportedBuilds: ["kr:812"],
  requires: ["IMessageRouter"],
  handles: ["LoginRequested"],
  sends: ["LoginAccepted"],
  config: { protocol: "string", greeting: ["hello", "string"] },
})
class EchoLobby {
  /** Registers a LoginRequested handler that logs the greeting and replies LoginAccepted. */
  async activate(ctx: Context): Promise<void> {
    const opts = ctx.config;
    const protocol = opts.protocol as string;

    const onLogin = async (session: Session, msg: Record<string, unknown>): Promise<void> => {
      ctx.logger.info(`${opts.greeting} ${msg.account}`);
      await ctx.messages.send(protocol, session, "LoginAccepted", {
        user_number: session.player ?? 0,
        account: msg.account,
        nickname: msg.account,
        server_time: new Date().toISOString(),
        character_item_id: 300001,
      });
    };

    ctx.effect(await ctx.messages.on(protocol, "LoginRequested", onLogin));
  }
}
