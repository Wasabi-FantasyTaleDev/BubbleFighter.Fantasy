"""Adversarial event requests test host lifetime guards independently of SDK guards."""
import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from bff_plugin import Context, EventSpec, plugin, run
from bff_plugin.errors import INVALID_PARAMS, RpcError

EVENT = EventSpec("EquipmentSelectionApplying", "waterfall", "Int32")
observer = None


@plugin("conformance.event-observer", version="1.0.0")
class Observer:
    async def activate(self, ctx: Context):
        global observer
        observer = ctx.activation


@plugin("conformance.event-lifetime", version="1.0.0", requires=["IFighterRepository", "IMessageRouter"],
        subscribes=[EVENT], publishes=[EVENT], handles=["LoginRequested"], config={"behavior": ("scope", str)})
class Listener:
    async def deactivate(self, ctx: Context):
        ctx.logger.info("event-lifetime-deactivated")

    async def activate(self, ctx: Context):
        previous = None
        pending = set()

        async def barrier():
            await ctx.services.call("IFighterRepository", "UpsertAsync", {"record": {
                "user_num": 1, "slot": 0, "character_type": 1, "level": 1,
                "equipped_item_id": 300001, "nickname": "barrier",
            }})

        async def reject(params, label):
            try:
                await ctx.events._runtime.call("events.next", params)
            except RpcError as error:
                if error.code != INVALID_PARAMS:
                    raise
                ctx.logger.info(label)
            else:
                raise RuntimeError("host accepted an invalid event continuation")

        async def callback(call):
            nonlocal previous
            if ctx.config["behavior"] == "detach":
                work = asyncio.create_task(call.next())
                pending.add(work)
                work.add_done_callback(pending.discard)
                work.add_done_callback(lambda task: task.exception() if not task.cancelled() else None)
                await barrier()
                ctx.logger.info("detached-event-admitted")
                return 123
            params = {"activation": ctx.activation, "invocation": call._params["invocation"]}
            if previous is not None:
                await reject(previous, "expired-event-rejected")
            await reject({**params, "activation": observer}, "foreign-event-rejected")
            previous = params
            values = await asyncio.gather(call.next(), call.next())
            return sum(values)

        if ctx.config["behavior"] != "publish":
            ctx.effect(await ctx.events.on(EVENT, callback))
        else:
            async def publish(session, message):
                def terminal(call):
                    ctx.logger.info("event-terminal-retained")
                    return 100
                body = {"protocol": "test/game", "user_num": 9007199254740993, "item_id": 13, "worn": True}
                work = asyncio.create_task(ctx.events.publish(EVENT, body, terminal=terminal))
                await barrier()
                work.cancel()
                try:
                    await work
                except asyncio.CancelledError:
                    ctx.logger.info("publication-wait-cancelled")
            ctx.effect(await ctx.messages.on("test/game", "LoginRequested", publish))


if __name__ == "__main__":
    run()
