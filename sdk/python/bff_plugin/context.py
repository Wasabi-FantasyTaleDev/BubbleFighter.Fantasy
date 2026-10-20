"""The activation context handed to a plugin: config, logger, messages and effects."""

from __future__ import annotations

import inspect
import asyncio
from dataclasses import dataclass
from typing import TYPE_CHECKING, Any, Awaitable, Callable, TypeVar

if TYPE_CHECKING:  # pragma: no cover
    from ._runtime import Runtime

T = TypeVar("T")


@dataclass(frozen=True)
class Session:
    """The client session a message arrived on (spec section 6)."""

    id: str
    protocol: str
    player: int | None
    build: str

    @staticmethod
    def from_json(value: dict[str, Any]) -> "Session":
        """Builds a session from the ``session`` object of a ``deliver`` request."""
        return Session(
            id=value["id"],
            protocol=value["protocol"],
            player=value.get("player"),
            build=value["build"],
        )


class Handle:
    """A host-side registration owned by this activation; disposing it releases it early."""

    def __init__(self, runtime: "Runtime", activation: str, handle: str) -> None:
        self._runtime = runtime
        self._activation = activation
        self.id = handle
        self._disposed = False

    async def dispose(self) -> None:
        """Releases the registration. Disposing twice is a no-op."""
        await self._dispose()

    async def _dispose(self, event_invocation: str | None = None) -> None:
        if self._disposed:
            return
        self._disposed = True
        await self._runtime.call("effect.dispose", {"activation": self._activation, "handle": self.id,
            **({"event_invocation": event_invocation} if event_invocation is not None else {})})

    def __repr__(self) -> str:
        return f"Handle({self.id!r})"


class Logger:
    """Sends log notifications to the host, which writes them through the row's logger."""

    def __init__(self, runtime: "Runtime", activation: str) -> None:
        self._runtime = runtime
        self._activation = activation

    def _log(self, level: str, message: str, fields: dict[str, Any]) -> None:
        params: dict[str, Any] = {"activation": self._activation, "level": level, "message": str(message)}
        if fields:
            params["fields"] = fields
        self._runtime.notify("log", params)

    def trace(self, message: str, **fields: Any) -> None:
        """Logs at trace level."""
        self._log("trace", message, fields)

    def debug(self, message: str, **fields: Any) -> None:
        """Logs at debug level."""
        self._log("debug", message, fields)

    def info(self, message: str, **fields: Any) -> None:
        """Logs at information level."""
        self._log("info", message, fields)

    def warn(self, message: str, **fields: Any) -> None:
        """Logs at warning level."""
        self._log("warn", message, fields)

    warning = warn

    def error(self, message: str, **fields: Any) -> None:
        """Logs at error level."""
        self._log("error", message, fields)


MessageHandler = Callable[[Session, dict[str, Any]], Awaitable[None]]
MessageNext = Callable[[], Awaitable[None]]
MessageMiddleware = Callable[[Session, dict[str, Any], MessageNext, asyncio.Event], Any]


