"""Adversarial decorator requests exercise host admission without the SDK's local lifetime guard."""

import asyncio
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bff_plugin import Context, ServiceCall, plugin, run
from bff_plugin.errors import INVALID_PARAMS, RpcError

observer = None


@plugin("conformance.scope-observer", version="1.0.0")
class Observer:
    async def activate(self, ctx: Context) -> None:
        global observer
        observer = ctx.activation


@plugin("conformance.lifetime-decorator", version="1.0.0", decorates=["IFighterRepository"],
        config={"behavior": ("scope", str)})
class Decorator:
    async def deactivate(self, ctx: Context) -> None:
        ctx.logger.info("lifetime-decorator-deactivated")

    async def activate(self, ctx: Context) -> None:
        previous = None
        pending = set()

        async def reject(params, label):
            try:
                await ctx.services._runtime.call("services.inner", params)
            except RpcError as error:
                if error.code != INVALID_PARAMS:
                    raise
                ctx.logger.info(label)
            else:
                raise RuntimeError("host admitted a foreign or expired inner-service invocation")

        async def rows(call: ServiceCall):
            nonlocal previous
            if ctx.config["behavior"] == "detach":
                task = asyncio.create_task(call.inner())
                pending.add(task)
                task.add_done_callback(pending.discard)
                # The base Upsert waits until List has entered, so this is an admission barrier,
                # not a delay that assumes the detached request has reached the host.
                await call.inner("UpsertAsync", {"record": {
                    "user_num": 1, "slot": 0, "character_type": 1, "level": 1,
                    "equipped_item_id": 300001, "nickname": "barrier",
                }})
                ctx.logger.info("detached-inner-admitted")
                return []
            params = {"activation": ctx.activation, "invocation": call._params["invocation"],
                      "method": call.method, "args": call.args}
            if previous is not None:
                await reject(previous, "expired-inner-rejected")
            await reject({**params, "activation": observer}, "foreign-inner-rejected")
            previous = params
            first, second = await asyncio.gather(call.inner(), call.inner())
            if first != second:
                raise RuntimeError("concurrent inner calls selected different service state")
            return first

        ctx.effect(await ctx.services.decorate("IFighterRepository", {
            "ListByUserNumAsync": rows, "UpsertAsync": lambda call: call.inner(),
        }))

if __name__ == "__main__":
    run()
