use super::*;

/// One host-owned resource release callback with cooperative cancellation.
pub struct ResourceCall {
    cancelled: AtomicBool,
    /// Opaque callback invocation id, useful for diagnostics and self-disposal checks.
    pub invocation: String,
}

impl ResourceCall {
    /// Reports whether the host has requested release to stop.
    pub fn is_cancelled(&self) -> bool { self.cancelled.load(Ordering::SeqCst) }
    pub(super) fn cancel(&self) { self.cancelled.store(true, Ordering::SeqCst); }
}

/// A resource release callback executed while native ownership remains admitted.
pub type ResourceHandler = Arc<dyn Fn(&ResourceCall) -> Result<(), Error> + Send + Sync>;

/// A native-owned resource handle.
pub struct ResourceHandle {
    id: String,
    callback: String,
    rt: Arc<Runtime>,
    state: Arc<Activation>,
}

impl ResourceHandle {
    /// Gets the opaque resource id.
    pub fn id(&self) -> &str { &self.id }
    /// Releases the resource early. Repeated ownership should be represented by one handle.
    pub fn dispose(self) -> Result<(), Error> {
        let result = self.rt.call("resources.dispose", json!({"activation": self.state.id, "resource": self.id}))?;
        if result.get("deferred").and_then(Value::as_bool) != Some(true) {
            self.state.resource_callbacks.lock().unwrap().remove(&self.callback);
        }
        Ok(())
    }
}

/// A child resource lifetime sharing the activation's bounded capacity.
pub struct ResourceScope {
    id: String,
    rt: Arc<Runtime>,
    state: Arc<Activation>,
}

impl ResourceScope {
    /// Gets the opaque scope id.
    pub fn id(&self) -> &str { &self.id }
    /// Creates a child scope.
    pub fn create(&self, name: &str) -> Result<ResourceScope, Error> {
        let result = self.rt.call("resources.scope", json!({"activation": self.state.id, "parent": self.id, "name": name}))?;
        Ok(ResourceScope { id: param_str(&result, "scope")?.to_string(), rt: self.rt.clone(), state: self.state.clone() })
    }
    /// Acquires a native-owned resource with a guest release callback.
    pub fn acquire<F>(&self, name: &str, release: F) -> Result<ResourceHandle, Error>
    where F: Fn(&ResourceCall) -> Result<(), Error> + Send + Sync + 'static {
        if name.is_empty() { return Err("resource name must be nonempty".into()); }
        let callback = format!("r{}", self.state.next_resource.fetch_add(1, Ordering::SeqCst) + 1);
        self.state.resource_callbacks.lock().unwrap().insert(callback.clone(), Arc::new(release));
        let result = match self.rt.call("resources.acquire", json!({"activation": self.state.id, "scope": self.id, "name": name, "callback": callback})) {
            Ok(value) => value,
            Err(error) => { self.state.resource_callbacks.lock().unwrap().remove(&callback); return Err(Box::new(error)); }
        };
        let id = param_str(&result, "resource")?.to_string();
        Ok(ResourceHandle { id, callback, rt: self.rt.clone(), state: self.state.clone() })
    }
    /// Disposes this scope and all native-owned descendants.
    pub fn dispose(self) -> Result<(), Error> {
        self.rt.call("resources.scope.dispose", json!({"activation": self.state.id, "scope": self.id}))?;
        Ok(())
    }
}

/// Creates native-owned resource scopes for an activation.
pub struct Resources { pub(crate) rt: Arc<Runtime>, pub(crate) state: Arc<Activation> }

impl Resources {
    /// Creates a root resource scope.
    pub fn scope(&self, name: &str) -> Result<ResourceScope, Error> {
        if !self.state.live.load(Ordering::SeqCst) || self.rt.protocol_version.load(Ordering::SeqCst) < 2 {
            return Err("resource scopes require a live protocol v2 activation".into());
        }
        let result = self.rt.call("resources.scope", json!({"activation": self.state.id, "name": name}))?;
        Ok(ResourceScope { id: param_str(&result, "scope")?.to_string(), rt: self.rt.clone(), state: self.state.clone() })
    }
}

impl Runtime {
    pub(super) fn prepare_resource(self: &Arc<Self>, params: &Value) -> Result<(), RpcError> {
        let key = identity(params)?;
        let invocation = param_str(params, "invocation")?.to_string();
        let call = Arc::new(ResourceCall { cancelled: AtomicBool::new(false), invocation });
        let mut calls = self.resource_calls.lock().unwrap();
        if calls.contains_key(&key) { return Err(RpcError::new(codes::INVALID_PARAMS, "duplicate resource invocation")); }
        calls.insert(key, call);
        Ok(())
    }
    pub(super) fn invoke_resource(self: &Arc<Self>, params: &Value) -> Result<Value, RpcError> {
        let activation = param_str(params, "activation")?;
        let state = self.active.lock().unwrap().get(activation).cloned()
            .or_else(|| self.retired.lock().unwrap().get(activation).cloned())
            .ok_or_else(|| RpcError::new(codes::ACTIVATION_NOT_LIVE, "activation is not known"))?;
        let callback = param_str(params, "callback")?;
        let call = self.resource_calls.lock().unwrap().get(&identity(params)?).cloned()
            .ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "resource invocation is not admitted"))?;
        let handler = state.resource_callbacks.lock().unwrap().get(callback).cloned()
            .ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "unknown resource callback"))?;
        let result = guarded(|| handler(&call));
        state.resource_callbacks.lock().unwrap().remove(callback);
        if state.resource_callbacks.lock().unwrap().is_empty() {
            self.retired.lock().unwrap().remove(activation);
            state.release_callbacks();
        }
        result.map(|_| json!({}))
    }
    pub(super) fn finish_resource(&self, params: &Value) {
        if let Ok(key) = identity(params) { if let Some(call) = self.resource_calls.lock().unwrap().remove(&key) { call.cancel(); } }
    }
    pub(super) fn cancel_resource(&self, params: &Value) {
        if let Ok(key) = identity(params) { if let Some(call) = self.resource_calls.lock().unwrap().get(&key) { call.cancel(); } }
    }
}

fn identity(params: &Value) -> Result<(String, String), RpcError> {
    Ok((param_str(params, "activation")?.to_string(), param_str(params, "invocation")?.to_string()))
}
