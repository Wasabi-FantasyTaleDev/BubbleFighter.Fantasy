use super::*;

/// An implementation of one service method, receiving its arguments and invocation lifetime.
pub type ServiceHandler = Arc<dyn Fn(&ServiceCall) -> Result<Value, Error> + Send + Sync>;

/// An owned ordered service snapshot. Indices keep their selected provider identity until disposal.
pub struct ServiceCollection {
    handle: Handle,
    service: String,
    collection: String,
    /// Number of selected providers, including zero for an absent optional extension.
    pub count: usize,
}

impl ServiceCollection {
    /// Calls one selected member; the host rejects expired collections and invalid indices.
    pub fn call(&self, index: usize, method: &str, args: Value) -> Result<Value, Error> {
        Ok(self.handle.rt.call("services.call", json!({"activation": self.handle.state.id,
            "service": self.service, "collection": self.collection, "index": index, "method": method, "args": args}))?)
    }

    /// Releases this selection early. Otherwise register its disposer with Context::effect_fn.
    pub fn dispose(self) -> Result<(), Error> { self.handle.dispose() }
}

/// One admitted service invocation, with cooperative cancellation and a scoped inner service.
pub struct ServiceCall {
    /// Complete schema method id.
    pub method: String,
    /// Named arguments; integers retain their signed or unsigned 64-bit precision.
    pub args: Value,
    rt: Arc<Runtime>,
    key: (String, String),
    has_inner: bool,
    cancelled: AtomicBool,
    live: AtomicBool,
}

impl ServiceCall {
    /// Reports caller cancellation or activation/transport shutdown. WASM observes notifications while reading host calls.
    pub fn is_cancelled(&self) -> bool { self.cancelled.load(Ordering::SeqCst) }

    /// Calls the selected native inner service. None reuses the current method or arguments.
    pub fn inner(&self, method: Option<&str>, args: Option<Value>) -> Result<Value, Error> {
        if !self.live.load(Ordering::SeqCst) || !self.has_inner {
            return Err(Box::new(RpcError::new(codes::INVALID_PARAMS, "this invocation has no live inner service")));
        }
        Ok(self.rt.call("services.inner", json!({
            "activation": self.key.0, "invocation": self.key.1,
            "method": method.unwrap_or(&self.method), "args": args.as_ref().unwrap_or(&self.args),
        }))?)
    }

    pub(super) fn cancel(&self) { self.cancelled.store(true, Ordering::SeqCst); }
}

impl Context {
    /// Selects the declared ordered providers through the activation's ordinary service view.
    pub fn get_all_services(&self, service: &str) -> Result<ServiceCollection, Error> {
        if !self.state.live.load(Ordering::SeqCst) || self.rt.protocol_version.load(Ordering::SeqCst) < 2 {
            return Err(Box::new(RpcError::new(codes::ACTIVATION_NOT_LIVE, "service collections require a live protocol v2 activation")));
        }
        let result = self.rt.call("services.get_all", json!({"activation": self.state.id, "service": service}))?;
        let id = param_str(&result, "handle")?.to_string();
        let collection = param_str(&result, "collection")?.to_string();
        let count = result["count"].as_u64().and_then(|value| usize::try_from(value).ok()).ok_or("invalid service collection count")?;
        Ok(ServiceCollection { handle: Handle { id, rt: self.rt.clone(), state: self.state.clone() },
            service: service.to_string(), collection, count })
    }