class Messages:
    """The typed message router and senders, as the plugin's activation sees them."""

    def __init__(self, runtime: "Runtime", activation: "ActivationState") -> None:
        self._runtime = runtime
        self._activation = activation

    async def on(self, protocol: str, type: str, handler: MessageHandler) -> Handle:
        """Registers ``handler(session, message_body)`` for a message type on a protocol.

        The registration is an effect of the activation; pass the result to ``ctx.effect`` so it is
        released when the plugin deactivates. The handler may be ``async`` or plain.
        """
        result = await self._runtime.call(
            "messages.on", {"activation": self._activation.id, "protocol": protocol, "type": type}
        )
        handle = result["handle"]
        self._activation.handlers[handle] = handler
        return _HandlerHandle(self._runtime, self._activation, handle)

    async def override(self, protocol: str, type: str, target_row_id: str, handler: MessageHandler) -> Handle:
        """Replaces a declared handler owned by ``target_row_id`` for this message type.

        ``target_row_id:type`` must appear in the plugin definition's ``overrides`` list. The returned
        handle has ordinary activation effect ownership and restores the previous handler when released.
        """
        result = await self._runtime.call(
            "messages.override", {"activation": self._activation.id, "protocol": protocol,
                                   "type": type, "target_row_id": target_row_id}
        )
        handle = result["handle"]
        self._activation.handlers[handle] = handler
        return _HandlerHandle(self._runtime, self._activation, handle)

    async def use(self, protocol: str, type: str, middleware: MessageMiddleware, *, priority: int = 0) -> Handle:
        """Installs middleware for a declared message contract.

        The callback receives ``(session, body, next, cancellation)``. ``next`` is single-use and must be
        awaited before the callback returns when downstream handling is required. A higher priority runs outermost.
        """
        if not callable(middleware):
            raise TypeError("message middleware must be callable")
        result = await self._runtime.call(
            "messages.use", {"activation": self._activation.id, "protocol": protocol, "type": type, "priority": priority}
        )
        handle = result["handle"]
        self._activation.middleware[handle] = middleware
        return _MiddlewareHandle(self._runtime, self._activation, handle)

    async def send(self, protocol: str, session: "Session | str", type: str, body: dict[str, Any]) -> None:
        """Sends a message of type ``type`` to a session the host delivered to this activation."""
        session_id = session.id if isinstance(session, Session) else session
        await self._runtime.call(
            "messages.send",
            {
                "activation": self._activation.id,
                "protocol": protocol,
                "session_id": session_id,
                "type": type,
                "body": body,
            },
        )


class _HandlerHandle(Handle):
    """A handle that also forgets its handler locally when disposed."""

    def __init__(self, runtime: "Runtime", activation: "ActivationState", handle: str) -> None:
        super().__init__(runtime, activation.id, handle)
        self._state = activation

    async def _dispose(self, event_invocation: str | None = None) -> None:
        await super()._dispose(event_invocation)
        self._state.handlers.pop(self.id, None)


class _MiddlewareHandle(Handle):
    """A handle that also forgets its middleware callback when disposed."""

    def __init__(self, runtime: "Runtime", activation: "ActivationState", handle: str) -> None:
        super().__init__(runtime, activation.id, handle)
        self._state = activation

    async def _dispose(self, event_invocation: str | None = None) -> None:
        await super()._dispose(event_invocation)
        self._state.middleware.pop(self.id, None)


class Services:
    """Calls declared host services using the shared contract inventory's names and argument fields."""

    def __init__(self, runtime: "Runtime", state: "ActivationState") -> None:
        self._runtime = runtime
        self._state = state

    async def call(self, service: str, method: str, args: dict[str, Any] | None = None) -> Any:
        """Calls a method (or its full inventory id); integers retain Python's exact precision."""
        if not self._state.live:
            raise RuntimeError("the activation is not live")
        if getattr(self._runtime, "protocol_version", 1) < 2:
            raise RuntimeError("services.call requires protocol v2")
        return await self._runtime.call("services.call", {
            "activation": self._state.id, "service": service, "method": method, "args": args if args is not None else {},
        })

    async def get_all(self, service: str) -> "ServiceCollection":
        """Selects declared ordered providers; own the returned collection with ctx.effect or dispose it early."""
        if not self._state.live or self._runtime.protocol_version < 2:
            raise RuntimeError("service collections require a live protocol v2 activation")
        result = await self._runtime.call("services.get_all", {"activation": self._state.id, "service": service})
        return ServiceCollection(self._runtime, self._state.id, service, result)

    async def provide(self, service: str, methods: dict[str, Callable[["ServiceCall"], Any]],
                      *, mode: str = "singleton", priority: int = 0) -> Handle:
        """Publishes all interface methods as a singleton, decoratable singleton, or many registration.

        Keys are unambiguous method names or complete schema ids. Callbacks receive a ServiceCall.
        Callback targets remain available to retained native projections until activation teardown.
        """
        if not self._state.live or self._runtime.protocol_version < 2:
            raise RuntimeError("service providers require a live protocol v2 activation")
        if not methods or not all(isinstance(key, str) and callable(value) for key, value in methods.items()):
            raise TypeError("methods must map method names or ids to callbacks")
        if len(self._state.providers) + len(self._state.event_callbacks) >= self._state.max_provider_callbacks:
            raise RuntimeError("provider callbacks exceed max_provider_callbacks")
        self._state.next_provider += 1
        callback = f"p{self._state.next_provider}"
        self._state.providers[callback] = dict(methods)
        try:
            result = await self._runtime.call("services.provide", {
                "activation": self._state.id, "service": service, "mode": mode,
                "priority": priority, "callback": callback, "methods": list(methods),
            })
            return Handle(self._runtime, self._state.id, result["handle"])
        except BaseException:
            self._state.providers.pop(callback, None)
            raise

    async def decorate(self, service: str, methods: dict[str, Callable[["ServiceCall"], Any]], *, priority: int = 0) -> Handle:
        """Adds an owned layer; each callback may invoke its scoped inner service through ServiceCall.inner."""
        return await self.provide(service, methods, mode="decorate", priority=priority)


