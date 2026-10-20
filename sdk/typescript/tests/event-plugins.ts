/** Fixtures for real hosted-loader tests, including native reentry and self-removal. */
import { Context, EventCall, type EventSpec, type Handle, plugin, run } from "../src/index.js";

const type = "EquipmentSelectionApplying";
const subscriptions: EventSpec[] = [{ type, mode: "sync" }, { type, mode: "async" },
  { type, mode: "waterfall", resultType: "Int32" }, { type, mode: "bail", resultType: "String" }];
const publications: EventSpec[] = [{ type, mode: "emit" }, { type, mode: "serial" }, { type, mode: "parallel" }, ...subscriptions.slice(2)];

@plugin(process.env.EVENT_PLUGIN_ID ?? "conformance.events", { version: "1.0.0", requires: ["IMessageRouter", "IEquipmentReplyPolicy"],
  subscribes: subscriptions, publishes: publications, handles: ["LoginRequested"], config: {
    modes: [["sync", "async", "waterfall", "bail"], "list"], priority: [0, "int"],
    behavior: ["normal", "string"], decision: ["", "string"], publisher: [false, "bool"],
  } })
class EventPlugin {
  async activate(ctx: Context): Promise<void> {
    const handles = new Map<string, Handle>();
    const behavior = ctx.config.behavior;
    if (behavior === "resource-release") {
      const scope = await ctx.resources.scope("test-scope");
      ctx.effect(scope);
      const resource = await scope.acquire("test-resource", async () => {
        ctx.logger.info("resource-released");
      });
      ctx.effect(resource);
      ctx.logger.info("resource-acquired");
      return;
    }
    if (behavior === "lifetime-fail") {
      ctx.effect(() => {
        if (!ctx.lifetime.aborted) throw new Error("rollback did not cancel lifetime");
        ctx.logger.info("lifetime-rollback-cancelled");
      });
      throw new Error("lifetime activation failure");
    }
    const listener = async (call: EventCall): Promise<unknown> => {
      if (call.body.user_num !== 9007199254740993n) throw new Error("event identity lost precision");
      ctx.logger.info(`event:${ctx.rowId}:${call.mode}`);
      await ctx.services.call("IEquipmentReplyPolicy", "get_ReplyAfterInventory");
      if (behavior === "lifetime-alive") {
        if (ctx.lifetime.aborted) throw new Error("unloading another row cancelled this activation");
        ctx.logger.info("lifetime-other-alive");
      }
      if (behavior === "lifetime-wait") {
        ctx.logger.info("lifetime-wait-entered");
        await new Promise<void>((resolve, reject) => {
          const timer = setTimeout(() => reject(new Error("activation cancellation was not received")), 15000);
          const finish = () => { clearTimeout(timer); resolve(); };
          if (ctx.lifetime.aborted) finish(); else ctx.lifetime.addEventListener("abort", finish, { once: true });
        });
        ctx.logger.info("lifetime-wait-cancelled");
      }
      if (behavior === "self") {
        await call.dispose(handles.get(call.mode)!);
        return call.mode === "waterfall" ? 77 : null;
      }
      if (behavior === `${call.mode}-error`) throw new Error("event fixture failure");
      if (behavior === "wait" && call.mode === "async") {
        ctx.logger.info("event-call-started");
        await new Promise<void>((resolve, reject) => {
          const timer = setTimeout(() => reject(new Error("event cancellation was not received")), 15000);
          const finish = () => { clearTimeout(timer); resolve(); };
          if (call.signal.aborted) finish(); else call.signal.addEventListener("abort", finish, { once: true });
        });
        ctx.logger.info("event-call-cancelled");
      }
      if (call.mode === "waterfall") {
        if (behavior === "short") return 77;
        if (behavior === "null") return null;
        return (await call.next() as number) + (behavior === "twice" ? await call.next() as number : 1);
      }
      return call.mode === "bail" ? ctx.config.decision || null : null;
    };
    for (const event of subscriptions) {
      if ((ctx.config.modes as string[]).includes(event.mode))
        handles.set(event.mode, ctx.effect(await ctx.events.on(event, listener, ctx.config.priority as number)));
    }
    if (ctx.config.publisher) ctx.effect(await ctx.messages.on("test/game", "LoginRequested", async (_session, message) => {
      const event = publications.find(item => item.mode === message.account)!;
      const result = await ctx.events.publish(event, { protocol: "test/game", user_num: 9007199254740993n, item_id: 13, worn: true },
        event.mode === "waterfall" ? () => 100 : undefined);
      ctx.logger.info(`published:${event.mode}:${result}`);
    }));
  }
  async deactivate(ctx: Context): Promise<void> {
    if (!ctx.lifetime.aborted) throw new Error("deactivation did not cancel lifetime");
    ctx.logger.info(`lifetime-deactivated:${ctx.rowId}`);
  }
}
run();
