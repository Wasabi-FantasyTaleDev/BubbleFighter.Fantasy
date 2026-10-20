use super::*;

/// An exact shared event identity and native subscription/publication mode.
#[derive(Debug, Clone)]
pub struct EventSpec {
    /// Event name from the shared schema; fully qualified names resolve ambiguities.
    pub event_type: String,
    /// Subscribe: sync/async/waterfall/bail. Publish: emit/serial/parallel/waterfall/bail.
    pub mode: String,
    /// Required result identity for waterfall/bail, absent for notification modes.
    pub result_type: Option<String>,
}

impl EventSpec {
    /// Declares an event operation without a result; use with_result for waterfall/bail.
    pub fn new(event_type: &str, mode: &str) -> Self {
        Self { event_type: event_type.to_string(), mode: mode.to_string(), result_type: None }
    }

    /// Adds the exact native result type to this declaration.
    pub fn with_result(mut self, result_type: &str) -> Self {
        self.result_type = Some(result_type.to_string());
        self
    }

    pub(super) fn wire(&self) -> Value {
        let mut value = json!({"type": self.event_type, "mode": self.mode});
        if let Some(result) = &self.result_type { value["result_type"] = json!(result); }
        value
    }
}

/// A listener or terminal. Notification modes return Value::Null; result modes return their declared type.
pub type EventHandler = Arc<dyn Fn(&EventCall) -> Result<Value, Error> + Send + Sync>;

/// One admitted native event callback with cooperative cancellation and scoped continuation.
pub struct EventCall {
    /// Validated event body; integer values preserve exact signed/unsigned 64-bit precision.
    pub body: Value,
    /// Fully qualified shared event type.
    pub event_type: String,
    /// The invoked subscription mode, or terminal for a publication's innermost operation.
    pub mode: String,
    rt: Arc<Runtime>,
    key: (String, String),
    has_next: bool,
    live: AtomicBool,
    cancelled: AtomicBool,
}

impl EventCall {
    /// Reports cancellation; WASM receives cooperative signals when it reads host traffic.
    pub fn is_cancelled(&self) -> bool { self.cancelled.load(Ordering::SeqCst) }

    /// Calls the selected downstream pipeline; native semantics permit repeated calls while live.
    pub fn next(&self) -> Result<Value, Error> {
        if !self.live.load(Ordering::SeqCst) || !self.has_next {
            return Err("this event invocation has no live continuation".into());
        }
        Ok(self.rt.call("events.next", json!({"activation": self.key.0, "invocation": self.key.1}))?)
    }

    /// Removes a registration in this native callback context, so self-removal does not wait on itself.
    pub fn dispose(&self, handle: Handle) -> Result<(), Error> {
        if !self.live.load(Ordering::SeqCst) || !Arc::ptr_eq(&self.rt, &handle.rt) || self.key.0 != handle.state.id {
            return Err("the event invocation or handle does not belong to this live activation".into());
        }
        handle.dispose_in_event(Some(&self.key.1))
    }

    pub(super) fn cancel(&self) { self.cancelled.store(true, Ordering::SeqCst); }
}

impl Context {
    fn reserve_event(&self, handler: EventHandler) -> Result<String, Error> {
        self.check_events_live()?;
        // Both registration families take these locks in the same order and release them before any RPC.
        let providers = self.state.providers.lock().unwrap();
        let mut events = self.state.event_callbacks.lock().unwrap();
        self.check_events_live()?;
        if providers.len() + events.len() >= self.state.max_provider_callbacks {
            return Err("callbacks exceed max_provider_callbacks".into());
        }
        let callback = format!("e{}", self.state.next_provider.fetch_add(1, Ordering::SeqCst) + 1);
        events.insert(callback.clone(), handler);
        Ok(callback)
    }

    fn check_events_live(&self) -> Result<(), Error> {
        if !self.state.live.load(Ordering::SeqCst) || self.rt.protocol_version.load(Ordering::SeqCst) < 2 {
            return Err("events require a live protocol v2 activation".into());
        }
        Ok(())
    }

    /// Registers an owned listener in the native bus. Higher priority runs first, ties keep registration order.
    pub fn on_event(&self, event: &EventSpec, handler: EventHandler, priority: i32) -> Result<Handle, Error> {
        let callback = self.reserve_event(handler)?;
        let mut params = event.wire();
        params["activation"] = json!(self.state.id);
        params["callback"] = json!(callback);
        params["priority"] = json!(priority);
        match self.rt.call("events.on", params) {
            Ok(result) => Ok(Handle { id: param_str(&result, "handle")?.to_string(), rt: self.rt.clone(), state: self.state.clone() }),
            Err(error) => { self.state.event_callbacks.lock().unwrap().remove(&callback); Err(Box::new(error)) }
        }
    }

    /// Publishes in the explicit native mode. Only waterfall takes a terminal callback.
    /// The temporary terminal remains retained until the actual native dispatch completes.
    pub fn publish_event(&self, event: &EventSpec, body: Value, terminal: Option<EventHandler>) -> Result<Value, Error> {
        self.check_events_live()?;
        if (event.mode == "waterfall") != terminal.is_some() {
            return Err("only waterfall publication requires a terminal callback".into());
        }
        let callback = terminal.map(|handler| self.reserve_event(handler)).transpose()?;
        let mut params = event.wire();
        params["activation"] = json!(self.state.id);
        params["body"] = body;
        if let Some(id) = &callback { params["terminal"] = json!(id); }
        let result = self.rt.call("events.publish", params);
        if let Some(id) = callback { self.state.event_callbacks.lock().unwrap().remove(&id); }
        Ok(result?)
    }
}

fn identity(params: &Value) -> Result<(String, String), RpcError> {
    Ok((param_str(params, "activation")?.to_string(), param_str(params, "invocation")?.to_string()))
}

impl Runtime {
    pub(super) fn prepare_event(self: &Arc<Self>, params: &Value) -> Result<(), RpcError> {
        let key = identity(params)?;
        let body = params.get("body").filter(|v| v.is_object()).ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "event body must be an object"))?.clone();
        let call = Arc::new(EventCall { body, event_type: param_str(params, "type")?.to_string(), mode: param_str(params, "mode")?.to_string(),
            rt: self.clone(), key: key.clone(), has_next: params.get("has_next").and_then(Value::as_bool).unwrap_or(false),
            live: AtomicBool::new(true), cancelled: AtomicBool::new(false) });
        let mut calls = self.event_calls.lock().unwrap();
        if calls.contains_key(&key) { return Err(RpcError::new(codes::INVALID_PARAMS, "duplicate event invocation")); }
        calls.insert(key, call);
        Ok(())
    }

    pub(super) fn invoke_event(self: &Arc<Self>, params: &Value) -> Result<Value, RpcError> {
        let state = self.live(params)?;
        let callback = param_str(params, "callback")?;
        let call = self.event_calls.lock().unwrap().get(&identity(params)?).cloned().ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "event invocation is not admitted"))?;
        let handler = state.event_callbacks.lock().unwrap().get(callback).cloned().ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "unknown event callback"))?;
        guarded(|| handler(&call))
    }

    pub(super) fn finish_event(&self, params: &Value) {
        if let Ok(key) = identity(params) {
            if let Some(call) = self.event_calls.lock().unwrap().remove(&key) {
                call.live.store(false, Ordering::SeqCst);
                call.cancel();
            }
        }
    }

    pub(super) fn cancel_event(&self, params: &Value) {
        if let Ok(key) = identity(params) {
            if let Some(call) = self.event_calls.lock().unwrap().get(&key) { call.cancel(); }
        }
    }
}