class ServiceCollection(Handle):
    """An owned, ordered service snapshot. Indices keep their provider identity until this handle is disposed."""

    def __init__(self, runtime: "Runtime", activation: str, service: str, result: dict[str, Any]) -> None:
        super().__init__(runtime, activation, result["handle"])
        self.count: int = result["count"]
        self._collection = result["collection"]
        self._service = service

    async def call(self, index: int, method: str, args: dict[str, Any] | None = None) -> Any:
        """Calls the selected member; the host rejects expired collections and invalid indices."""
        return await self._runtime.call("services.call", {
            "activation": self._activation, "service": self._service, "collection": self._collection,
            "index": index, "method": method, "args": args if args is not None else {},
        })


class ServiceCall:
    """One provider callback, with its arguments, cooperative cancellation and scoped inner service."""

    def __init__(self, runtime: "Runtime", params: dict[str, Any], cancellation: asyncio.Event) -> None:
        self.method: str = params["method"]
        self.args: dict[str, Any] = params["args"]
        self.cancellation = cancellation
        self._runtime = runtime
        self._params = params
        self._live = True

    async def inner(self, method: str | None = None, args: dict[str, Any] | None = None) -> Any:
        """Calls the native inner service while this decorator invocation remains active."""
        if not self._live or not self._params.get("has_inner"):
            raise RuntimeError("this invocation has no live inner service")
        return await self._runtime.call("services.inner", {
            "activation": self._params["activation"], "invocation": self._params["invocation"],
            "method": method if method is not None else self.method, "args": args if args is not None else self.args,
        })


class Lifetime:
    """Read-only activation cancellation; a signal requests stopping, not completed cleanup."""

    def __init__(self, signal: asyncio.Event) -> None:
        self._signal = signal

    def is_set(self) -> bool:
        """Reports whether this activation has started stopping."""
        return self._signal.is_set()

    async def wait(self) -> None:
        """Waits for cancellation without cancelling the calling task."""
        await self._signal.wait()


class TaskCall:
    """One host-owned background task callback with cooperative cancellation."""

    def __init__(self, cancellation: asyncio.Event) -> None:
        self.cancellation = cancellation

    def is_cancelled(self) -> bool:
        """Reports whether the host has requested this task to stop."""
        return self.cancellation.is_set()

    async def wait_cancelled(self) -> None:
        """Waits for the host cancellation request."""
        await self.cancellation.wait()


class TaskHandle:
    """A native-owned background task; waiting observes work, while disposal cancels and joins it."""

    def __init__(self, runtime: Any, state: "ActivationState", task: str, callback: str) -> None:
        self._runtime = runtime
        self._state = state
        self.id = task
        self._callback = callback
        self._disposed = False

    async def wait(self) -> dict[str, Any]:
        """Waits for the callback's actual work result without dropping its native owner."""
        return await self._runtime.call("tasks.wait", {"activation": self._state.id, "task": self.id})

    async def dispose(self) -> None:
        """Requests cancellation and joins native cleanup; repeated disposal is idempotent."""
        if self._disposed:
            return
        await self._runtime.call("tasks.dispose", {"activation": self._state.id, "task": self.id})
        self._disposed = True
        self._state.tasks.discard(self)
        self._state.task_callbacks.pop(self._callback, None)


