/** Exercises declared host service calls against a development memory wallet store. */
import { Context, plugin, run } from "../src/index.js";

@plugin("example.services-wallet", { version: "1.0.0", requires: ["IPlayerWalletRepository"] })
class Wallet {
  async activate(ctx: Context): Promise<void> {
    const wallet = { user_num: 9007199254740993n, lucci: 73, cash: 19 };
    for (let i = 0; i < 2; i++)
      await ctx.services.call("IPlayerWalletRepository", "UpsertAsync", { wallet });
    const actual = await ctx.services.call("IPlayerWalletRepository", "FindByUserNumAsync", { user_num: wallet.user_num }) as typeof wallet;
    if (actual?.user_num !== wallet.user_num || actual.lucci !== wallet.lucci || actual.cash !== wallet.cash)
      throw new Error("wallet round trip lost data");
  }
}

run();
