"""Demonstrates declared service calls against a development memory wallet store."""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from bff_plugin import Context, plugin, run


@plugin("example.services-wallet", version="1.0.0", requires=["IPlayerWalletRepository"])
class Wallet:
    """Writes an idempotent snapshot and verifies its exact 64-bit owner id."""

    async def activate(self, ctx: Context) -> None:
        wallet = {"user_num": 9007199254740993, "lucci": 73, "cash": 19}
        for _ in range(2):
            await ctx.services.call("IPlayerWalletRepository", "UpsertAsync", {"wallet": wallet})
        actual = await ctx.services.call("IPlayerWalletRepository", "FindByUserNumAsync", {"user_num": wallet["user_num"]})
        if actual != wallet:
            raise RuntimeError("wallet round trip lost data")


if __name__ == "__main__":
    run()
