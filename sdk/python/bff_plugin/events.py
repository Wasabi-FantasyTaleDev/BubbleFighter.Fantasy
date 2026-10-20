"""Adapters for the host's owned event bus; dispatch order and policy remain native."""

from __future__ import annotations

import asyncio
from dataclasses import dataclass
from typing import Any, Callable, TYPE_CHECKING

if TYPE_CHECKING:
    from ._runtime import Runtime
    from .context import ActivationState, Handle


@dataclass(frozen=True)
class EventSpec:
    """Declares an event type, native mode and (for waterfall/bail) an exact result type.

    Subscribe modes are sync, async, waterfall and bail. Publish modes are emit,
    serial, parallel, waterfall and bail. Type identities come from bff contracts schema.
    """

    type: str
    mode: str
    result_type: str | None = None

    def definition(self) -> dict[str, Any]:
        """Returns the protocol declaration; the host validates the shared type identity."""
        return {"type": self.type, "mode": self.mode,
                **({"result_type": self.result_type} if self.result_type is not None else {})}


class EventCall:
    """One callback with cooperative cancellation and invocation-scoped continuation/disposal."""

    def __init__(self, runtime: Runtime, params: dict[str, Any], cancellation: asyncio.Event) -> None:
        self.body: dict[str, Any] = params["body"]
        self.type: str = params["type"]
        self.mode: str = params["mode"]
        self.cancellation = cancellation
        self._runtime = runtime
        self._params = params
        self._live = True

    async def next(self) -> Any:
        """Invokes the selected downstream pipeline; may be called again while this callback lives."""
        if not self._live or not self._params.get("has_next"):
            raise RuntimeError("this event invocation has no live continuation")
        return await self._runtime.call("events.next", {
            "activation": self._params["activation"], "invocation": self._params["invocation"],
        })

    async def dispose(self, handle: Handle) -> None:
        """Disposes an owned registration in this native callback context, including itself.

        Use this instead of handle.dispose() inside a listener that removes itself;
        an unrelated disposal must still wait for that listener's unfinished work.
        """
        if not self._live or handle._runtime is not self._runtime or handle._activation != self._params["activation"]:
            raise RuntimeError("the event invocation or handle does not belong to this live activation")
        await handle._dispose(self._params["invocation"])


class Events:
    """Registers and publishes through the same event bus used by native plugins."""

    def __init__(self, runtime: Runtime, state: ActivationState) -> None:
        self._runtime = runtime
        self._state = state

    def _reserve(self, handler: Callable[[EventCall], Any]) -> str:
        if not self._state.live or self._runtime.protocol_version < 2:
            raise RuntimeError("events require a live protocol v2 activation")
        if not callable(handler):
            raise TypeError("an event callback must be callable")
        if len(self._state.providers) + len(self._state.event_callbacks) >= self._state.max_provider_callbacks:
            raise RuntimeError("callbacks exceed max_provider_callbacks")
        self._state.next_provider += 1
        callback = f"e{self._state.next_provider}"
        self._state.event_callbacks[callback] = handler
        return callback

    async def on(self, event: EventSpec, handler: Callable[[EventCall], Any], *, priority: int = 0) -> Handle:
        """Registers an owned listener; higher priority runs first and equal priority preserves order."""
        from .context import Handle
        callback = self._reserve(handler)

        async def register() -> Handle:
            try:
                result = await self._runtime.call("events.on", {
                    **event.definition(), "activation": self._state.id, "callback": callback, "priority": priority,
                })
                return Handle(self._runtime, self._state.id, result["handle"])
            except BaseException:
                self._state.event_callbacks.pop(callback, None)
                raise

        # Cancellation of the caller's await does not undo a host registration. Keep its
        # callback alive until actual failure or row teardown, even if no handle was received.
        work = asyncio.create_task(register())
        work.add_done_callback(lambda task: task.exception() if not task.cancelled() else None)
        return await asyncio.shield(work)

    async def publish(self, event: EventSpec, body: dict[str, Any], *,
                      terminal: Callable[[EventCall], Any] | None = None) -> Any:
        """Publishes in an explicit native mode; waterfall requires a terminal returning the result.

        Cancelling this Python await does not discard a terminal still reachable by
        native dispatch. It remains bounded and retained until the actual response.
        """
        if not self._state.live or self._runtime.protocol_version < 2:
            raise RuntimeError("events require a live protocol v2 activation")
        if (event.mode == "waterfall") != (terminal is not None):
            raise ValueError("only waterfall publication requires a terminal callback")
        callback = self._reserve(terminal) if terminal is not None else None

        async def dispatch() -> Any:
            try:
                return await self._runtime.call("events.publish", {
                    **event.definition(), "activation": self._state.id, "body": body,
                    **({"terminal": callback} if callback is not None else {}),
                })
            finally:
                if callback is not None:
                    self._state.event_callbacks.pop(callback, None)

        work = asyncio.create_task(dispatch())
        work.add_done_callback(lambda task: task.exception() if not task.cancelled() else None)
        return await asyncio.shield(work)