class ResourceCall:
    """One host-owned resource release callback with cooperative cancellation."""

    def __init__(self, cancellation: asyncio.Event, invocation: str) -> None:
        self.cancellation = cancellation
        self.invocation = invocation

    def is_cancelled(self) -> bool:
        """Reports whether the host has requested the release callback to stop."""
        return self.cancellation.is_set()

    async def wait_cancelled(self) -> None:
        """Waits for the host cancellation request."""
        await self.cancellation.wait()


class ResourceHandle:
    """A native-owned resource; disposal runs its registered release callback."""

    def __init__(self, runtime: Any, state: "ActivationState", resource: str, callback: str) -> None:
        self._runtime = runtime
        self._state = state
        self.id = resource
        self._callback = callback
        self._disposed = False

    async def dispose(self) -> None:
        """Releases the resource early; repeated disposal is idempotent."""
        if self._disposed:
            return
        params = {"activation": self._state.id, "resource": self.id}
        invocation = self._runtime.current_resource_invocation()
        if invocation is not None:
            params["release_invocation"] = invocation
        result = await self._runtime.call("resources.dispose", params)
        self._disposed = not (isinstance(result, dict) and result.get("deferred"))
        if self._disposed:
            self._state.resource_callbacks.pop(self._callback, None)
            self._state.resources.discard(self)


class ResourceScope:
    """A child resource lifetime that shares its activation's bounded capacity."""

    def __init__(self, runtime: Any, state: "ActivationState", scope: str) -> None:
        self._runtime = runtime
        self._state = state
        self.id = scope
        self._disposed = False

    async def create(self, name: str) -> "ResourceScope":
        """Creates a child scope owned by this scope."""
        result = await self._runtime.call("resources.scope", {
            "activation": self._state.id, "parent": self.id, "name": name,
        })
        child = ResourceScope(self._runtime, self._state, result["scope"])
        self._state.scopes.add(child)
        return child

    async def acquire(self, name: str, release: Callable[[ResourceCall], Any]) -> ResourceHandle:
        """Acquires a resource whose release callback is owned by the native scope."""
        if self._disposed:
            raise RuntimeError("resource scope is disposed")
        if not isinstance(name, str) or not name:
            raise ValueError("resource name must be a nonempty string")
        if not callable(release):
            raise TypeError("resource release must be callable")
        self._state.next_resource += 1
        callback = f"r{self._state.next_resource}"
        self._state.resource_callbacks[callback] = release
        try:
            result = await self._runtime.call("resources.acquire", {
                "activation": self._state.id, "scope": self.id, "name": name, "callback": callback,
            })
            handle = ResourceHandle(self._runtime, self._state, result["resource"], callback)
            self._state.resources.add(handle)
            return handle
        except BaseException:
            self._state.resource_callbacks.pop(callback, None)
            raise

    async def dispose(self) -> None:
        """Disposes this scope and all native-owned descendants."""
        if self._disposed:
            return
        await self._runtime.call("resources.scope.dispose", {"activation": self._state.id, "scope": self.id})
        self._disposed = True
        self._state.scopes.discard(self)


class Resources:
    """Creates native-owned resource scopes for the activation."""

    def __init__(self, runtime: Any, state: "ActivationState") -> None:
        self._runtime = runtime
        self._state = state

    async def scope(self, name: str) -> ResourceScope:
        """Creates a root scope owned by the activation."""
        if not self._state.live or self._runtime.protocol_version < 2:
            raise RuntimeError("resource scopes require a live protocol v2 activation")
        if not isinstance(name, str) or not name:
            raise ValueError("resource scope name must be a nonempty string")
        result = await self._runtime.call("resources.scope", {"activation": self._state.id, "name": name})
        scope = ResourceScope(self._runtime, self._state, result["scope"])
        self._state.scopes.add(scope)
        return scope


