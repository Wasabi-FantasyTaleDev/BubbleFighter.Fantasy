"""Example SDK plugin: greets every login the router delivers and answers with LoginAccepted."""

from __future__ import annotations

import sys
from datetime import datetime, timezone
from pathlib import Path

try:
    import bff_plugin  # noqa: F401
except ModuleNotFoundError:
    # Running from a source checkout without installing the SDK: sdk/python is two levels above this package.
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from bff_plugin import Context, Session, plugin


@plugin(
    "example.echo-lobby",
    version="1.0.0",
    description="Echoes a greeting on login.",
    supported_builds=["kr:812"],
    requires=["IMessageRouter"],
    handles=["LoginRequested"],
    sends=["LoginAccepted"],
    config={"protocol": str, "greeting": ("hello", str)},
)
class EchoLobby:
    """Registers a LoginRequested handler that logs the greeting and replies LoginAccepted."""

    async def activate(self, ctx: Context) -> None:
        opts = ctx.config
        protocol = opts["protocol"]

        async def on_login(session: Session, msg: dict) -> None:
            ctx.logger.info(f"{opts['greeting']} {msg['account']}")
            await ctx.messages.send(
                protocol,
                session,
                "LoginAccepted",
                {
                    "user_number": session.player or 0,
                    "account": msg["account"],
                    "nickname": msg["account"],
                    "server_time": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                    "character_item_id": 300001,
                },
            )

        ctx.effect(await ctx.messages.on(protocol, "LoginRequested", on_login))
