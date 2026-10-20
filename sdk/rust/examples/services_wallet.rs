//! Exercises declared host service calls against a development memory wallet store.
use bff_plugin::{Context, Definition, Error, Plugin, Server};
use serde_json::json;

struct Wallet;

impl Plugin for Wallet {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        let wallet = json!({"user_num": 9007199254740993i64, "lucci": 73, "cash": 19});
        for _ in 0..2 {
            ctx.call_service("IPlayerWalletRepository", "UpsertAsync", json!({"wallet": wallet}))?;
        }
        let actual = ctx.call_service("IPlayerWalletRepository", "FindByUserNumAsync", json!({"user_num": 9007199254740993i64}))?;
        if actual != wallet { return Err("wallet round trip lost data".into()); }
        Ok(())
    }
}

fn main() {
    std::process::exit(Server::new().register(Definition {
        id: "example.services-wallet".into(), version: "1.0.0".into(),
        requires: vec!["IPlayerWalletRepository".into()], ..Default::default()
    }, || Wallet).run());
}
