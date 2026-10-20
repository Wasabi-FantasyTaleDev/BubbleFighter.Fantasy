//! Event fixtures for hosted-loader tests, never installed in game profiles.
use bff_plugin::{Context, Definition, Error, EventCall, EventHandler, EventSpec, Handle, optional, Plugin, Server};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

fn subscriptions() -> Vec<EventSpec> {
    vec![EventSpec::new("EquipmentSelectionApplying", "sync"), EventSpec::new("EquipmentSelectionApplying", "async"),
        EventSpec::new("EquipmentSelectionApplying", "waterfall").with_result("Int32"),
        EventSpec::new("EquipmentSelectionApplying", "bail").with_result("String")]
}
fn publications() -> Vec<EventSpec> {
    let mut result = vec![EventSpec::new("EquipmentSelectionApplying", "emit"), EventSpec::new("EquipmentSelectionApplying", "serial"),
        EventSpec::new("EquipmentSelectionApplying", "parallel")];
    result.extend(subscriptions().into_iter().skip(2));
    result
}

struct EventPlugin;
impl Plugin for EventPlugin {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        if ctx.string("behavior") == "resource-release" {
            let scope = ctx.resources().scope("test-scope")?;
            let context = ctx.clone();
            let resource = scope.acquire("test-resource", move |_| {
                context.log("info", "resource-released", Default::default());
                Ok(())
            })?;
            ctx.effect_fn(move || scope.dispose());
            ctx.effect_fn(move || resource.dispose());
            ctx.log("info", "resource-acquired", Default::default());
            return Ok(());
        }
        if ctx.string("behavior") == "lifetime-fail" {
            let context = ctx.clone();
            ctx.effect_fn(move || {
                if !context.lifetime().is_cancelled() { return Err("rollback did not cancel lifetime".into()); }
                context.log("info", "lifetime-rollback-cancelled", Default::default());
                Ok(())
            });
            return Err("lifetime activation failure".into());
        }
        let handles: Arc<Mutex<HashMap<String, Handle>>> = Arc::new(Mutex::new(HashMap::new()));
        let owned = handles.clone();
        ctx.effect_fn(move || {
            let selected = std::mem::take(&mut *owned.lock().unwrap());
            for (_, handle) in selected { handle.dispose()?; }
            Ok(())
        });
        for event in subscriptions() {
            if !ctx.strings("modes").contains(&event.mode) { continue; }
            let context = ctx.clone();
            let captured = handles.clone();
            let listener: EventHandler = Arc::new(move |call: &EventCall| {
                if call.body["user_num"] != json!(9007199254740993i64) { return Err("event identity lost precision".into()); }
                context.log("info", &format!("event:{}:{}", context.row_id, call.mode), Default::default());
                context.call_service("IEquipmentReplyPolicy", "get_ReplyAfterInventory", json!({}))?;
                let behavior = context.string("behavior");
                if behavior == "lifetime-alive" {
                    if context.lifetime().is_cancelled() { return Err("unloading another row cancelled this activation".into()); }
                    context.log("info", "lifetime-other-alive", Default::default());
                }
                if behavior == "lifetime-wait" {
                    context.log("info", "lifetime-wait-entered", Default::default());
                    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(15);
                    while !context.lifetime().is_cancelled() {
                        if std::time::Instant::now() >= deadline { return Err("activation cancellation was not received".into()); }
                        std::thread::sleep(std::time::Duration::from_millis(1));
                    }
                    context.log("info", "lifetime-wait-cancelled", Default::default());
                }
                if behavior == "self" {
                    let handle = captured.lock().unwrap().remove(&call.mode).ok_or("missing event handle")?;
                    call.dispose(handle)?;
                    return Ok(if call.mode == "waterfall" { json!(77) } else { Value::Null });
                }
                if behavior == format!("{}-error", call.mode) { return Err("event fixture failure".into()); }
                if behavior == "wait" && call.mode == "async" {
                    context.log("info", "event-call-started", Default::default());
                    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(15);
                    while !call.is_cancelled() {
                        if std::time::Instant::now() >= deadline { return Err("event cancellation was not received".into()); }
                        std::thread::sleep(std::time::Duration::from_millis(1));
                    }
                    context.log("info", "event-call-cancelled", Default::default());
                }
                if call.mode == "waterfall" {
                    if behavior == "short" { return Ok(json!(77)); }
                    if behavior == "null" { return Ok(Value::Null); }
                    let value = call.next()?.as_i64().ok_or("non-integer result")?;
                    let extra = if behavior == "twice" { call.next()?.as_i64().ok_or("non-integer result")? } else { 1 };
                    return Ok(json!(value + extra));
                }
                let decision = context.string("decision");
                Ok(if call.mode == "bail" && !decision.is_empty() { json!(decision) } else { Value::Null })
            });
            let handle = ctx.on_event(&event, listener, ctx.config["priority"].as_i64().ok_or("invalid priority")? as i32)?;
            handles.lock().unwrap().insert(event.mode, handle);
        }
        if ctx.config["publisher"] == true {
            let context = ctx.clone();
            let handle = ctx.on("test/game", "LoginRequested", move |_, message| {
                let event = publications().into_iter().find(|event| message["account"] == event.mode).ok_or("unknown publication mode")?;
                let terminal: Option<EventHandler> = if event.mode == "waterfall" { Some(Arc::new(|_| Ok(json!(100)))) } else { None };
                let result = context.publish_event(&event, json!({"protocol": "test/game", "user_num": 9007199254740993i64, "item_id": 13, "worn": true}), terminal)?;
                context.log("info", &format!("published:{}:{}", event.mode, result), Default::default());
                Ok(())
            })?;
            ctx.effect(handle);
        }
        Ok(())
    }
    fn deactivate(&mut self, ctx: &Context) -> Result<(), Error> {
        if !ctx.lifetime().is_cancelled() { return Err("deactivation did not cancel lifetime".into()); }
        ctx.log("info", &format!("lifetime-deactivated:{}", ctx.row_id), Default::default());
        Ok(())
    }
}

fn main() {
    let server = Server::new().register(Definition { id: std::env::var("EVENT_PLUGIN_ID").unwrap_or_else(|_| "conformance.events".into()), version: "1.0.0".into(),
        requires: vec!["IEquipmentReplyPolicy".into()], handles: vec!["LoginRequested".into()],
        subscribes: subscriptions(), publishes: publications(), config: vec![
            ("modes".into(), optional("list", json!(["sync", "async", "waterfall", "bail"]))),
            ("priority".into(), optional("int", json!(0))), ("behavior".into(), optional("string", json!("normal"))),
            ("decision".into(), optional("string", json!(""))), ("publisher".into(), optional("bool", json!(false))),
        ], ..Default::default() }, || EventPlugin);
    std::process::exit(server.run());
}
