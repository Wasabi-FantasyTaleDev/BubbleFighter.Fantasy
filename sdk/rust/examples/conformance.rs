//! Serves the two plugins the shared fixtures in tests/fixtures/sdk-protocol are written against; the
//! fixture header lines describe their behaviour.

use bff_plugin::{optional, required, Context, Definition, Error, Plugin, Server};
use serde_json::json;

struct Echo;

impl Plugin for Echo {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        let greeting = ctx.string("greeting");
        let mut protocols = vec![ctx.string("protocol")];
        protocols.extend(ctx.strings("extra_protocols"));
        for protocol in protocols {
            let (c, greeting, p) = (ctx.clone(), greeting.clone(), protocol.clone());
            let handle = ctx.on(&protocol, "LoginRequested", move |session, body| {
                let account = body.get("account").and_then(|v| v.as_str()).unwrap_or("");
                if account == "task" {
                    // A WebAssembly module has one thread, so it must refuse tasks instead of starving host requests.
                    let outcome = match c.run_task("probe", |_| Ok(())) {
                        Ok(task) => { task.dispose()?; "tasks are available".to_string() }
                        Err(error) => error.to_string(),
                    };
                    c.info(&format!("run-task: {outcome}"));
                    return c.send(&p, &session.id, "LoginAccepted", json!({
                        "user_number": 1, "account": format!("task:{outcome}"), "nickname": account,
                        "server_time": "2026-01-01T00:00:00Z", "character_item_id": 300001,
                    }));
                }
                c.info(&format!("{greeting} {account}"));
                c.send(&p, &session.id, "LoginAccepted", json!({
                    "user_number": 1, "account": account, "nickname": account,
                    "server_time": "2026-01-01T00:00:00Z", "character_item_id": 300001,
                }))
            })?;
            ctx.effect(handle);
        }
        Ok(())
    }
}

struct Failing;

impl Plugin for Failing {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        let handle = ctx.on(&ctx.string("protocol"), "LoginRequested", |_, _| unreachable!())?;
        ctx.effect(handle);
        Err("activation refused".into())
    }
}

struct Middleware;

impl Plugin for Middleware {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        let protocol = ctx.string("protocol");
        let behavior = ctx.string("behavior");
        let sender = ctx.clone();
        let callback_protocol = protocol.clone();
        let handle = ctx.use_middleware(&protocol, "LoginRequested", 7, move |session, body, next, _lifetime| {
            let account = body.get("account").and_then(|v| v.as_str()).unwrap_or("");
            sender.send(&callback_protocol, &session.id, "LoginAccepted", json!({
                "user_number": 1, "account": format!("middleware:{account}"), "nickname": account,
                "server_time": "2026-01-01T00:00:00Z", "character_item_id": 300001,
            }))?;
            next()?;
            if behavior == "twice" {
                let _ = next();
            }
            Ok(())
        })?;
        ctx.effect(handle);
        Ok(())
    }
}

fn main() {
    let s = |v: &[&str]| v.iter().map(|x| x.to_string()).collect::<Vec<_>>();
    let server = Server::new()
        .register(
            Definition {
                id: "conformance.echo".into(),
                version: "1.0.0".into(),
                description: "Echoes a greeting on login.".into(),
                supported_builds: s(&["kr:812"]),
                handles: s(&["LoginRequested"]),
                sends: s(&["LoginAccepted"]),
                config: vec![
                    ("protocol".into(), required("string")),
                    ("greeting".into(), optional("string", json!("hello"))),
                    ("extra_protocols".into(), optional("list", json!([]))),
                ],
                ..Default::default()
            },
            || Echo,
        )
        .register(
            Definition {
                id: "conformance.failing".into(),
                version: "1.0.0".into(),
                description: "Registers a handler, then fails activation.".into(),
                handles: s(&["LoginRequested"]),
                config: vec![("protocol".into(), required("string"))],
                ..Default::default()
            },
            || Failing,
        );
    let server = server.register(
        Definition {
            id: "conformance.middleware".into(),
            version: "1.0.0".into(),
            description: "Wraps LoginRequested and continues the native pipeline once.".into(),
            supported_builds: s(&["kr:812"]),
            sends: s(&["LoginAccepted"]),
            middleware: s(&["LoginRequested"]),
            config: vec![("protocol".into(), required("string")), ("behavior".into(), optional("string", json!("once")))],
            ..Default::default()
        },
        || Middleware,
    );
    std::process::exit(server.run());
}
