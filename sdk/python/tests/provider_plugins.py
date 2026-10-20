"""Service providers used by the real mounted-loader tests; no live persistence is configured."""

import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bff_plugin import Context, ServiceCall, plugin, run


@plugin("conformance.provider", version="1.0.0", provides=["IEquipmentReplyPolicy", "IFighterRepository"],
        config={"behavior": ("normal", str)})
class Provider:
    async def activate(self, ctx: Context) -> None:
        stored = None

        def upsert(call: ServiceCall):
            nonlocal stored
            stored = call.args["record"]

        async def rows(call: ServiceCall):
            if ctx.config["behavior"] == "wait" and call.args["user_num"] == 9007199254740993:
                ctx.logger.info("provider-call-started")
                await asyncio.wait_for(call.cancellation.wait(), 15)
                ctx.logger.info("provider-call-cancelled")
                return []
            if ctx.config["behavior"] == "null":
                return None
            return [stored] if stored and stored["user_num"] == call.args["user_num"] else []

        if ctx.config["behavior"] == "undeclared":
            await ctx.services.provide("IOnlinePlayers", {"FindByUserNum": lambda call: None})
        if ctx.config["behavior"] == "wrong-mode":
            await ctx.services.provide("IEquipmentReplyPolicy", {"get_ReplyAfterInventory": lambda call: False}, mode="many")
        if ctx.config["behavior"] == "incomplete":
            await ctx.services.provide("IFighterRepository", {"UpsertAsync": upsert})
        async def policy(call: ServiceCall):
            if ctx.config["behavior"] == "self-unregister":
                await policy_handle.dispose()
            return False

        policy_handle = ctx.effect(await ctx.services.provide("IEquipmentReplyPolicy", {"get_ReplyAfterInventory": policy}, mode="decoratable"))
        ctx.effect(await ctx.services.provide("IFighterRepository", {"ListByUserNumAsync": rows, "UpsertAsync": upsert}))
        if ctx.config["behavior"] == "rollback":
            raise RuntimeError("provider activation rollback")


@plugin("conformance.decorator", version="1.0.0", decorates=["IEquipmentReplyPolicy"],
        config={"priority": (0, int), "operation": ("negate", str)})
class Decorator:
    async def activate(self, ctx: Context) -> None:
        async def policy(call: ServiceCall):
            value = await call.inner()
            return not value if ctx.config["operation"] == "negate" else False

        ctx.effect(await ctx.services.decorate("IEquipmentReplyPolicy", {"get_ReplyAfterInventory": policy}, priority=ctx.config["priority"]))


@plugin("conformance.provider-many", version="1.0.0", provides_many=["IEquipmentReplyPolicy"],
        config={"priority": (0, int), "value": (False, bool)})
class Many:
    async def activate(self, ctx: Context) -> None:
        ctx.effect(await ctx.services.provide("IEquipmentReplyPolicy", {"get_ReplyAfterInventory": lambda call: ctx.config["value"]},
                                              mode="many", priority=ctx.config["priority"]))


@plugin("conformance.provider-consumer", version="1.0.0", requires=["IEquipmentReplyPolicy"])
class Consumer:
    async def activate(self, ctx: Context) -> None:
        value = await ctx.services.call("IEquipmentReplyPolicy", "get_ReplyAfterInventory")
        if value is not False:
            raise ValueError("SDK consumer expected the registered provider's false policy")
        ctx.logger.info("provider-consumer-ok")


@plugin("conformance.many-consumer", version="1.0.0", requires_many=["IEquipmentReplyPolicy"],
        config={"expected": ([False, True, False], list)})
class ManyConsumer:
    async def activate(self, ctx: Context) -> None:
        selected = ctx.effect(await ctx.services.get_all("IEquipmentReplyPolicy"))
        values = [await selected.call(index, "get_ReplyAfterInventory") for index in range(selected.count)]
        if values != ctx.config["expected"]:
            raise ValueError(f"unexpected ordered services: {values}")
        await selected.dispose()
        ctx.logger.info("many-consumer-ok")


@plugin("conformance.optional-many-consumer", version="1.0.0", optional_many=["IEquipmentReplyPolicy"],
        config={"expected": ([], list)})
class OptionalManyConsumer(ManyConsumer):
    pass


if __name__ == "__main__":
    run()
