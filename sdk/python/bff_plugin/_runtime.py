"""The stdio JSON-RPC runtime: serves registered plugins to a host (spec sections 3, 4 and 8)."""

from __future__ import annotations

import asyncio
import inspect
import json
import os
import sys
import threading
import contextvars
import traceback
from typing import Any, BinaryIO

from . import errors
from .context import ActivationState, Context, Session, ServiceCall, TaskCall, ResourceCall
from .events import EventCall
from .declaration import PluginSpec

SDK_NAME = "bff-plugin-python"
SDK_VERSION = "0.1.0"
PROTOCOL_VERSION = 2
DEFAULT_MAX_LINE_BYTES = 1_048_576
DEFAULT_MAX_IN_FLIGHT = 64
# Teardown must reach a plugin whose callback slots are all in flight, so only the lifecycle control
# methods may use these extra slots; the host admits the same reserve on its side of the wire.
CONTROL_RESERVE = 4
# The reader thread blocks once this many unprocessed lines are queued, which stops the host from being read faster than served.
_QUEUE_LINES = 256
_EOF = object()


class _LineTooLong:
    pass


class Runtime:
    """Reads requests from ``stdin``, dispatches them to plugin classes and writes responses to ``stdout``."""

    def __init__(self, specs: list[PluginSpec], stdin_fd: int, stdout: BinaryIO,
                 max_line_bytes: int = DEFAULT_MAX_LINE_BYTES) -> None:
        self._specs = {spec.id: spec for spec in specs}
        self._order = list(specs)
        self._stdin_fd = stdin_fd
        self._stdout = stdout
        self._max_line = max_line_bytes
        self._write_lock = threading.Lock()
        self._next_id = 0
        self._pending: dict[int, asyncio.Future[Any]] = {}
        self._activations: dict[str, ActivationState] = {}
        self._retired_activations: dict[str, ActivationState] = {}
        self._tasks: set[asyncio.Task[None]] = set()
        self._stop = asyncio.Event()
        self._exit_code = 0
        self._max_in_flight = DEFAULT_MAX_IN_FLIGHT
        self._inbound = 0
        self._early_lifetime = False
        self._service_calls: dict[tuple[str, str, str], asyncio.Event] = {}
        self._task_calls: dict[tuple[str, str], asyncio.Event] = {}
        self._resource_calls: dict[tuple[str, str], asyncio.Event] = {}
        self._message_calls: dict[tuple[str, str], asyncio.Event] = {}
        self._resource_invocation: contextvars.ContextVar[str | None] = contextvars.ContextVar(
            "bff_resource_invocation", default=None
        )
        self._loop: asyncio.AbstractEventLoop | None = None

    # ----- output -----------------------------------------------------------------------------------------------

    def _write(self, message: dict[str, Any]) -> None:
        data = json.dumps(message, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        if len(data) > self._max_line:
            raise errors.RpcError(
                errors.INTERNAL_ERROR, f"message of {len(data)} bytes exceeds the {self._max_line}-byte line limit"
            )
        with self._write_lock:
            self._stdout.write(data + b"\n")
            self._stdout.flush()

    def notify(self, method: str, params: dict[str, Any]) -> None:
        """Sends a notification to the host."""
        self._write({"jsonrpc": "2.0", "method": method, "params": params})

    async def call(self, method: str, params: dict[str, Any]) -> Any:
        """Sends a request to the host and returns its result; an error object raises ``RpcError``."""
        if self._stop.is_set():
            raise errors.ProtocolError("the host went away")
        if len(self._pending) >= self._max_in_flight:
            raise errors.RpcError(errors.INTERNAL_ERROR, "host call exceeds max_in_flight")
        self._next_id += 1
        request_id = self._next_id
        future: asyncio.Future[Any] = asyncio.get_running_loop().create_future()
        self._pending[request_id] = future
        try:
            self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
            return await future
        finally:
            self._pending.pop(request_id, None)

    def _respond(self, request_id: Any, result: Any = None, error: errors.RpcError | None = None) -> None:
        if error is not None:
            body: dict[str, Any] = {"code": error.code, "message": error.message}
            if error.data is not None:
                body["data"] = error.data
            self._write({"jsonrpc": "2.0", "id": request_id, "error": body})
        else:
            self._write({"jsonrpc": "2.0", "id": request_id, "result": result})

    # ----- input ------------------------------------------------------------------------------------------------

    def _read_lines(self, loop: asyncio.AbstractEventLoop, queue: "asyncio.Queue[Any]") -> None:
        """Runs on a daemon thread: splits stdin into bounded lines. Uses os.read so exit never waits on a buffer lock."""
        buffer = bytearray()
        try:
            while True:
                chunk = os.read(self._stdin_fd, 65536)
                if not chunk:
                    break
                buffer.extend(chunk)
                while True:
                    newline = buffer.find(b"\n")
                    if newline < 0:
                        if len(buffer) > self._max_line:
                            asyncio.run_coroutine_threadsafe(queue.put(_LineTooLong()), loop).result()
                            return
                        break
                    line = bytes(buffer[:newline])
                    del buffer[: newline + 1]
                    if len(line) > self._max_line:
                        asyncio.run_coroutine_threadsafe(queue.put(_LineTooLong()), loop).result()
                        return
                    asyncio.run_coroutine_threadsafe(queue.put(line), loop).result()
        except (OSError, RuntimeError):
            pass
        finally:
            try:
                asyncio.run_coroutine_threadsafe(queue.put(_EOF), loop).result()
            except RuntimeError:
                pass

    async def serve(self) -> int:
        """Serves until the host sends ``shutdown`` or closes stdin; returns the process exit code."""
        loop = asyncio.get_running_loop()
        self._loop = loop
        queue: asyncio.Queue[Any] = asyncio.Queue(maxsize=_QUEUE_LINES)
        threading.Thread(target=self._read_lines, args=(loop, queue), daemon=True, name="bff-stdin").start()
        while not self._stop.is_set():
            getter = asyncio.ensure_future(queue.get())
            stopper = asyncio.ensure_future(self._stop.wait())
            done, _ = await asyncio.wait({getter, stopper}, return_when=asyncio.FIRST_COMPLETED)
            if getter not in done:
                getter.cancel()
                break
            stopper.cancel()
            item = getter.result()
            if item is _EOF:
                break
            if isinstance(item, _LineTooLong):
                print(f"bff_plugin: a host line exceeded {self._max_line} bytes; exiting", file=sys.stderr)
                self._exit_code = 2
                break
            if not item.strip():
                continue
            try:
                message = json.loads(item)
                if not isinstance(message, dict):
                    raise ValueError("a protocol line must be a JSON object")
            except ValueError as error:
                print(f"bff_plugin: unparseable host line ({error}); exiting", file=sys.stderr)
                self._exit_code = 2
                break
            self._dispatch(message)
        await self._shutdown()
        return self._exit_code

    async def _shutdown(self) -> None:
        self._stop.set()
        for state in self._activations.values():
            state.lifetime.set()
        for signal in self._service_calls.values():
            signal.set()
        for signal in self._task_calls.values():
            signal.set()
        for signal in self._resource_calls.values():
            signal.set()
        for signal in self._message_calls.values():
            signal.set()
        for task in list(self._tasks):
            task.cancel()
        # Cancellation cleanup may await a shielded host call; settle it before waiting for the handler.
        for future in list(self._pending.values()):
            if not future.done():
                future.set_exception(errors.ProtocolError("the host went away"))
        if self._tasks:
            await asyncio.gather(*self._tasks, return_exceptions=True)

    def _dispatch(self, message: dict[str, Any]) -> None:
        method = message.get("method")
        if isinstance(method, str):
            if "id" in message:
                limit = self._max_in_flight + (CONTROL_RESERVE if method in ("deactivate", "shutdown") else 0)
                if self._inbound >= limit:
                    self._respond(message["id"], error=errors.RpcError(errors.INTERNAL_ERROR,
                                  "host request exceeds max_in_flight or max_reentry_depth"))
                    return
                self._inbound += 1
                params = message.get("params") or {}
                if method in ("services.invoke", "events.invoke"):
                    try:
                        key = (method.split(".")[0], _string(params, "activation"), _string(params, "invocation"))
                        if key in self._service_calls:
                            raise errors.RpcError(errors.INVALID_PARAMS, "duplicate callback invocation")
                        self._service_calls[key] = asyncio.Event()
                    except errors.RpcError as error:
                        self._inbound -= 1
                        self._respond(message["id"], error=error)
                        return
                if method == "tasks.invoke":
                    try:
                        key = (_string(params, "activation"), _string(params, "invocation"))
                        if key in self._task_calls:
                            raise errors.RpcError(errors.INVALID_PARAMS, "duplicate task invocation")
                        self._task_calls[key] = asyncio.Event()
                    except errors.RpcError as error:
                        self._inbound -= 1
                        self._respond(message["id"], error=error)
                        return
                if method == "resources.invoke":
                    try:
                        key = (_string(params, "activation"), _string(params, "invocation"))
                        if key in self._resource_calls:
                            raise errors.RpcError(errors.INVALID_PARAMS, "duplicate resource invocation")
                        self._resource_calls[key] = asyncio.Event()
                    except errors.RpcError as error:
                        self._inbound -= 1
                        self._respond(message["id"], error=error)
                        return
                if method == "messages.invoke":
                    try:
                        key = (_string(params, "activation"), _string(params, "invocation"))
                        if key in self._message_calls:
                            raise errors.RpcError(errors.INVALID_PARAMS, "duplicate message middleware invocation")
                        self._message_calls[key] = asyncio.Event()
                    except errors.RpcError as error:
                        self._inbound -= 1
                        self._respond(message["id"], error=error)
                        return
                self._spawn(self._serve_request(message["id"], method, params))
            elif method == "shutdown":
                self._stop.set()
            elif method == "lifecycle.cancel":
                params = message.get("params") or {}
                state = self._activations.get(params.get("activation"))
                if state is not None:
                    state.lifetime.set()
            elif method in ("services.cancel", "events.cancel"):
                params = message.get("params") or {}
                signal = self._service_calls.get((method.split(".")[0], params.get("activation"), params.get("invocation")))
                if signal is not None:
                    signal.set()
            elif method == "tasks.cancel":
                params = message.get("params") or {}
                signal = self._task_calls.get((params.get("activation"), params.get("invocation")))
                if signal is not None:
                    signal.set()
            elif method == "resources.cancel":
                params = message.get("params") or {}
                signal = self._resource_calls.get((params.get("activation"), params.get("invocation")))
                if signal is not None:
                    signal.set()
            elif method == "messages.cancel":
                params = message.get("params") or {}
                signal = self._message_calls.get((params.get("activation"), params.get("invocation")))
                if signal is not None:
                    signal.set()
            return
        request_id = message.get("id")
        future = self._pending.get(request_id) if isinstance(request_id, int) else None
        if future is None or future.done():
            print(f"bff_plugin: response to unknown request {request_id!r} ignored", file=sys.stderr)
            return
        if "error" in message:
            error = message["error"] if isinstance(message["error"], dict) else {}
            future.set_exception(
                errors.RpcError(int(error.get("code", errors.INTERNAL_ERROR)), str(error.get("message", "")), error.get("data"))
            )
        else:
            future.set_result(message.get("result"))

    def _spawn(self, coroutine: Any) -> None:
        task = asyncio.ensure_future(coroutine)
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)

    # ----- host-to-plugin methods -------------------------------------------------------------------------------

    async def _serve_request(self, request_id: Any, method: str, params: dict[str, Any]) -> None:
        try:
            handler = {
                "initialize": self._initialize,
                "describe": self._describe,
                "activate": self._activate,
                "deactivate": self._deactivate,
                "deliver": self._deliver,
                "services.invoke": self._invoke_service,
                "events.invoke": self._invoke_event,
                "tasks.invoke": self._invoke_task,
                "resources.invoke": self._invoke_resource,
                "messages.invoke": self._invoke_message,
            }.get(method)
            if handler is None:
                raise errors.RpcError(errors.METHOD_NOT_FOUND, f"unknown method {method!r}")
            result = await handler(params)
            self._respond(request_id, result)
        except errors.RpcError as error:
            self._respond(request_id, error=error)
        except asyncio.CancelledError:
            raise
        except Exception as error:  # noqa: BLE001 - plugin code failed; the host decides what that means
            self._respond(request_id, error=_plugin_error(error))
        finally:
            if method in ("services.invoke", "events.invoke"):
                self._service_calls.pop((method.split(".")[0], params.get("activation"), params.get("invocation")), None)
            if method == "tasks.invoke":
                self._task_calls.pop((params.get("activation"), params.get("invocation")), None)
            if method == "resources.invoke":
                self._resource_calls.pop((params.get("activation"), params.get("invocation")), None)
            if method == "messages.invoke":
                self._message_calls.pop((params.get("activation"), params.get("invocation")), None)
            self._inbound -= 1

    async def _initialize(self, params: dict[str, Any]) -> dict[str, Any]:
        offered = params.get("protocol_versions", [params.get("protocol_version")])
        selected = next((v for v in (2, 1) if v in offered), None) if isinstance(offered, list) else None
        if selected is None:
            raise errors.RpcError(
                errors.INVALID_PARAMS, f"this SDK speaks protocol version {PROTOCOL_VERSION}, host asked for {params.get('protocol_version')!r}"
            )
        limit = params.get("max_in_flight", DEFAULT_MAX_IN_FLIGHT)
        depth = params.get("max_reentry_depth", 8)
        for name, value, maximum in (("max_in_flight", limit, 4096), ("max_reentry_depth", depth, 64)):
            if type(value) is not int or not 1 <= value <= maximum:
                raise errors.RpcError(errors.INVALID_PARAMS, f"{name} must be an integer from 1 to {maximum}")
        self._max_in_flight = limit
        self.protocol_version = selected
        features = params.get("features", [])
        self._early_lifetime = selected >= 2 and isinstance(features, list) and "lifecycle.cancel" in features
        return {"protocol_version": selected, "sdk": {"name": SDK_NAME, "version": SDK_VERSION}}

    async def _describe(self, params: dict[str, Any]) -> dict[str, Any]:
        return {"definitions": [spec.definition() for spec in self._order]}

    async def _activate(self, params: dict[str, Any]) -> dict[str, Any]:
        activation_id = _string(params, "activation")
        plugin_id = _string(params, "plugin")
        spec = self._specs.get(plugin_id)
        if spec is None:
            raise errors.RpcError(errors.INVALID_PARAMS, f"this process does not serve plugin {plugin_id!r}")
        if activation_id in self._activations:
            raise errors.RpcError(errors.INVALID_PARAMS, f"activation {activation_id!r} is already active")
        config = params.get("config")
        if not isinstance(config, dict):
            raise errors.RpcError(errors.INVALID_PARAMS, "'config' must be an object")
        state = ActivationState(activation_id, spec.cls())
        capacity = params.get("max_provider_callbacks", 64)
        if type(capacity) is not int or not 1 <= capacity <= 2147483647:
            raise errors.RpcError(errors.INVALID_PARAMS, "max_provider_callbacks must be a positive int32")
        state.max_provider_callbacks = capacity
        state.context = Context(self, state, str(params.get("row_id", "")), plugin_id, config, bool(params.get("reload", False)))
        self._activations[activation_id] = state
        try:
            # Readiness follows state installation; cancellation cannot arrive before its signal exists.
            if self._early_lifetime:
                await self.call("lifecycle.ready", {"activation": activation_id})
            if state.lifetime.is_set():
                raise errors.RpcError(errors.ACTIVATION_NOT_LIVE, "the activation is stopping")
            result = state.instance.activate(state.context)
            if inspect.isawaitable(result):
                await result
        except BaseException:
            state.lifetime.set()
            # A failed activation leaves nothing behind: release what it registered, then report the failure.
            failures = await state.dispose_effects()
            for failure in failures:
                print(f"bff_plugin: rollback after failed activate also failed: {failure}", file=sys.stderr)
            self._activations.pop(activation_id, None)
            state.live = False
            raise
        return {}

    async def _deactivate(self, params: dict[str, Any]) -> dict[str, Any]:
        state = self._live(params)
        state.live = False
        state.lifetime.set()
        failures: list[BaseException] = []
        try:
            hook = getattr(state.instance, "deactivate", None)
            if callable(hook):
                result = hook(state.context)
                if inspect.isawaitable(result):
                    await result
        except Exception as error:  # noqa: BLE001 - disposers still have to run
            failures.append(error)
        failures.extend(await state.dispose_effects())
        self._activations.pop(state.id, None)
        if state.resource_callbacks:
            self._retired_activations[state.id] = state
        if failures:
            first = failures[0]
            raise errors.RpcError(
                errors.PLUGIN_ERROR,
                f"{len(failures)} failure(s) while deactivating; first: {type(first).__name__}: {first}",
            )
        return {}

    async def _deliver(self, params: dict[str, Any]) -> dict[str, Any]:
        state = self._live(params)
        handle = _string(params, "handle")
        handler = state.handlers.get(handle)
        if handler is None:
            raise errors.RpcError(errors.INVALID_PARAMS, f"unknown handle {handle!r}")
        session = params.get("session")
        message = params.get("message")
        if not isinstance(session, dict) or not isinstance(message, dict) or not isinstance(message.get("body"), dict):
            raise errors.RpcError(errors.INVALID_PARAMS, "'session' and 'message' {type, body} must be objects")
        result = handler(Session.from_json(session), message["body"])
        if inspect.isawaitable(result):
            await result
        return {"handled": True}

    def _live(self, params: dict[str, Any]) -> ActivationState:
        activation_id = _string(params, "activation")
        state = self._activations.get(activation_id) or self._retired_activations.get(activation_id)
        if state is None or not state.live:
            raise errors.RpcError(errors.ACTIVATION_NOT_LIVE, f"activation {activation_id!r} is not live")
        return state

    async def _invoke_service(self, params: dict[str, Any]) -> Any:
        state = self._live(params)
        methods = state.providers.get(_string(params, "callback"))
        method = _string(params, "method")
        handler = methods.get(method, methods.get(params.get("method_name"))) if methods else None
        if handler is None or not isinstance(params.get("args"), dict):
            raise errors.RpcError(errors.INVALID_PARAMS, "unknown provider method or invalid args")
        call = ServiceCall(self, params, self._service_calls[("services", state.id, _string(params, "invocation"))])
        try:
            result = handler(call)
            return await result if inspect.isawaitable(result) else result
        finally:
            call._live = False

    async def _invoke_event(self, params: dict[str, Any]) -> Any:
        state = self._live(params)
        handler = state.event_callbacks.get(_string(params, "callback"))
        if handler is None or not isinstance(params.get("body"), dict):
            raise errors.RpcError(errors.INVALID_PARAMS, "unknown event callback or invalid body")
        call = EventCall(self, params, self._service_calls[("events", state.id, _string(params, "invocation"))])
        try:
            result = handler(call)
            return await result if inspect.isawaitable(result) else result
        finally:
            call._live = False

    async def _invoke_task(self, params: dict[str, Any]) -> dict[str, Any]:
        state = self._live(params)
        callback = _string(params, "callback")
        handler = state.task_callbacks.get(callback)
        if handler is None or not isinstance(params.get("name"), str):
            raise errors.RpcError(errors.INVALID_PARAMS, "unknown task callback or invalid name")
        signal = self._task_calls[(_string(params, "activation"), _string(params, "invocation"))]
        result = handler(TaskCall(signal))
        if inspect.isawaitable(result):
            await result
        return {}

    async def _invoke_message(self, params: dict[str, Any]) -> dict[str, Any]:
        state = self._live(params)
        callback = _string(params, "handle")
        middleware = state.middleware.get(callback)
        session = params.get("session")
        message = params.get("message")
        if middleware is None or not isinstance(session, dict) or not isinstance(message, dict) or not isinstance(message.get("body"), dict):
            raise errors.RpcError(errors.INVALID_PARAMS, "unknown middleware callback or invalid message")
        invocation = _string(params, "invocation")
        cancellation = self._message_calls[(state.id, invocation)]
        used = False

        async def next_handler() -> None:
            nonlocal used
            if used:
                raise errors.RpcError(errors.INVALID_PARAMS, "message middleware next is single-use")
            used = True
            await self.call("messages.next", {"activation": state.id, "invocation": invocation})

        result = middleware(Session.from_json(session), message["body"], next_handler, cancellation)
        if inspect.isawaitable(result):
            await result
        return {}

    async def _invoke_resource(self, params: dict[str, Any]) -> dict[str, Any]:
        activation_id = _string(params, "activation")
        state = self._activations.get(activation_id) or self._retired_activations.get(activation_id)
        if state is None:
            raise errors.RpcError(errors.ACTIVATION_NOT_LIVE, f"activation {activation_id!r} is not known")
        callback = _string(params, "callback")
        key = (_string(params, "activation"), _string(params, "invocation"))
        cancellation = self._resource_calls.get(key)
        if cancellation is None:
            raise errors.RpcError(errors.INVALID_PARAMS, "resource invocation is not admitted")
        work = state.resource_callbacks.get(callback)
        if work is None:
            raise errors.RpcError(errors.INVALID_PARAMS, "unknown resource callback")
        token = self._resource_invocation.set(key[1])
        try:
            result = work(ResourceCall(cancellation, key[1]))
            if inspect.isawaitable(result):
                await result
            return {}
        finally:
            self._resource_invocation.reset(token)
            state.resource_callbacks.pop(callback, None)
            if not state.resource_callbacks:
                self._retired_activations.pop(state.id, None)

    def current_resource_invocation(self) -> str | None:
        """Returns the release invocation currently running in this task, if any."""
        return self._resource_invocation.get()


def _string(params: dict[str, Any], name: str) -> str:
    value = params.get(name)
    if not isinstance(value, str) or not value:
        raise errors.RpcError(errors.INVALID_PARAMS, f"{name!r} must be a nonempty string")
    return value


def _plugin_error(error: BaseException) -> errors.RpcError:
    trace = "".join(traceback.format_exception(type(error), error, error.__traceback__))
    return errors.RpcError(errors.PLUGIN_ERROR, f"{type(error).__name__}: {error}", {"traceback": trace})


def run(plugins: list[PluginSpec] | None = None) -> None:
    """Serves every ``@plugin`` class over stdio until the host shuts the process down.

    stdout carries the protocol, so ``print`` is redirected to stderr (which the host logs).
    """
    from .declaration import registered_plugins

    specs = plugins if plugins is not None else registered_plugins()
    if not specs:
        raise RuntimeError("no @plugin classes are declared; import the module that declares them before run()")
    protocol_out = sys.stdout.buffer
    sys.stdout = sys.stderr
    runtime = Runtime(specs, sys.stdin.fileno(), protocol_out)
    code = asyncio.run(runtime.serve())
    protocol_out.flush()
    sys.stderr.flush()
    if code:
        sys.exit(code)