class Context:
    """Everything a plugin may use while active."""

    def __init__(self, runtime: "Runtime", state: "ActivationState", row_id: str, plugin: str,
                 config: dict[str, Any], reload: bool) -> None:
        self.activation = state.id
        self.row_id = row_id
        self.plugin = plugin
        self.config = config
        self.reload = reload
        self.logger = Logger(runtime, state.id)
        self.messages = Messages(runtime, state)
        self.services = Services(runtime, state)
        from .events import Events
        self.events = Events(runtime, state)
        self.tasks = Tasks(runtime, state)
        self.resources = Resources(runtime, state)
        self.lifetime = Lifetime(state.lifetime)
        self.supports_lifetime_cancellation = runtime._early_lifetime
        self._state = state

    def effect(self, disposable: T) -> T:
        """Registers something to release on deactivate, in reverse order, and returns it.

        Accepts an object with a ``dispose()`` method (plain or ``async``), or a plain/async callable.
        """
        if not (callable(getattr(disposable, "dispose", None)) or callable(disposable)):
            raise TypeError("ctx.effect needs an object with dispose() or a callable")
        self._state.effects.append(disposable)
        return disposable


class ActivationState:
    """The SDK's record of one live activation."""

    def __init__(self, activation_id: str, instance: Any) -> None:
        self.id = activation_id
        self.instance = instance
        self.effects: list[Any] = []
        self.handlers: dict[str, MessageHandler] = {}
        self.middleware: dict[str, MessageMiddleware] = {}
        self.context: Context | None = None
        self.live = True
        self.lifetime = asyncio.Event()
        self.providers: dict[str, dict[str, Callable[[ServiceCall], Any]]] = {}
        self.event_callbacks: dict[str, Callable[..., Any]] = {}
        self.task_callbacks: dict[str, Callable[[TaskCall], Any]] = {}
        self.tasks: set[TaskHandle] = set()
        self.resources: set[ResourceHandle] = set()
        self.scopes: set[ResourceScope] = set()
        self.resource_callbacks: dict[str, Callable[[ResourceCall], Any]] = {}
        self.next_resource = 0
        self.max_provider_callbacks = 64
        self.next_provider = 0

    async def dispose_effects(self) -> list[BaseException]:
        """Disposes every effect in reverse order, continuing past failures; returns the failures."""
        failures: list[BaseException] = []
        while self.effects:
            effect = self.effects.pop()
            try:
                dispose = getattr(effect, "dispose", None)
                result = dispose() if callable(dispose) else effect()
                if inspect.isawaitable(result):
                    await result
            except Exception as error:  # noqa: BLE001 - every disposer must run
                failures.append(error)
        return failures


class Tasks:
    """Starts background work under the host's native task owner and shared effect budget."""

    def __init__(self, runtime: Any, state: ActivationState) -> None:
        self._runtime = runtime
        self._state = state

    async def run(self, name: str, work: Callable[[TaskCall], Any]) -> TaskHandle:
        """Starts ``work`` and returns a handle; pass that handle to ``ctx.effect`` for teardown ownership."""
        if not self._state.live or self._runtime.protocol_version < 2:
            raise RuntimeError("tasks require a live protocol v2 activation")
        if not isinstance(name, str) or not name:
            raise ValueError("task name must be a nonempty string")
        if not callable(work):
            raise TypeError("task work must be callable")
        self._state.next_provider += 1
        callback = f"t{self._state.next_provider}"
        self._state.task_callbacks[callback] = work
        try:
            result = await self._runtime.call("tasks.start", {
                "activation": self._state.id, "name": name, "callback": callback,
            })
            handle = TaskHandle(self._runtime, self._state, result["task"], callback)
            self._state.tasks.add(handle)
            return handle
        except BaseException:
            self._state.task_callbacks.pop(callback, None)
            raise