    /// Publishes every method under singleton, decoratable, many or decorate registration mode.
    /// Keys are unambiguous method names or full schema ids. Targets survive unregister for retained native projections.
    pub fn provide_service(&self, service: &str, methods: HashMap<String, ServiceHandler>, mode: &str, priority: i32) -> Result<Handle, Error> {
        if !self.state.live.load(Ordering::SeqCst) || self.rt.protocol_version.load(Ordering::SeqCst) < 2 {
            return Err(Box::new(RpcError::new(codes::ACTIVATION_NOT_LIVE, "service providers require a live protocol v2 activation")));
        }
        if methods.is_empty() || methods.keys().any(|name| name.is_empty()) {
            return Err(Box::new(RpcError::new(codes::INVALID_PARAMS, "methods must not be empty")));
        }
        let mut names: Vec<_> = methods.keys().cloned().collect();
        names.sort();
        let callback = {
            let mut providers = self.state.providers.lock().unwrap();
            if !self.state.live.load(Ordering::SeqCst) {
                return Err(Box::new(RpcError::new(codes::ACTIVATION_NOT_LIVE, "the activation is not live")));
            }
            if providers.len() + self.state.event_callbacks.lock().unwrap().len() >= self.state.max_provider_callbacks {
                return Err(Box::new(RpcError::new(codes::INTERNAL_ERROR, "provider callbacks exceed max_provider_callbacks")));
            }
            let callback = format!("p{}", self.state.next_provider.fetch_add(1, Ordering::SeqCst) + 1);
            providers.insert(callback.clone(), methods);
            callback
        };
        let result = self.rt.call("services.provide", json!({
            "activation": self.state.id, "service": service, "mode": mode,
            "priority": priority, "callback": callback, "methods": names,
        }));
        let result = match result {
            Ok(result) => result,
            Err(error) => { self.state.providers.lock().unwrap().remove(&callback); return Err(Box::new(error)); }
        };
        let id = param_str(&result, "handle")?.to_string();
        Ok(Handle { id, rt: self.rt.clone(), state: self.state.clone() })
    }

    /// Adds an owned layer around a declared decoratable singleton.
    pub fn decorate_service(&self, service: &str, methods: HashMap<String, ServiceHandler>, priority: i32) -> Result<Handle, Error> {
        self.provide_service(service, methods, "decorate", priority)
    }
}

fn identity(params: &Value) -> Result<(String, String), RpcError> {
    Ok((param_str(params, "activation")?.to_string(), param_str(params, "invocation")?.to_string()))
}

impl Runtime {
    pub(super) fn prepare_service(self: &Arc<Self>, params: &Value) -> Result<(), RpcError> {
        let key = identity(params)?;
        let method = param_str(params, "method")?.to_string();
        let args = params.get("args").filter(|value| value.is_object()).ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "provider args must be an object"))?.clone();
        let call = Arc::new(ServiceCall { method, args, rt: self.clone(), key: key.clone(),
            has_inner: params.get("has_inner").and_then(Value::as_bool).unwrap_or(false),
            cancelled: AtomicBool::new(false), live: AtomicBool::new(true) });
        let mut calls = self.service_calls.lock().unwrap();
        if calls.contains_key(&key) { return Err(RpcError::new(codes::INVALID_PARAMS, "duplicate service invocation")); }
        calls.insert(key, call);
        Ok(())
    }

    pub(super) fn invoke_service(self: &Arc<Self>, params: &Value) -> Result<Value, RpcError> {
        let state = self.live(params)?;
        let callback = param_str(params, "callback")?;
        let key = identity(params)?;
        let call = self.service_calls.lock().unwrap().get(&key).cloned().ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "service invocation is not admitted"))?;
        let handler = {
            let providers = state.providers.lock().unwrap();
            providers.get(callback).and_then(|methods| methods.get(&call.method)
                .or_else(|| params.get("method_name").and_then(Value::as_str).and_then(|name| methods.get(name)))).cloned()
        }.ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "unknown provider method"))?;
        guarded(|| handler(&call))
    }

    pub(super) fn finish_service(&self, params: &Value) {
        if let Ok(key) = identity(params) {
            if let Some(call) = self.service_calls.lock().unwrap().remove(&key) {
                call.live.store(false, Ordering::SeqCst);
                call.cancel();
            }
        }
    }

    pub(super) fn cancel_service(&self, params: &Value) {
        if let Ok(key) = identity(params) {
            if let Some(call) = self.service_calls.lock().unwrap().get(&key) { call.cancel(); }
        }
    }
}
