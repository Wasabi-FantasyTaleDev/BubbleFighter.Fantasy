"""Plugins the conformance fixtures in tests/fixtures/sdk-protocol are written against.

Every SDK implements the same two behaviors; the fixtures' header lines describe them.
"""

from __future__ import annotations

import sys
import asyncio
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bff_plugin import Context, Session, plugin, run  # noqa: E402


@plugin(
    "conformance.echo",
    version="1.0.0",
    description="Echoes a greeting on login.",
    supported_builds=["kr:812"],
    requires=["IMessageRouter"],
    handles=["LoginRequested"],
    sends=["LoginAccepted"],
    config={"protocol": str, "greeting": ("hello", str), "extra_protocols": ([], list)},
)
class Echo:
    """Registers LoginRequested on the protocol and each extra protocol; replies LoginAccepted."""

    async def activate(self, ctx: Context) -> None:
        opts = ctx.config

        def handler_for(protocol: str):
            async def on_login(session: Session, msg: dict) -> None:
                ctx.logger.info(f"{opts['greeting']} {msg['account']}")
                await ctx.messages.send(
                    protocol,
                    session,
                    "LoginAccepted",
                    {
                        "user_number": 1,
                        "account": msg["account"],
                        "nickname": msg["account"],
                        "server_time": "2026-01-01T00:00:00Z",
                        "character_item_id": 300001,
                    },
                )

            return on_login

        for protocol in [opts["protocol"], *opts["extra_protocols"]]:
            ctx.effect(await ctx.messages.on(protocol, "LoginRequested", handler_for(protocol)))


@plugin(
    "conformance.failing",
    version="1.0.0",
    description="Registers a handler, then fails activation.",
    requires=["IMessageRouter"],
    handles=["LoginRequested"],
    config={"protocol": str},
)
class Failing:
    """Fails after registering, so the SDK has to roll the registration back."""

    async def activate(self, ctx: Context) -> None:
        async def never(session: Session, msg: dict) -> None:
            raise AssertionError("unreachable")

        ctx.effect(await ctx.messages.on(ctx.config["protocol"], "LoginRequested", never))
        raise RuntimeError("activation refused")


@plugin(
    "conformance.middleware",
    version="1.0.0",
    description="Wraps LoginRequested and continues the native pipeline once.",
    supported_builds=["kr:812"],
    requires=["IMessageRouter"],
    middleware=["LoginRequested"],
    sends=["LoginAccepted"],
    config={"protocol": str, "behavior": ("once", str)},
)
class Middleware:
    """Reports the intercepted body through the typed sender, then calls the continuation once."""

    async def activate(self, ctx: Context) -> None:
        async def around(session: Session, msg: dict[str, Any], next_handler: MessageNext, cancellation: asyncio.Event) -> None:
            if ctx.config["behavior"] == "hang":
                # Never answer or observe cancellation: the host's callback deadline must stop waiting and fault the row.
                await asyncio.Event().wait()
            await ctx.messages.send(
                ctx.config["protocol"],
                session,
                "LoginAccepted",
                {
                    "user_number": 1,
                    "account": f"middleware:{msg['account']}",
                    "nickname": msg["account"],
                    "server_time": "2026-01-01T00:00:00Z",
                    "character_item_id": 300001,
                },
            )
            await next_handler()
            if ctx.config["behavior"] == "twice":
                try:
                    await next_handler()
                except Exception:
                    pass

        ctx.effect(await ctx.messages.use(ctx.config["protocol"], "LoginRequested", around, priority=7))


if __name__ == "__main__":
    run()
