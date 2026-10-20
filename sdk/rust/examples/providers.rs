//! Provider fixtures mounted only by loader tests, never as live persistence.
use bff_plugin::{Context, Definition, Error, optional, Plugin, Server, ServiceHandler};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

struct Provider;
impl Plugin for Provider {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        let mut methods: HashMap<String, ServiceHandler> = HashMap::new();
        methods.insert("get_ReplyAfterInventory".into(), Arc::new(|_| Ok(json!(false))));
        let handle = ctx.provide_service("IEquipmentReplyPolicy", methods, "decoratable", 0)?;
        ctx.effect(handle);
        let stored = Arc::new(Mutex::new(Value::Null));
        let writer = stored.clone();
        let waiting = ctx.config["behavior"] == "wait";
        let logger = ctx.clone();
        let mut methods: HashMap<String, ServiceHandler> = HashMap::new();
        methods.insert("UpsertAsync".into(), Arc::new(move |call| { *writer.lock().unwrap() = call.args["record"].clone(); Ok(Value::Null) }));
        methods.insert("ListByUserNumAsync".into(), Arc::new(move |call| {
            if waiting && call.args["user_num"] == json!(9007199254740993i64) {
                logger.log("info", "provider-call-started", Default::default());
                let deadline = std::time::Instant::now() + std::time::Duration::from_secs(15);
                while !call.is_cancelled() {
                    if std::time::Instant::now() >= deadline { return Err("provider cancellation was not received".into()); }
                    std::thread::sleep(std::time::Duration::from_millis(1));
                }
                logger.log("info", "provider-call-cancelled", Default::default());
                return Ok(json!([]));
            }
            let record = stored.lock().unwrap();
            Ok(if record.is_object() && record["user_num"] == call.args["user_num"] { json!([*record]) } else { json!([]) })
        }));
        let handle = ctx.provide_service("IFighterRepository", methods, "singleton", 0)?;
        ctx.effect(handle);
        Ok(())
    }
}

struct Decorator;
impl Plugin for Decorator {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        let negate = ctx.config["operation"] == "negate";
        let mut methods: HashMap<String, ServiceHandler> = HashMap::new();
        methods.insert("get_ReplyAfterInventory".into(), Arc::new(move |call| {
            let value = call.inner(None, None)?.as_bool().ok_or("policy result must be bool")?;
            Ok(json!(negate && !value))
        }));
        let handle = ctx.decorate_service("IEquipmentReplyPolicy", methods, ctx.config["priority"].as_i64().unwrap() as i32)?;
        ctx.effect(handle);
        Ok(())
    }
}

struct Many;
impl Plugin for Many {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        let value = ctx.config["value"].clone();
        let mut methods: HashMap<String, ServiceHandler> = HashMap::new();
        methods.insert("get_ReplyAfterInventory".into(), Arc::new(move |_| Ok(value.clone())));
        let handle = ctx.provide_service("IEquipmentReplyPolicy", methods, "many", ctx.config["priority"].as_i64().unwrap() as i32)?;
        ctx.effect(handle);
        Ok(())
    }
}

struct Consumer;
impl Plugin for Consumer {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        let value = ctx.call_service("IEquipmentReplyPolicy", "get_ReplyAfterInventory", json!({}))?;
        if value != json!(false) { return Err("SDK consumer expected the registered provider's false policy".into()); }
        ctx.log("info", "provider-consumer-ok", Default::default());
        Ok(())
    }
}

struct ManyConsumer;
impl Plugin for ManyConsumer {
    fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
        let selected = ctx.get_all_services("IEquipmentReplyPolicy")?;
        let mut values = Vec::new();
        for index in 0..selected.count { values.push(selected.call(index, "get_ReplyAfterInventory", json!({}))?); }
        if json!(values) != ctx.config["expected"] { return Err("unexpected ordered services".into()); }
        selected.dispose()?;
        ctx.info("many-consumer-ok");
        Ok(())
    }
}

fn main() {
    let server = Server::new()
        .register(Definition { id: "conformance.provider".into(), version: "1.0.0".into(), provides: vec!["IEquipmentReplyPolicy".into(), "IFighterRepository".into()], config: vec![("behavior".into(), optional("string", json!("normal")))], ..Default::default() }, || Provider)
        .register(Definition { id: "conformance.decorator".into(), version: "1.0.0".into(), decorates: vec!["IEquipmentReplyPolicy".into()], config: vec![("priority".into(), optional("int", json!(0))), ("operation".into(), optional("string", json!("negate")))], ..Default::default() }, || Decorator)
        .register(Definition { id: "conformance.provider-many".into(), version: "1.0.0".into(), provides_many: vec!["IEquipmentReplyPolicy".into()], config: vec![("priority".into(), optional("int", json!(0))), ("value".into(), optional("bool", json!(false)))], ..Default::default() }, || Many)
        .register(Definition { id: "conformance.provider-consumer".into(), version: "1.0.0".into(), requires: vec!["IEquipmentReplyPolicy".into()], ..Default::default() }, || Consumer)
        .register(Definition { id: "conformance.many-consumer".into(), version: "1.0.0".into(), requires_many: vec!["IEquipmentReplyPolicy".into()], config: vec![("expected".into(), optional("list", json!([false, true, false])))], ..Default::default() }, || ManyConsumer)
        .register(Definition { id: "conformance.optional-many-consumer".into(), version: "1.0.0".into(), optional_many: vec!["IEquipmentReplyPolicy".into()], config: vec![("expected".into(), optional("list", json!([])))], ..Default::default() }, || ManyConsumer);
    std::process::exit(server.run());
}
