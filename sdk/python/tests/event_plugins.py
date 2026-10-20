"""Event fixtures used only by real hosted-loader tests."""
import asyncio
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from bff_plugin import Context, EventSpec, plugin, run

EVENT = "EquipmentSelectionApplying"
SUBSCRIPTIONS = [EventSpec(EVENT, "sync"), EventSpec(EVENT, "async"),
                 EventSpec(EVENT, "waterfall", "Int32"), EventSpec(EVENT, "bail", "String")]
PUBLICATIONS = [EventSpec(EVENT, mode) for mode in ("emit", "serial", "parallel")] + SUBSCRIPTIONS[2:]


@plugin(os.environ.get("EVENT_PLUGIN_ID", "conformance.events"), version="1.0.0", requires=["IMessageRouter", "IEquipmentReplyPolicy"],
        subscribes=SUBSCRIPTIONS, publishes=PUBLICATIONS, handles=["LoginRequested"], config={
            "modes": (["sync", "async", "waterfall", "bail"], list), "priority": (0, int),
            "behavior": ("normal", str), "decision": ("", str), "publisher": (False, bool),
        })
class EventPlugin:
    async def activate(self, ctx: Context):
        handles = {}
        behavior = ctx.config["behavior"]
        if behavior == "task-wait":
            async def work(call):
                ctx.logger.info("task-entered")
                await call.wait_cancelled()
                ctx.logger.info("task-cancelled")
            ctx.effect(await ctx.tasks.run("test-task", work))
        if behavior == "deliver-hold":
            async def hold(session, message):
                ctx.logger.info("deliver-entered")
                await ctx.lifetime.wait()
                # Stay admitted while teardown sends deactivate, so the control request needs its own
                # slot beyond max_in_flight on both sides of the wire.
                await asyncio.sleep(0.3)
                ctx.logger.info("deliver-cancelled")
            ctx.effect(await ctx.messages.on("test/game", "LoginRequested", hold))
        if behavior == "resource-release":
            scope = await ctx.resources.scope("test-scope")
            ctx.effect(scope)
            async def release(call):
                ctx.logger.info("resource-released")
            resource = await scope.acquire("test-resource", release)
            ctx.effect(resource)
            ctx.logger.info("resource-acquired")
            return
        if behavior == "lifetime-ready-repeat":
            for _ in range(100):
                await ctx.messages._runtime.call("lifecycle.ready", {"activation": ctx.activation})
            ctx.logger.info("lifetime-ready-repeated")
        if behavior == "lifetime-fail":
            def cleanup():
                assert ctx.lifetime.is_set()
                ctx.logger.info("lifetime-rollback-cancelled")
            ctx.effect(cleanup)
            raise ValueError("lifetime activation failure")

        async def listener(call):
            assert call.body["user_num"] == 9007199254740993
            ctx.logger.info(f"event:{ctx.row_id}:{call.mode}")
            await ctx.services.call("IEquipmentReplyPolicy", "get_ReplyAfterInventory")
            if behavior == "lifetime-alive":
                assert not ctx.lifetime.is_set()
                ctx.logger.info("lifetime-other-alive")
            if behavior == "lifetime-wait":
                ctx.logger.info("lifetime-wait-entered")
                await asyncio.wait_for(ctx.lifetime.wait(), 15)
                ctx.logger.info("lifetime-wait-cancelled")
            if behavior == "self":
                await call.dispose(handles[call.mode])
                return 77 if call.mode == "waterfall" else None
            if behavior == f"{call.mode}-error":
                raise ValueError("event fixture failure")
            if behavior == "wait" and call.mode == "async":
                ctx.logger.info("event-call-started")
                await asyncio.wait_for(call.cancellation.wait(), 15)
                ctx.logger.info("event-call-cancelled")
            if call.mode == "waterfall":
                if behavior == "short":
                    return 77
                if behavior == "null":
                    return None
                value = await call.next()
                return value + (await call.next() if behavior == "twice" else 1)
            if call.mode == "bail":
                return ctx.config["decision"] or None
            return None

        for event in SUBSCRIPTIONS:
            if event.mode in ctx.config["modes"]:
                handles[event.mode] = ctx.effect(await ctx.events.on(event, listener, priority=ctx.config["priority"]))

        if ctx.config["publisher"]:
            async def publish(session, message):
                event = next(item for item in PUBLICATIONS if item.mode == message["account"])
                body = {"protocol": "test/game", "user_num": 9007199254740993, "item_id": 13, "worn": True}
                result = await ctx.events.publish(event, body, terminal=(lambda call: 100) if event.mode == "waterfall" else None)
                ctx.logger.info(f"published:{event.mode}:{result}")
            ctx.effect(await ctx.messages.on("test/game", "LoginRequested", publish))

    async def deactivate(self, ctx: Context):
        assert ctx.lifetime.is_set()
        ctx.logger.info(f"lifetime-deactivated:{ctx.row_id}")


if __name__ == "__main__":
    run()
