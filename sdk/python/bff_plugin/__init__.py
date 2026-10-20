"""Write Bubble Fighter server plugins in Python.

A plugin is a class decorated with ``@plugin``; ``run()`` serves it to the host over stdio::

    from bff_plugin import plugin, Context, run

    @plugin("example.echo-lobby", version="1.0.0", requires=["IMessageRouter"],
            handles=["LoginRequested"], config={"protocol": str})
    class EchoLobby:
        async def activate(self, ctx: Context) -> None:
            async def on_login(session, msg):
                ctx.logger.info(f"login {msg['account']}")
            ctx.effect(await ctx.messages.on(ctx.config["protocol"], "LoginRequested", on_login))

    if __name__ == "__main__":
        run()
"""

from ._runtime import SDK_NAME, SDK_VERSION, run
from .context import (Context, Handle, Lifetime, Logger, Messages, Resources, ResourceCall, ResourceHandle,
                      ResourceScope, Services, ServiceCall, ServiceCollection, Session, TaskCall, TaskHandle)
from .declaration import plugin
from .errors import RpcError
from .events import Events, EventCall, EventSpec

__all__ = [
    "Context",
    "Events",
    "EventCall",
    "EventSpec",
    "Handle",
    "Lifetime",
    "Logger",
    "Messages",
    "RpcError",
    "SDK_NAME",
    "SDK_VERSION",
    "Session",
    "TaskCall",
    "TaskHandle",
    "Resources",
    "ResourceCall",
    "ResourceHandle",
    "ResourceScope",
    "Services",
    "ServiceCall",
    "ServiceCollection",
    "plugin",
    "run",
]
