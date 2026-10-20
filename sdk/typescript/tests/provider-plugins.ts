/** Service fixtures for real mounted-loader tests; they are never installed as live persistence. */
import { Context, plugin, run, ServiceCall } from "../src/index.js";

@plugin("conformance.provider", { version: "1.0.0", provides: ["IEquipmentReplyPolicy", "IFighterRepository"], config: { behavior: ["normal", "string"] } })
class Provider {
  async activate(ctx: Context): Promise<void> {
    let stored: Record<string, unknown> | null = null;
    ctx.effect(await ctx.services.provide("IEquipmentReplyPolicy", { get_ReplyAfterInventory: () => false }, "decoratable"));
    ctx.effect(await ctx.services.provide("IFighterRepository", {
      ListByUserNumAsync: async call => {
        if (ctx.config.behavior === "wait" && call.args.user_num === 9007199254740993n) {
          ctx.logger.info("provider-call-started");
          await new Promise<void>((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error("provider cancellation was not received")), 15000);
            const finish = () => { clearTimeout(timer); resolve(); };
            if (call.signal.aborted) finish();
            else call.signal.addEventListener("abort", finish, { once: true });
          });
          ctx.logger.info("provider-call-cancelled");
          return [];
        }
        return stored && stored.user_num === call.args.user_num ? [stored] : [];
      },
      UpsertAsync: call => { stored = call.args.record as Record<string, unknown>; return null; },
    }));
  }
}

@plugin("conformance.decorator", { version: "1.0.0", decorates: ["IEquipmentReplyPolicy"], config: { priority: [0, "int"], operation: ["negate", "string"] } })
class Decorator {
  async activate(ctx: Context): Promise<void> {
    ctx.effect(await ctx.services.decorate("IEquipmentReplyPolicy", {
      get_ReplyAfterInventory: async (call: ServiceCall) => {
        const value = await call.inner();
        return ctx.config.operation === "negate" ? !value : false;
      },
    }, ctx.config.priority as number));
  }
}

@plugin("conformance.provider-many", { version: "1.0.0", providesMany: ["IEquipmentReplyPolicy"], config: { priority: [0, "int"], value: [false, "bool"] } })
class Many {
  async activate(ctx: Context): Promise<void> {
    ctx.effect(await ctx.services.provide("IEquipmentReplyPolicy", { get_ReplyAfterInventory: () => ctx.config.value }, "many", ctx.config.priority as number));
  }
}

@plugin("conformance.provider-consumer", { version: "1.0.0", requires: ["IEquipmentReplyPolicy"] })
class Consumer {
  async activate(ctx: Context): Promise<void> {
    const value = await ctx.services.call("IEquipmentReplyPolicy", "get_ReplyAfterInventory");
    if (value !== false) throw new Error("SDK consumer expected the registered provider's false policy");
    ctx.logger.info("provider-consumer-ok");
  }
}

@plugin("conformance.many-consumer", { version: "1.0.0", requiresMany: ["IEquipmentReplyPolicy"], config: { expected: [[false, true, false], "list"] } })
class ManyConsumer {
  async activate(ctx: Context): Promise<void> {
    const selected = ctx.effect(await ctx.services.getAll("IEquipmentReplyPolicy"));
    const values = [];
    for (let index = 0; index < selected.count; index++) values.push(await selected.call(index, "get_ReplyAfterInventory"));
    if (JSON.stringify(values) !== JSON.stringify(ctx.config.expected)) throw new Error("unexpected ordered services");
    await selected.dispose();
    ctx.logger.info("many-consumer-ok");
  }
}

@plugin("conformance.optional-many-consumer", { version: "1.0.0", optionalMany: ["IEquipmentReplyPolicy"], config: { expected: [[], "list"] } })
class OptionalManyConsumer extends ManyConsumer {}

run();
