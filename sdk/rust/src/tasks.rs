use super::*;

/// One host-owned background operation with cooperative cancellation.
pub struct TaskCall {
    cancelled: AtomicBool,
}

impl TaskCall {
    /// Reports whether the host has requested this task to stop.
    pub fn is_cancelled(&self) -> bool { self.cancelled.load(Ordering::SeqCst) }
    pub(super) fn cancel(&self) { self.cancelled.store(true, Ordering::SeqCst); }
}

/// A task callback invoked by the host's native task owner.
pub type TaskHandler = Arc<dyn Fn(&TaskCall) -> Result<(), Error> + Send + Sync>;

/// A native-owned task. Waiting observes callback work; disposal cancels and joins native cleanup.
pub struct TaskHandle {
    id: String,
    callback: String,
    rt: Arc<Runtime>,
    state: Arc<Activation>,
}

impl TaskHandle {
    /// Gets the opaque task id.
    pub fn id(&self) -> &str { &self.id }

    /// Waits for callback work without dropping its native owner.
    pub fn wait(&self) -> Result<Value, Error> {
        Ok(self.rt.call("tasks.wait", json!({"activation": self.state.id, "task": self.id}))?)
    }

    /// Requests cancellation and joins native cleanup.
    pub fn dispose(self) -> Result<(), Error> {
        self.rt.call("tasks.dispose", json!({"activation": self.state.id, "task": self.id}))?;
        self.state.task_callbacks.lock().unwrap().remove(&self.callback);
        Ok(())
    }
}

impl Context {
    /// Starts work under the host's native task owner and shared effect budget.
    /// Register the returned handle with [`Context::effect_fn`] so teardown owns it.
    /// WebAssembly modules have one thread and refuse tasks: running a callback inline would keep the
    /// module from reading the host's `tasks.cancel` and `deactivate` requests.
    pub fn run_task<F>(&self, name: &str, work: F) -> Result<TaskHandle, Error>
    where F: Fn(&TaskCall) -> Result<(), Error> + Send + Sync + 'static {
        if super::INLINE { return Err("tasks are not supported in WebAssembly mode".into()); }
        if !self.state.live.load(Ordering::SeqCst) || self.rt.protocol_version.load(Ordering::SeqCst) < 2 {
            return Err("tasks require a live protocol v2 activation".into());
        }
        if name.is_empty() { return Err("task name must be nonempty".into()); }
        let callback = format!("t{}", self.state.next_task.fetch_add(1, Ordering::SeqCst) + 1);
        self.state.task_callbacks.lock().unwrap().insert(callback.clone(), Arc::new(work));
        let result = self.rt.call("tasks.start", json!({"activation": self.state.id, "name": name, "callback": callback}));
        let task = match result {
            Ok(value) => param_str(&value, "task")?.to_string(),
            Err(error) => { self.state.task_callbacks.lock().unwrap().remove(&callback); return Err(Box::new(error)); }
        };
        Ok(TaskHandle { id: task, callback, rt: self.rt.clone(), state: self.state.clone() })
    }
}

impl Runtime {
    pub(super) fn prepare_task(self: &Arc<Self>, params: &Value) -> Result<(), RpcError> {
        let key = identity(params)?;
        let call = Arc::new(TaskCall { cancelled: AtomicBool::new(false) });
        let mut calls = self.task_calls.lock().unwrap();
        if calls.contains_key(&key) { return Err(RpcError::new(codes::INVALID_PARAMS, "duplicate task invocation")); }
        calls.insert(key, call);
        Ok(())
    }

    pub(super) fn invoke_task(self: &Arc<Self>, params: &Value) -> Result<Value, RpcError> {
        let state = self.live(params)?;
        let callback = param_str(params, "callback")?;
        let key = identity(params)?;
        let call = self.task_calls.lock().unwrap().get(&key).cloned().ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "task invocation is not admitted"))?;
        let handler = state.task_callbacks.lock().unwrap().get(callback).cloned().ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "unknown task callback"))?;
        guarded(|| handler(&call)).map(|_| json!({}))
    }

    pub(super) fn finish_task(&self, params: &Value) {
        if let Ok(key) = identity(params) { if let Some(call) = self.task_calls.lock().unwrap().remove(&key) { call.cancel(); } }
    }

    pub(super) fn cancel_task(&self, params: &Value) {
        if let Ok(key) = identity(params) { if let Some(call) = self.task_calls.lock().unwrap().get(&key) { call.cancel(); } }
    }
}

fn identity(params: &Value) -> Result<(String, String), RpcError> {
    Ok((param_str(params, "activation")?.to_string(), param_str(params, "invocation")?.to_string()))
}
