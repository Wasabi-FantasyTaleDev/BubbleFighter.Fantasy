"""Runtime regressions for cancellation cleanup and bounded outstanding host calls."""

import asyncio
import io
import json
import os
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bff_plugin._runtime import Runtime
from bff_plugin import errors
from bff_plugin.context import ActivationState, Context
from bff_plugin.declaration import PluginSpec


class SignalingOutput(io.BytesIO):
    def __init__(self):
        super().__init__()
        self.written = asyncio.Event()

    def write(self, data):
        result = super().write(data)
        self.written.set()
        return result


class RuntimeTests(unittest.IsolatedAsyncioTestCase):
    async def test_cancel_before_readiness_reply_never_enters_user_hook(self):
        entered = []
        class Plugin:
            async def activate(self, ctx):
                entered.append(ctx)

        spec = PluginSpec("probe", "1.0.0", "", (), (), (), (), (), (), (), {}, Plugin)
        output = SignalingOutput()
        runtime = Runtime([spec], -1, output)
        await runtime._initialize({"protocol_versions": [2], "features": ["lifecycle.cancel"]})
        activation = asyncio.create_task(runtime._activate({"activation": "a", "plugin": "probe", "config": {}}))
        await asyncio.wait_for(output.written.wait(), 2)
        ready = json.loads(output.getvalue().splitlines()[0])
        self.assertEqual("lifecycle.ready", ready["method"])
        state = runtime._activations["a"]
        signal = state.context.lifetime
        runtime._dispatch({"method": "lifecycle.cancel", "params": {"activation": "a"}})
        runtime._dispatch({"id": ready["id"], "result": {}})
        with self.assertRaises(errors.RpcError):
            await activation
        self.assertTrue(signal.is_set())
        self.assertFalse(entered)
        self.assertFalse(runtime._activations)

    async def test_shutdown_signals_each_activation_and_unknown_ids_are_not_retained(self):
        runtime = Runtime([], -1, SignalingOutput())
        states = [ActivationState(name, object()) for name in ("a", "b")]
        for state in states:
            runtime._activations[state.id] = state
            state.context = Context(runtime, state, state.id, "probe", {}, False)
        for name in ("missing", "a", "a"):
            runtime._dispatch({"method": "lifecycle.cancel", "params": {"activation": name}})
        self.assertEqual({"a", "b"}, set(runtime._activations))
        self.assertTrue(states[0].context.lifetime.is_set())
        self.assertFalse(states[1].context.lifetime.is_set())
        waiter = asyncio.create_task(states[1].context.lifetime.wait())
        await runtime._shutdown()
        await asyncio.wait_for(waiter, 2)

    async def test_eof_settles_shielded_host_call_before_handler_cleanup(self):
        read_fd, write_fd = os.pipe()
        output = SignalingOutput()
        runtime = Runtime([], read_fd, output)
        cleaned = asyncio.Event()
        canceled = asyncio.Event()

        async def handler():
            call = asyncio.create_task(runtime.call("services.call", {}))
            try:
                await asyncio.shield(call)
            except asyncio.CancelledError:
                canceled.set()
                with self.assertRaises(errors.ProtocolError):
                    await call
                cleaned.set()

        runtime._spawn(handler())
        serving = asyncio.create_task(runtime.serve())
        try:
            await asyncio.wait_for(output.written.wait(), 2)
            os.close(write_fd)
            write_fd = None
            self.assertEqual(0, await asyncio.wait_for(serving, 2))
            self.assertTrue(canceled.is_set())
            self.assertTrue(cleaned.is_set())
            self.assertFalse(runtime._pending)
            self.assertFalse(runtime._tasks)
        finally:
            if write_fd is not None:
                os.close(write_fd)
            os.close(read_fd)
            if not serving.done():
                serving.cancel()
            await asyncio.gather(serving, return_exceptions=True)

    async def test_outgoing_capacity_rejects_then_recovers_after_response(self):
        output = SignalingOutput()
        runtime = Runtime([], -1, output)
        runtime._max_in_flight = 1
        first = asyncio.create_task(runtime.call("services.call", {}))
        await asyncio.wait_for(output.written.wait(), 2)
        with self.assertRaises(errors.RpcError) as rejected:
            await runtime.call("services.call", {})
        self.assertEqual(errors.INTERNAL_ERROR, rejected.exception.code)
        self.assertEqual(1, len(output.getvalue().splitlines()))
        runtime._dispatch({"id": 1, "result": "first"})
        self.assertEqual("first", await first)
        output.written.clear()
        later = asyncio.create_task(runtime.call("services.call", {}))
        await asyncio.wait_for(output.written.wait(), 2)
        runtime._dispatch({"id": 2, "result": "later"})
        self.assertEqual("later", await later)
        self.assertFalse(runtime._pending)


if __name__ == "__main__":
    unittest.main()
