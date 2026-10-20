//! Out-of-process plugins for the BubbleFighter Fantasy host, in Rust.
//!
//! A plugin process speaks SDK plugin protocols v1 and v2 (docs/sdk-protocol.md): newline-delimited JSON-RPC 2.0 on
//! stdin/stdout. The host's `loader.process` row starts the process, sends `initialize` and `describe`, and then
//! activates rows against the definitions registered with [`Server::register`]. Host requests run on their own
//! threads, so a request that calls back into the host never blocks the reader.
//!
//! The same plugin also builds for `wasm32-wasip1` and runs inside the host under `loader.wasm`. There the
//! protocol lines travel through the host imports `bff.recv`, `bff.send` and `bff.log` instead of stdio, and the
//! runtime is single-threaded: host requests are served one at a time, and a call into the host reads lines until
//! its response arrives, serving nested host requests within the configured depth and in-flight limits. Panics abort the module (wasm has no
//! unwinding), which the host reports as the plugin's fault.

use serde_json::{json, Map, Value};
use std::collections::HashMap;
#[cfg(not(target_arch = "wasm32"))]
use std::io::{BufRead, Read};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicUsize, Ordering};
use std::sync::mpsc;
use std::sync::{Arc, Mutex};

mod services;
pub use services::{ServiceCall, ServiceCollection, ServiceHandler};
mod events;
pub use events::{EventSpec, EventCall, EventHandler};
mod tasks;
pub use tasks::{TaskCall, TaskHandle};
mod resources;
pub use resources::{ResourceCall, ResourceHandle, ResourceScope, Resources};

/// Protocol version this SDK speaks.
pub const PROTOCOL_VERSION: i64 = 2;
/// SDK name reported in `initialize`.
pub const SDK_NAME: &str = "bff-plugin-rust";
/// Longest JSON line read or written; the host's default `max_line_bytes`.
pub const MAX_LINE_BYTES: usize = 1 << 20;

/// Line transport for `wasm32` builds: the host imports of `loader.wasm`.
#[cfg(target_arch = "wasm32")]
mod wasm_io {
    #[link(wasm_import_module = "bff")]
    extern "C" {
        fn recv(ptr: *mut u8, cap: i32) -> i32;
        fn send(ptr: *const u8, len: i32);
        fn log(ptr: *const u8, len: i32);
    }

    /// Blocks for the host's next line; `None` at end of input.
    pub fn read_line() -> Option<Vec<u8>> {
        let mut buf = vec![0u8; 64 * 1024];
        loop {
            // SAFETY: the host writes at most `cap` bytes into the buffer it was given.
            let n = unsafe { recv(buf.as_mut_ptr(), buf.len() as i32) };
            if n == 0 {
                return None;
            }
            if n < 0 {
                // The line is longer than the buffer and stays pending on the host: grow and ask again.
                buf.resize((-n) as usize, 0);
                continue;
            }
            buf.truncate(n as usize);
            return Some(buf);
        }
    }

    /// Emits one protocol line.
    pub fn write_line(line: &[u8]) {
        // SAFETY: the host only reads `len` bytes at `ptr`.
        unsafe { send(line.as_ptr(), line.len() as i32) }
    }

    /// Emits one diagnostic line.
    pub fn diag(text: &str) {
        // SAFETY: as above.
        unsafe { log(text.as_ptr(), text.len() as i32) }
    }
}

/// True when the runtime serves requests inline (wasm has no threads).
const INLINE: bool = cfg!(target_arch = "wasm32");

/// Teardown must reach a plugin whose callback slots are all in flight; only the lifecycle control
/// methods may use these slots, and the host admits the same reserve on its side of the wire.
const CONTROL_RESERVE: usize = 4;

/// Writes a diagnostic line: stderr for a process, `bff.log` for a module.
fn diag(text: &str) {
    #[cfg(target_arch = "wasm32")]
    wasm_io::diag(text);
    #[cfg(not(target_arch = "wasm32"))]
    eprintln!("{text}");
}

/// Error codes of the SDK plugin protocol.
pub mod codes {
    /// Plugin code failed.
    pub const PLUGIN_ERROR: i64 = -32000;
    /// The activation id is unknown or already deactivated.
    pub const ACTIVATION_NOT_LIVE: i64 = -32001;
    /// The host forgot the session (its connection closed).
    pub const SESSION_UNKNOWN: i64 = -32002;
    /// The message type is not in the plugin's `handles`/`sends`.
    pub const TYPE_NOT_ALLOWED: i64 = -32003;
    /// No sender or binding exists for the message on that protocol.
    pub const NO_BINDING: i64 = -32004;
    /// A declared service has no available provider.
    pub const SERVICE_UNAVAILABLE: i64 = -32005;
    /// The selected host provider failed.
    pub const SERVICE_ERROR: i64 = -32010;
    /// Unknown method.
    pub const METHOD_NOT_FOUND: i64 = -32601;
    /// Malformed parameters.
    pub const INVALID_PARAMS: i64 = -32602;
    /// SDK or transport failure.
    pub const INTERNAL_ERROR: i64 = -32603;
}

/// A JSON-RPC error: returned by host calls, and sent to the host for a failed request.
#[derive(Debug, Clone)]
pub struct RpcError {
    /// One of [`codes`].
    pub code: i64,
    /// Human-readable text.
    pub message: String,
    /// Optional structured detail.
    pub data: Option<Value>,
}

impl RpcError {
    /// Builds an error without data.
    pub fn new(code: i64, message: impl Into<String>) -> Self {
        RpcError { code, message: message.into(), data: None }
    }
}

impl std::fmt::Display for RpcError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "rpc error {}: {}", self.code, self.message)
    }
}

impl std::error::Error for RpcError {}

/// The error type plugin code returns. An [`RpcError`] keeps its code; anything else becomes a plugin error.
pub type Error = Box<dyn std::error::Error + Send + Sync>;

fn to_rpc(error: &Error) -> RpcError {
    match error.downcast_ref::<RpcError>() {
        Some(rpc) => rpc.clone(),
        None => RpcError::new(codes::PLUGIN_ERROR, error.to_string()),
    }
}

/// One config schema entry: `kind` is string, int, bool, number, list or map. The host validates the row config
/// against it and applies defaults before `activate`.
#[derive(Debug, Clone)]
pub struct Field {
    kind: String,
    default: Option<Value>,
}

/// A config field the row must set.
pub fn required(kind: &str) -> Field {
    Field { kind: kind.to_string(), default: None }
}

/// A config field with a default value.
pub fn optional(kind: &str, default: Value) -> Field {
    Field { kind: kind.to_string(), default: Some(default) }
}

/// What `describe` reports for one plugin. `handles` and `sends` name `IMessage` records from
/// BubbleFighter.Fantasy.Contracts by short name. Protocol v1 allows only `IMessageRouter` in `requires` (always
/// added) and nothing in `provides`.
#[derive(Debug, Clone, Default)]
pub struct Definition {
    /// Plugin id that rows name.
    pub id: String,
    /// Release version, parseable as a .NET version.
    pub version: String,
    /// Free text for plugin listings.
    pub description: String,
    /// Build ranges such as `kr:812`; empty means build independent.
    pub supported_builds: Vec<String>,
    /// `legacy` and/or `modern`; empty means any family.
    pub supported_families: Vec<String>,
    /// Required host contracts by short interface name.
    pub requires: Vec<String>,
    /// Optional host contracts.
    pub optional: Vec<String>,
    /// Singleton interfaces implemented by this plugin.
    pub provides: Vec<String>,
    /// Interfaces published as explicit multiple registrations.
    pub provides_many: Vec<String>,
    /// Decoratable singleton interfaces wrapped by this plugin.
    pub decorates: Vec<String>,
    /// Ordered extension interfaces requiring at least one provider.
    pub requires_many: Vec<String>,
    /// Ordered extension interfaces allowing an empty selection.
    pub optional_many: Vec<String>,
    /// Event operations registered in the native bus.
    pub subscribes: Vec<EventSpec>,
    /// Event operations this plugin may publish.
    pub publishes: Vec<EventSpec>,
    /// Message types handled.
    pub handles: Vec<String>,
    /// Message types sent.
    pub sends: Vec<String>,
    /// Explicit handler replacement targets in `row_id:fully.qualified.MessageType` form.
    pub overrides: Vec<String>,
    /// Message types for which middleware may be installed.
    pub middleware: Vec<String>,
    /// Config schema, by snake_case key.
    pub config: Vec<(String, Field)>,
}

impl Definition {
    fn wire(&self) -> Value {
        let mut config = Map::new();
        for (name, field) in &self.config {
            let entry = match &field.default {
                None => json!({"type": field.kind, "required": true}),
                Some(default) => json!({"type": field.kind, "default": default}),
            };
            config.insert(name.clone(), entry);
        }
        let mut value = json!({
            "id": self.id, "version": self.version, "description": self.description,
            "supported_builds": self.supported_builds, "supported_families": self.supported_families,
            "requires": self.requires, "optional": self.optional, "provides": self.provides,
            "handles": self.handles, "sends": self.sends, "config": config,
        });
        if !self.provides_many.is_empty() { value["provides_many"] = json!(self.provides_many); }
        if !self.decorates.is_empty() { value["decorates"] = json!(self.decorates); }
        if !self.requires_many.is_empty() { value["requires_many"] = json!(self.requires_many); }
        if !self.optional_many.is_empty() { value["optional_many"] = json!(self.optional_many); }
        if !self.subscribes.is_empty() { value["subscribes"] = json!(self.subscribes.iter().map(EventSpec::wire).collect::<Vec<_>>()); }
        if !self.publishes.is_empty() { value["publishes"] = json!(self.publishes.iter().map(EventSpec::wire).collect::<Vec<_>>()); }
        if !self.overrides.is_empty() { value["overrides"] = json!(self.overrides); }
        if !self.middleware.is_empty() { value["middleware"] = json!(self.middleware); }
        value
    }
}

/// One activation of a definition.
pub trait Plugin: Send {
    /// Registers handlers and effects. An error fails the row after the SDK released what was registered.
    fn activate(&mut self, ctx: &Context) -> Result<(), Error>;
    /// Runs before the activation's effects are released.
    fn deactivate(&mut self, _ctx: &Context) -> Result<(), Error> {
        Ok(())
    }
}

/// The client connection a delivered message came from.
#[derive(Debug, Clone)]
pub struct Session {
    /// Opaque session id; pass it to [`Context::send`].
    pub id: String,
    /// Protocol name, for example `kr-812/game`.
    pub protocol: String,
    /// User number, or `None` before login.
    pub player: Option<i64>,
    /// `region:major:resource`.
    pub build: String,
}

/// A message handler: the session and the message body (a JSON object).
pub type Handler = Arc<dyn Fn(&Session, &Map<String, Value>) -> Result<(), Error> + Send + Sync>;

/// The single-use continuation passed to a message middleware callback.
pub type MessageNext = Arc<dyn Fn() -> Result<(), Error> + Send + Sync>;

/// A message middleware callback. The lifetime signal is set when the host cancels the invocation.
pub type MessageMiddleware = Arc<dyn Fn(&Session, &Map<String, Value>, MessageNext, Lifetime) -> Result<(), Error> + Send + Sync>;

type Effect = Box<dyn FnOnce() -> Result<(), Error> + Send>;

/// A clonable, read-only activation cancellation signal; cancellation is not completed cleanup.
#[derive(Clone, Default)]
pub struct Lifetime(Arc<AtomicBool>);

impl Lifetime {
    /// Reports that this activation is stopping. WASM receives notifications while reading host calls.
    pub fn is_cancelled(&self) -> bool { self.0.load(Ordering::SeqCst) }

    fn cancel(&self) { self.0.store(true, Ordering::SeqCst); }
}

struct Activation {
    id: String,
    lifetime: Lifetime,
    live: AtomicBool,
    handlers: Mutex<HashMap<String, Handler>>,
    middleware: Mutex<HashMap<String, MessageMiddleware>>,
    providers: Mutex<HashMap<String, HashMap<String, ServiceHandler>>>,
    event_callbacks: Mutex<HashMap<String, EventHandler>>,
    task_callbacks: Mutex<HashMap<String, tasks::TaskHandler>>,
    resource_callbacks: Mutex<HashMap<String, resources::ResourceHandler>>,
    max_provider_callbacks: usize,
    next_provider: AtomicUsize,
    next_task: AtomicUsize,
    next_resource: AtomicUsize,
    effects: Mutex<Vec<Effect>>,
}

impl Activation {
    /// Breaks callback/context cycles after admission closes; running callbacks hold their own Arc.
    fn release_callbacks(&self) {
        self.release_non_resource_callbacks();
        let resources = std::mem::take(&mut *self.resource_callbacks.lock().unwrap());
        drop(resources);
    }

    /// Drops callbacks that cannot be invoked after admission closes while retaining resource releases.
    fn release_non_resource_callbacks(&self) {
        let handlers = std::mem::take(&mut *self.handlers.lock().unwrap());
        let middleware = std::mem::take(&mut *self.middleware.lock().unwrap());
        let providers = std::mem::take(&mut *self.providers.lock().unwrap());
        let events = std::mem::take(&mut *self.event_callbacks.lock().unwrap());
        let tasks = std::mem::take(&mut *self.task_callbacks.lock().unwrap());
        // User captures may run destructors that reenter the SDK; drop them outside registry locks.
        drop((handlers, middleware, providers, events, tasks));
    }

    /// Releases effects in reverse registration order and returns every failure.
    fn dispose_effects(&self) -> Vec<RpcError> {
        let effects: Vec<Effect> = std::mem::take(&mut *self.effects.lock().unwrap());
        let mut failures = Vec::new();
        for effect in effects.into_iter().rev() {
            if let Err(e) = guarded(effect) {
                failures.push(e);
            }
        }
        failures
    }
}

/// What `activate` receives: the validated config and the host services the negotiated protocol offers. Cheap to clone;
/// handlers may capture a clone.
#[derive(Clone)]
pub struct Context {
    /// Row config after host validation, with defaults applied.
    pub config: Map<String, Value>,
    /// The row id.
    pub row_id: String,
    /// The plugin id.
    pub plugin: String,
    /// Whether the host asked for a code refresh.
    pub reload: bool,
    rt: Arc<Runtime>,
    state: Arc<Activation>,
}

/// A registration returned by [`Context::on`]; disposing it unregisters the handler on the host.
pub struct Handle {
    /// The host's handle id.
    pub id: String,
    rt: Arc<Runtime>,
    state: Arc<Activation>,
}

impl Handle {
    /// Unregisters the handler.
    pub fn dispose(self) -> Result<(), Error> {
        self.dispose_in_event(None)
    }

    fn dispose_in_event(self, invocation: Option<&str>) -> Result<(), Error> {
        self.state.handlers.lock().unwrap().remove(&self.id);
        self.state.middleware.lock().unwrap().remove(&self.id);
        let mut params = json!({"activation": self.state.id, "handle": self.id});
        if let Some(id) = invocation { params["event_invocation"] = json!(id); }
        self.rt.call("effect.dispose", params)?;
        Ok(())
    }
}

impl Context {
    /// Returns the signal cancelled at stop, rollback or transport loss, before cleanup hooks run.
    pub fn lifetime(&self) -> Lifetime { self.state.lifetime.clone() }

    /// Reports whether the host can signal stop before requesting deactivation; older hosts cannot.
    pub fn supports_lifetime_cancellation(&self) -> bool { self.rt.early_lifetime.load(Ordering::SeqCst) }

    /// Returns the native-owned resource scope factory for this activation.
    pub fn resources(&self) -> Resources { Resources { rt: self.rt.clone(), state: self.state.clone() } }

    /// Calls a declared host service by method name or full inventory id. Requires a v2 host.
    /// JSON integer values retain their signed or unsigned 64-bit precision.
    pub fn call_service(&self, service: &str, method: &str, args: Value) -> Result<Value, Error> {
        if !self.state.live.load(Ordering::SeqCst) {
            return Err(Box::new(RpcError::new(codes::ACTIVATION_NOT_LIVE, "the activation is not live")));
        }
        if self.rt.protocol_version.load(Ordering::SeqCst) < 2 {
            return Err(Box::new(RpcError::new(codes::METHOD_NOT_FOUND, "services.call requires protocol v2")));
        }
        Ok(self.rt.call("services.call", json!({"activation": self.state.id, "service": service, "method": method, "args": args}))?)
    }

    /// A string config value. Panics when the key is missing or not a string, which host validation rules out
    /// for declared fields.
    pub fn string(&self, key: &str) -> String {
        self.config.get(key).and_then(Value::as_str).unwrap_or_else(|| panic!("config {key:?} is not a string")).to_string()
    }

    /// A list config value whose items are strings.
    pub fn strings(&self, key: &str) -> Vec<String> {
        self.config
            .get(key)
            .and_then(Value::as_array)
            .map(|items| items.iter().map(|v| v.as_str().unwrap_or_else(|| panic!("config {key:?} has a non-string item")).to_string()).collect())
            .unwrap_or_default()
    }

    /// Registers `handler` for `message_type` on `protocol` and returns its handle.
    pub fn on<F>(&self, protocol: &str, message_type: &str, handler: F) -> Result<Handle, Error>
    where
        F: Fn(&Session, &Map<String, Value>) -> Result<(), Error> + Send + Sync + 'static,
    {
        let result = self.rt.call("messages.on", json!({"activation": self.state.id, "protocol": protocol, "type": message_type}))?;
        let id = result
            .get("handle")
            .and_then(Value::as_str)
            .ok_or_else(|| RpcError::new(codes::INTERNAL_ERROR, "messages.on returned no handle"))?
            .to_string();
        let mut handlers = self.state.handlers.lock().unwrap();
        if !self.state.live.load(Ordering::SeqCst) {
            return Err(Box::new(RpcError::new(codes::ACTIVATION_NOT_LIVE, "the activation is not live")));
        }
        handlers.insert(id.clone(), Arc::new(handler));
        Ok(Handle { id, rt: self.rt.clone(), state: self.state.clone() })
    }

    /// Replaces a declared handler owned by `target_row_id`. The target must be listed in `Definition::overrides`.
    pub fn override_handler<F>(&self, protocol: &str, message_type: &str, target_row_id: &str, handler: F) -> Result<Handle, Error>
    where
        F: Fn(&Session, &Map<String, Value>) -> Result<(), Error> + Send + Sync + 'static,
    {
        let result = self.rt.call("messages.override", json!({
            "activation": self.state.id, "protocol": protocol, "type": message_type, "target_row_id": target_row_id,
        }))?;
        let id = result
            .get("handle")
            .and_then(Value::as_str)
            .ok_or_else(|| RpcError::new(codes::INTERNAL_ERROR, "messages.override returned no handle"))?
            .to_string();
        let mut handlers = self.state.handlers.lock().unwrap();
        if !self.state.live.load(Ordering::SeqCst) {
            return Err(Box::new(RpcError::new(codes::ACTIVATION_NOT_LIVE, "the activation is not live")));
        }
        handlers.insert(id.clone(), Arc::new(handler));
        Ok(Handle { id, rt: self.rt.clone(), state: self.state.clone() })
    }

    /// Registers middleware for a declared message type. The continuation is single-use and higher priorities run outermost.
    pub fn use_middleware<F>(&self, protocol: &str, message_type: &str, priority: i32, middleware: F) -> Result<Handle, Error>
    where
        F: Fn(&Session, &Map<String, Value>, MessageNext, Lifetime) -> Result<(), Error> + Send + Sync + 'static,
    {
        let result = self.rt.call("messages.use", json!({
            "activation": self.state.id, "protocol": protocol, "type": message_type, "priority": priority,
        }))?;
        let id = result
            .get("handle")
            .and_then(Value::as_str)
            .ok_or_else(|| RpcError::new(codes::INTERNAL_ERROR, "messages.use returned no handle"))?
            .to_string();
        let mut registrations = self.state.middleware.lock().unwrap();
        if !self.state.live.load(Ordering::SeqCst) {
            return Err(Box::new(RpcError::new(codes::ACTIVATION_NOT_LIVE, "the activation is not live")));
        }
        registrations.insert(id.clone(), Arc::new(middleware));
        Ok(Handle { id, rt: self.rt.clone(), state: self.state.clone() })
    }

    /// Sends `message_type` with `body` to the session on `protocol`.
    pub fn send(&self, protocol: &str, session_id: &str, message_type: &str, body: Value) -> Result<(), Error> {
        self.rt.call(
            "messages.send",
            json!({"activation": self.state.id, "protocol": protocol, "session_id": session_id, "type": message_type, "body": body}),
        )?;
        Ok(())
    }

    /// Registers a handle to release on deactivate (or when activate fails). Effects run in reverse order.
    pub fn effect(&self, handle: Handle) {
        self.effect_fn(move || handle.dispose());
    }

    /// Registers a release function, or runs it immediately if teardown has already closed admission.
    pub fn effect_fn<F: FnOnce() -> Result<(), Error> + Send + 'static>(&self, release: F) {
        let mut effects = self.state.effects.lock().unwrap();
        if self.state.live.load(Ordering::SeqCst) {
            effects.push(Box::new(release));
        } else {
            drop(effects);
            if let Err(error) = guarded(release) {
                diag(&format!("bff-plugin: late effect cleanup failed: {error}"));
            }
        }
    }

    /// Logs through the row's host logger. `level` is trace, debug, info, warn or error; `fields` may be empty.
    pub fn log(&self, level: &str, message: &str, fields: Map<String, Value>) {
        let mut params = json!({"activation": self.state.id, "level": level, "message": message});
        if !fields.is_empty() {
            params["fields"] = Value::Object(fields);
        }
        self.rt.notify("log", params);
    }

    /// Logs at info level without fields.
    pub fn info(&self, message: &str) {
        self.log("info", message, Map::new());
    }

    /// Logs at warn level without fields.
    pub fn warn(&self, message: &str) {
        self.log("warn", message, Map::new());
    }

    /// Logs at error level without fields.
    pub fn error(&self, message: &str) {
        self.log("error", message, Map::new());
    }
}

type Factory = Box<dyn Fn() -> Box<dyn Plugin> + Send + Sync>;

/// Collects plugin definitions and serves them.
#[derive(Default)]
pub struct Server {
    specs: Vec<(Definition, Factory)>,
}

impl Server {
    /// An empty server.
    pub fn new() -> Self {
        Server::default()
    }

    /// Adds a plugin; `factory` makes a fresh instance per activation. Panics on an invalid definition.
    pub fn register<F, P>(mut self, mut def: Definition, factory: F) -> Self
    where
        F: Fn() -> P + Send + Sync + 'static,
        P: Plugin + 'static,
    {
        assert!(!def.id.is_empty() && !def.version.is_empty(), "a definition needs an id and a version");
        assert!(self.specs.iter().all(|(d, _)| d.id != def.id), "plugin {} registered twice", def.id);
        if !def.requires.iter().any(|r| r == "IMessageRouter") {
            def.requires.insert(0, "IMessageRouter".to_string());
        }
        self.specs.push((def, Box::new(move || Box::new(factory()) as Box<dyn Plugin>)));
        self
    }

    /// Serves stdin/stdout until `shutdown` or end of input; returns the process exit code (0 clean, 2 on a
    /// protocol violation). Use as `std::process::exit(server.run())`.
    pub fn run(self) -> i32 {
        let rt = Arc::new(Runtime {
            out: Mutex::new(std::io::stdout()),
            next_id: AtomicI64::new(0),
            protocol_version: AtomicI64::new(1),
            early_lifetime: AtomicBool::new(false),
            pending: Mutex::new(HashMap::new()),
            active: Mutex::new(HashMap::new()),
            retired: Mutex::new(HashMap::new()),
            instances: Mutex::new(HashMap::new()),
            service_calls: Mutex::new(HashMap::new()),
            event_calls: Mutex::new(HashMap::new()), task_calls: Mutex::new(HashMap::new()), resource_calls: Mutex::new(HashMap::new()), message_calls: Mutex::new(HashMap::new()),
            specs: self.specs,
            closed: AtomicBool::new(false),
            slots: Slots(Mutex::new(0)),
            max_in_flight: AtomicUsize::new(64),
            max_reentry_depth: AtomicUsize::new(8),
            exit_code: AtomicI64::new(0),
        });
        #[cfg(target_arch = "wasm32")]
        let code = rt.serve_inline();
        #[cfg(not(target_arch = "wasm32"))]
        let code = rt.serve_stdio();
        rt.shutdown();
        code
    }
}

impl Runtime {
    #[cfg(not(target_arch = "wasm32"))]
    fn serve_stdio(self: &Arc<Self>) -> i32 {
        let rt = self;
        let stdin = std::io::stdin();
        let mut reader = stdin.lock();
        let mut code = 0;
        loop {
            let mut line = Vec::new();
            match (&mut reader).take(MAX_LINE_BYTES as u64 + 2).read_until(b'\n', &mut line) {
                Ok(0) | Err(_) => break,
                Ok(_) => {}
            }
            if line.len() > MAX_LINE_BYTES + 1 {
                eprintln!("bff-plugin: a host line exceeded {MAX_LINE_BYTES} bytes; exiting");
                code = 2;
                break;
            }
            if line.iter().all(u8::is_ascii_whitespace) {
                continue;
            }
            let message: Map<String, Value> = match serde_json::from_slice(&line) {
                Ok(Value::Object(m)) => m,
                _ => {
                    eprintln!("bff-plugin: unparseable host line; exiting");
                    code = 2;
                    break;
                }
            };
            if rt.dispatch(message) {
                break;
            }
        }
        code
    }

    #[cfg(target_arch = "wasm32")]
    fn serve_inline(self: &Arc<Self>) -> i32 {
        loop {
            if self.closed.load(Ordering::SeqCst) {
                return self.exit_code.load(Ordering::SeqCst) as i32;
            }
            let message = match self.read_message() {
                Ok(Some(message)) => message,
                Ok(None) => return 0,
                Err(code) => return code,
            };
            if self.dispatch(message) {
                return 0;
            }
        }
    }

    /// Reads and parses one host line in a module; `Err` carries the exit code for a protocol violation.
    #[cfg(target_arch = "wasm32")]
    fn read_message(&self) -> Result<Option<Map<String, Value>>, i32> {
        let Some(line) = wasm_io::read_line() else { return Ok(None) };
        match serde_json::from_slice(&line) {
            Ok(Value::Object(m)) => Ok(Some(m)),
            _ => {
                diag("bff-plugin: unparseable host line; exiting");
                Err(2)
            }
        }
    }
}

/// Counts admitted requests before any thread is created; excess work never queues behind callbacks.
struct Slots(Mutex<usize>);

impl Slots {
    fn try_acquire(&self, limit: usize) -> bool {
        let mut used = self.0.lock().unwrap();
        if *used >= limit { return false; }
        *used += 1;
        true
    }
}

struct RequestSlot<'a>(&'a Slots);

impl Drop for RequestSlot<'_> {
    fn drop(&mut self) {
        *self.0.0.lock().unwrap() -= 1;
    }
}

#[cfg(not(target_arch = "wasm32"))]
fn write_locked<W: std::io::Write>(out: &Mutex<W>, data: &[u8], slot: Option<RequestSlot<'_>>) -> std::io::Result<()> {
    let mut out = out.lock().unwrap();
    // Blocked response producers retain their slots; only this single writer can be outside the request budget.
    drop(slot);
    out.write_all(data).and_then(|_| out.flush())
}

struct Runtime {
    protocol_version: AtomicI64,
    early_lifetime: AtomicBool,
    out: Mutex<std::io::Stdout>,
    next_id: AtomicI64,
    pending: Mutex<HashMap<i64, mpsc::SyncSender<Result<Value, RpcError>>>>,
    active: Mutex<HashMap<String, Arc<Activation>>>,
    retired: Mutex<HashMap<String, Arc<Activation>>>,
    instances: Mutex<HashMap<String, (Box<dyn Plugin>, Context)>>,
    service_calls: Mutex<HashMap<(String, String), Arc<ServiceCall>>>,
    event_calls: Mutex<HashMap<(String, String), Arc<EventCall>>>,
    task_calls: Mutex<HashMap<(String, String), Arc<TaskCall>>>,
    resource_calls: Mutex<HashMap<(String, String), Arc<resources::ResourceCall>>>,
    message_calls: Mutex<HashMap<(String, String), Lifetime>>,
    specs: Vec<(Definition, Factory)>,
    closed: AtomicBool,
    slots: Slots,
    max_in_flight: AtomicUsize,
    max_reentry_depth: AtomicUsize,
    #[cfg_attr(not(target_arch = "wasm32"), allow(dead_code))]
    exit_code: AtomicI64,
}

fn guarded<T, F: FnOnce() -> Result<T, Error>>(f: F) -> Result<T, RpcError> {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(v)) => Ok(v),
        Ok(Err(e)) => Err(to_rpc(&e)),
        Err(panic) => {
            let text = panic
                .downcast_ref::<&str>()
                .map(|s| s.to_string())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "plugin panicked".to_string());
            Err(RpcError::new(codes::PLUGIN_ERROR, text))
        }
    }
}

fn bounded_param(params: &Value, name: &str, fallback: usize, maximum: usize) -> Result<usize, RpcError> {
    match params.get(name) {
        None => Ok(fallback),
        Some(value) => value.as_u64().filter(|n| *n >= 1 && *n <= maximum as u64).map(|n| n as usize)
            .ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, format!("{name} must be an integer from 1 to {maximum}"))),
    }
}

fn param_str<'a>(params: &'a Value, name: &str) -> Result<&'a str, RpcError> {
    params
        .get(name)
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, format!("{name:?} must be a non-empty string")))
}

impl Runtime {
    fn write(&self, message: Value) -> Result<(), RpcError> {
        self.write_with_slot(message, None)
    }

    fn write_with_slot(&self, message: Value, slot: Option<RequestSlot<'_>>) -> Result<(), RpcError> {
        #[cfg_attr(target_arch = "wasm32", allow(unused_mut))]
        let mut data = serde_json::to_vec(&message).map_err(|e| RpcError::new(codes::INTERNAL_ERROR, e.to_string()))?;
        if data.len() > MAX_LINE_BYTES {
            return Err(RpcError::new(codes::INTERNAL_ERROR, format!("message of {} bytes exceeds the line limit", data.len())));
        }
        #[cfg(target_arch = "wasm32")]
        {
            let _ = &self.out;
            drop(slot);
            wasm_io::write_line(&data);
            return Ok(());
        }
        #[cfg(not(target_arch = "wasm32"))]
        data.push(b'\n');
        #[cfg(not(target_arch = "wasm32"))]
        return write_locked(&self.out, &data, slot).map_err(|e| RpcError::new(codes::INTERNAL_ERROR, e.to_string()));
    }

    fn call(self: &Arc<Self>, method: &str, params: Value) -> Result<Value, RpcError> {
        let id = self.next_id.fetch_add(1, Ordering::SeqCst) + 1;
        let (tx, rx) = mpsc::sync_channel(1);
        {
            let mut pending = self.pending.lock().unwrap();
            if self.closed.load(Ordering::SeqCst) {
                return Err(RpcError::new(codes::INTERNAL_ERROR, "the host went away"));
            }
            if pending.len() >= self.max_in_flight.load(Ordering::SeqCst) {
                return Err(RpcError::new(codes::INTERNAL_ERROR, "host call exceeds max_in_flight"));
            }
            pending.insert(id, tx);
        }
        if let Err(e) = self.write(json!({"jsonrpc": "2.0", "id": id, "method": method, "params": params})) {
            self.pending.lock().unwrap().remove(&id);
            return Err(e);
        }
        #[cfg(target_arch = "wasm32")]
        let result = self.wait_inline(&rx);
        #[cfg(not(target_arch = "wasm32"))]
        let result = rx.recv().unwrap_or_else(|_| Err(RpcError::new(codes::INTERNAL_ERROR, "the host went away")));
        self.pending.lock().unwrap().remove(&id);
        result
    }

    /// Serves callbacks while every response is routed to its own waiter, even if an outer response arrives first.
    #[cfg(target_arch = "wasm32")]
    fn wait_inline(self: &Arc<Self>, response: &mpsc::Receiver<Result<Value, RpcError>>) -> Result<Value, RpcError> {
        loop {
            match response.try_recv() {
                Ok(result) => return result,
                Err(mpsc::TryRecvError::Disconnected) => return Err(RpcError::new(codes::INTERNAL_ERROR, "the host went away")),
                Err(mpsc::TryRecvError::Empty) => {}
            }
            let message = match self.read_message() {
                Ok(Some(message)) => message,
                ended => {
                    if let Err(code) = ended { self.exit_code.store(code as i64, Ordering::SeqCst); }
                    self.shutdown();
                    continue;
                }
            };
            self.dispatch(message);
        }
    }

    fn shutdown(&self) {
        self.closed.store(true, Ordering::SeqCst);
        for state in self.active.lock().unwrap().values() { state.lifetime.cancel(); }
        for call in self.service_calls.lock().unwrap().values() { call.cancel(); }
        for call in self.event_calls.lock().unwrap().values() { call.cancel(); }
        for call in self.task_calls.lock().unwrap().values() { call.cancel(); }
        for call in self.resource_calls.lock().unwrap().values() { call.cancel(); }
        for call in self.message_calls.lock().unwrap().values() { call.cancel(); }
        for (_, sender) in self.pending.lock().unwrap().drain() {
            let _ = sender.send(Err(RpcError::new(codes::INTERNAL_ERROR, "the host went away")));
        }
    }

    fn prepare_message(&self, params: &Value) -> Result<(), RpcError> {
        let activation = param_str(params, "activation")?.to_string();
        let invocation = param_str(params, "invocation")?.to_string();
        let key = (activation, invocation);
        let mut calls = self.message_calls.lock().unwrap();
        if calls.contains_key(&key) { return Err(RpcError::new(codes::INVALID_PARAMS, "duplicate message middleware invocation")); }
        calls.insert(key, Lifetime::default());
        Ok(())
    }

    fn finish_message(&self, params: &Value) {
        let activation = params.get("activation").and_then(Value::as_str).unwrap_or("");
        let invocation = params.get("invocation").and_then(Value::as_str).unwrap_or("");
        self.message_calls.lock().unwrap().remove(&(activation.to_string(), invocation.to_string()));
    }

    fn cancel_message(&self, params: &Value) {
        let activation = params.get("activation").and_then(Value::as_str).unwrap_or("");
        let invocation = params.get("invocation").and_then(Value::as_str).unwrap_or("");
        if let Some(call) = self.message_calls.lock().unwrap().get(&(activation.to_string(), invocation.to_string())) { call.cancel(); }
    }

    fn notify(&self, method: &str, params: Value) {
        let _ = self.write(json!({"jsonrpc": "2.0", "method": method, "params": params}));
    }

    /// Routes one host line; returns true when the host asked the process to stop.
    fn dispatch(self: &Arc<Self>, message: Map<String, Value>) -> bool {
        if let Some(method) = message.get("method").and_then(Value::as_str) {
            let Some(id) = message.get("id").cloned() else {
                if method == "shutdown" { self.shutdown(); return true; }
                if method == "lifecycle.cancel" {
                    if let Some(id) = message.get("params").and_then(|p| p.get("activation")).and_then(Value::as_str) {
                        if let Some(state) = self.active.lock().unwrap().get(id) { state.lifetime.cancel(); }
                    }
                }
                if method == "services.cancel" { self.cancel_service(message.get("params").unwrap_or(&Value::Null)); }
                if method == "events.cancel" { self.cancel_event(message.get("params").unwrap_or(&Value::Null)); }
                if method == "tasks.cancel" { self.cancel_task(message.get("params").unwrap_or(&Value::Null)); }
                if method == "resources.cancel" { self.cancel_resource(message.get("params").unwrap_or(&Value::Null)); }
                if method == "messages.cancel" { self.cancel_message(message.get("params").unwrap_or(&Value::Null)); }
                return false;
            };
            let method = method.to_string();
            let params = message.get("params").cloned().unwrap_or_else(|| json!({}));
            let flight = self.max_in_flight.load(Ordering::SeqCst);
            let mut limit = if INLINE { flight.min(self.max_reentry_depth.load(Ordering::SeqCst)) } else { flight };
            if method == "deactivate" || method == "shutdown" {
                limit += CONTROL_RESERVE;
            }
            if self.closed.load(Ordering::SeqCst) || !self.slots.try_acquire(limit) {
                let _ = self.write(json!({"jsonrpc": "2.0", "id": id, "error": {
                    "code": codes::INTERNAL_ERROR, "message": "host request exceeds max_in_flight or max_reentry_depth"
                }}));
                return false;
            }
            let rt = self.clone();
            if method == "services.invoke" || method == "events.invoke" || method == "tasks.invoke" || method == "resources.invoke" {
                let prepared = if method == "events.invoke" { self.prepare_event(&params) } else if method == "tasks.invoke" { self.prepare_task(&params) } else if method == "resources.invoke" { self.prepare_resource(&params) } else { self.prepare_service(&params) };
                if let Err(error) = prepared {
                    let _ = self.write_with_slot(json!({"jsonrpc": "2.0", "id": id, "error": {"code": error.code, "message": error.message}}), Some(RequestSlot(&self.slots)));
                    return false;
                }
            }
            if method == "messages.invoke" {
                if let Err(error) = self.prepare_message(&params) {
                    let _ = self.write_with_slot(json!({"jsonrpc": "2.0", "id": id, "error": {"code": error.code, "message": error.message}}), Some(RequestSlot(&self.slots)));
                    return false;
                }
            }
            let work = move || {
                let slot = RequestSlot(&rt.slots);
                let reply = match rt.serve(&method, &params) {
                    Ok(result) => json!({"jsonrpc": "2.0", "id": id, "result": result}),
                    Err(e) => {
                        let mut body = json!({"code": e.code, "message": e.message});
                        if let Some(data) = e.data {
                            body["data"] = data;
                        }
                        json!({"jsonrpc": "2.0", "id": id, "error": body})
                    }
                };
                if method == "services.invoke" { rt.finish_service(&params); }
                if method == "events.invoke" { rt.finish_event(&params); }
                if method == "tasks.invoke" { rt.finish_task(&params); }
                if method == "resources.invoke" { rt.finish_resource(&params); }
                if method == "messages.invoke" { rt.finish_message(&params); }
                let _ = rt.write_with_slot(reply, Some(slot));
            };
            if INLINE {
                work();
            } else {
                std::thread::spawn(work);
            }
            return false;
        }
        let Some(id) = message.get("id").and_then(Value::as_i64) else {
            diag("bff-plugin: response without a numeric id ignored");
            return false;
        };
        let Some(sender) = self.pending.lock().unwrap().remove(&id) else {
            diag(&format!("bff-plugin: response to unknown request {id} ignored"));
            return false;
        };
        let outcome = match message.get("error") {
            Some(error) => Err(RpcError {
                code: error.get("code").and_then(Value::as_i64).unwrap_or(codes::INTERNAL_ERROR),
                message: error.get("message").and_then(Value::as_str).unwrap_or("").to_string(),
                data: error.get("data").cloned(),
            }),
            None => Ok(message.get("result").cloned().unwrap_or(Value::Null)),
        };
        let _ = sender.send(outcome);
        false
    }

    fn serve(self: &Arc<Self>, method: &str, params: &Value) -> Result<Value, RpcError> {
        match method {
            "initialize" => {
                let selected = if let Some(offered) = params.get("protocol_versions").and_then(Value::as_array) {
                    [2, 1].into_iter().find(|version| offered.iter().any(|v| v.as_i64() == Some(*version)))
                } else {
                    params.get("protocol_version").and_then(Value::as_i64).filter(|v| *v == 1 || *v == 2)
                };
                if selected.is_none() {
                    return Err(RpcError::new(codes::INVALID_PARAMS, format!("this SDK speaks protocol version {PROTOCOL_VERSION}")));
                }
                let flight = bounded_param(params, "max_in_flight", 64, 4096)?;
                let depth = bounded_param(params, "max_reentry_depth", 8, 64)?;
                self.max_in_flight.store(flight, Ordering::SeqCst);
                self.max_reentry_depth.store(depth, Ordering::SeqCst);
                self.protocol_version.store(selected.unwrap(), Ordering::SeqCst);
                self.early_lifetime.store(selected == Some(2) && params.get("features").and_then(Value::as_array)
                    .is_some_and(|features| features.iter().any(|f| f.as_str() == Some("lifecycle.cancel"))), Ordering::SeqCst);
                Ok(json!({"protocol_version": selected.unwrap(), "sdk": {"name": SDK_NAME, "version": env!("CARGO_PKG_VERSION")}}))
            }
            "describe" => Ok(json!({"definitions": self.specs.iter().map(|(d, _)| d.wire()).collect::<Vec<_>>()})),
            "activate" => self.activate(params),
            "deactivate" => self.deactivate(params),
            "deliver" => self.deliver(params),
            "services.invoke" => self.invoke_service(params),
            "events.invoke" => self.invoke_event(params),
            "tasks.invoke" => self.invoke_task(params),
            "resources.invoke" => self.invoke_resource(params),
            "messages.invoke" => self.invoke_message(params),
            _ => Err(RpcError::new(codes::METHOD_NOT_FOUND, format!("unknown method {method:?}"))),
        }
    }

    fn live(&self, params: &Value) -> Result<Arc<Activation>, RpcError> {
        let id = param_str(params, "activation")?;
        match self.active.lock().unwrap().get(id) {
            Some(state) if state.live.load(Ordering::SeqCst) => Ok(state.clone()),
            _ => Err(RpcError::new(codes::ACTIVATION_NOT_LIVE, format!("activation {id:?} is not live"))),
        }
    }

    fn activate(self: &Arc<Self>, params: &Value) -> Result<Value, RpcError> {
        let id = param_str(params, "activation")?.to_string();
        let plugin = param_str(params, "plugin")?.to_string();
        let Some((_, factory)) = self.specs.iter().find(|(d, _)| d.id == plugin) else {
            return Err(RpcError::new(codes::INVALID_PARAMS, format!("this process does not serve plugin {plugin:?}")));
        };
        let Some(config) = params.get("config").and_then(Value::as_object).cloned() else {
            return Err(RpcError::new(codes::INVALID_PARAMS, "'config' must be an object"));
        };
        let state = Arc::new(Activation {
            id: id.clone(),
            lifetime: Lifetime::default(),
            live: AtomicBool::new(true),
            handlers: Mutex::new(HashMap::new()),
            middleware: Mutex::new(HashMap::new()),
            providers: Mutex::new(HashMap::new()),
            event_callbacks: Mutex::new(HashMap::new()),
            task_callbacks: Mutex::new(HashMap::new()), resource_callbacks: Mutex::new(HashMap::new()),
            max_provider_callbacks: bounded_param(params, "max_provider_callbacks", 64, 2147483647)?,
            next_provider: AtomicUsize::new(0),
            next_task: AtomicUsize::new(0), next_resource: AtomicUsize::new(0),
            effects: Mutex::new(Vec::new()),
        });
        {
            let mut active = self.active.lock().unwrap();
            if self.closed.load(Ordering::SeqCst) {
                return Err(RpcError::new(codes::ACTIVATION_NOT_LIVE, "the host went away"));
            }
            if active.contains_key(&id) {
                return Err(RpcError::new(codes::INVALID_PARAMS, format!("activation {id:?} is already active")));
            }
            active.insert(id.clone(), state.clone());
        }
        let ctx = Context {
            config,
            row_id: params.get("row_id").and_then(Value::as_str).unwrap_or("").to_string(),
            plugin,
            reload: params.get("reload").and_then(Value::as_bool).unwrap_or(false),
            rt: self.clone(),
            state: state.clone(),
        };
        let activated = guarded(|| {
            if self.early_lifetime.load(Ordering::SeqCst) {
                self.call("lifecycle.ready", json!({"activation": id}))?;
            }
            if state.lifetime.is_cancelled() {
                return Err(Box::new(RpcError::new(codes::ACTIVATION_NOT_LIVE, "the activation is stopping")) as Error);
            }
            let mut instance = factory();
            instance.activate(&ctx)?;
            Ok(instance)
        });
        let instance = match activated {
            Ok(instance) => instance,
            Err(e) => {
                state.lifetime.cancel();
                // A failed activation leaves nothing behind: release what it registered, then report the failure.
                state.live.store(false, Ordering::SeqCst);
                for failure in state.dispose_effects() {
                    diag(&format!("bff-plugin: rollback after failed activate also failed: {failure}"));
                }
                state.release_callbacks();
                self.active.lock().unwrap().remove(&id);
                return Err(e);
            }
        };
        self.instances.lock().unwrap().insert(id, (instance, ctx));
        Ok(json!({}))
    }

    fn deactivate(&self, params: &Value) -> Result<Value, RpcError> {
        let state = self.live(params)?;
        state.live.store(false, Ordering::SeqCst);
        state.lifetime.cancel();
        let mut failures = Vec::new();
        // A hook can call the host and reenter this runtime; release the instance map before invoking it.
        let instance = self.instances.lock().unwrap().remove(&state.id);
        if let Some((mut instance, ctx)) = instance {
            if let Err(e) = guarded(|| instance.deactivate(&ctx)) {
                failures.push(e);
            }
        }
        failures.extend(state.dispose_effects());
        self.active.lock().unwrap().remove(&state.id);
        if state.resource_callbacks.lock().unwrap().is_empty() {
            state.release_callbacks();
        } else {
            state.release_non_resource_callbacks();
            self.retired.lock().unwrap().insert(state.id.clone(), state.clone());
        }
        match failures.first() {
            None => Ok(json!({})),
            Some(first) => Err(RpcError::new(codes::PLUGIN_ERROR, format!("{} failure(s) while deactivating; first: {first}", failures.len()))),
        }
    }

    fn invoke_message(self: &Arc<Self>, params: &Value) -> Result<Value, RpcError> {
        let state = self.live(params)?;
        let handle = param_str(params, "handle")?;
        let middleware = state.middleware.lock().unwrap().get(handle).cloned()
            .ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, format!("unknown middleware callback {handle:?}")))?;
        let session_value = params.get("session").and_then(Value::as_object)
            .ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "'session' must be an object"))?;
        let body = params.get("message").and_then(|m| m.get("body")).and_then(Value::as_object)
            .ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "'message.body' must be an object"))?;
        let session = Session {
            id: session_value.get("id").and_then(Value::as_str).unwrap_or("").to_string(),
            protocol: session_value.get("protocol").and_then(Value::as_str).unwrap_or("").to_string(),
            player: session_value.get("player").and_then(Value::as_i64),
            build: session_value.get("build").and_then(Value::as_str).unwrap_or("").to_string(),
        };
        let activation = param_str(params, "activation")?.to_string();
        let invocation = param_str(params, "invocation")?.to_string();
        let signal = self.message_calls.lock().unwrap().get(&(activation.clone(), invocation.clone())).cloned()
            .ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, "message middleware invocation is not admitted"))?;
        let used = Arc::new(AtomicBool::new(false));
        let rt = self.clone();
        let next: MessageNext = Arc::new(move || {
            if used.swap(true, Ordering::SeqCst) { return Err(Box::new(RpcError::new(codes::INVALID_PARAMS, "message middleware next is single-use"))); }
            rt.call("messages.next", json!({"activation": activation, "invocation": invocation})).map(|_| ()).map_err(|e| Box::new(e) as Error)
        });
        guarded(|| middleware(&session, body, next, signal)).map(|_| json!({}))
    }

    fn deliver(&self, params: &Value) -> Result<Value, RpcError> {
        let state = self.live(params)?;
        let handle = param_str(params, "handle")?;
        let handler = state
            .handlers
            .lock()
            .unwrap()
            .get(handle)
            .cloned()
            .ok_or_else(|| RpcError::new(codes::INVALID_PARAMS, format!("unknown handle {handle:?}")))?;
        let session = params.get("session").and_then(Value::as_object);
        let body = params.get("message").and_then(|m| m.get("body")).and_then(Value::as_object);
        let (Some(session), Some(body)) = (session, body) else {
            return Err(RpcError::new(codes::INVALID_PARAMS, "'session' and 'message' {type, body} must be objects"));
        };
        let session = Session {
            id: session.get("id").and_then(Value::as_str).unwrap_or("").to_string(),
            protocol: session.get("protocol").and_then(Value::as_str).unwrap_or("").to_string(),
            player: session.get("player").and_then(Value::as_i64),
            build: session.get("build").and_then(Value::as_str).unwrap_or("").to_string(),
        };
        guarded(|| handler(&session, body)).map(|_| json!({"handled": true}))
    }
}

#[cfg(all(test, not(target_arch = "wasm32")))]
mod runtime_tests {
    use super::*;
    use std::io::{self, Write};
    use std::time::Duration;

    struct CapturingPlugin {
        observed: Arc<Mutex<Option<std::sync::Weak<Activation>>>>,
        fail_activate: bool,
    }

    impl Plugin for CapturingPlugin {
        fn activate(&mut self, ctx: &Context) -> Result<(), Error> {
            *self.observed.lock().unwrap() = Some(Arc::downgrade(&ctx.state));
            let message_context = ctx.clone();
            ctx.state.handlers.lock().unwrap().insert("message".into(), Arc::new(move |_, _| {
                let _keep_alive = &message_context;
                Ok(())
            }));
            let provider_context = ctx.clone();
            let provider: ServiceHandler = Arc::new(move |_| {
                let _keep_alive = &provider_context;
                Ok(Value::Null)
            });
            ctx.state.providers.lock().unwrap().insert("provider".into(), HashMap::from([("method".into(), provider)]));
            let event_context = ctx.clone();
            ctx.state.event_callbacks.lock().unwrap().insert("event".into(), Arc::new(move |_| {
                let _keep_alive = &event_context;
                Ok(Value::Null)
            }));
            let effect_context = ctx.clone();
            ctx.effect_fn(move || {
                assert!(effect_context.lifetime().is_cancelled(), "cleanup started before cancellation");
                let _keep_alive = effect_context;
                Err("cleanup failed".into())
            });
            if self.fail_activate { Err("activation failed".into()) } else { Ok(()) }
        }

        fn deactivate(&mut self, ctx: &Context) -> Result<(), Error> {
            assert!(ctx.lifetime().is_cancelled(), "hook started before cancellation");
            Err("hook failed".into())
        }
    }

    #[test]
    fn cancellation_is_scoped_and_shutdown_signals_all_remaining_activations() {
        let rt = Arc::new(Runtime {
            early_lifetime: AtomicBool::new(false), protocol_version: AtomicI64::new(2), out: Mutex::new(std::io::stdout()),
            next_id: AtomicI64::new(0), pending: Mutex::new(HashMap::new()),
            active: Mutex::new(HashMap::new()), retired: Mutex::new(HashMap::new()), instances: Mutex::new(HashMap::new()),
            service_calls: Mutex::new(HashMap::new()), event_calls: Mutex::new(HashMap::new()), task_calls: Mutex::new(HashMap::new()), resource_calls: Mutex::new(HashMap::new()), message_calls: Mutex::new(HashMap::new()),
            specs: vec![], closed: AtomicBool::new(false), slots: Slots(Mutex::new(0)),
            max_in_flight: AtomicUsize::new(4), max_reentry_depth: AtomicUsize::new(4), exit_code: AtomicI64::new(0),
        });
        let lifetimes: Vec<Lifetime> = ["a", "b"].into_iter().map(|id| {
            let state = Arc::new(Activation {
                id: id.into(), lifetime: Lifetime::default(), live: AtomicBool::new(true),
                handlers: Mutex::new(HashMap::new()), middleware: Mutex::new(HashMap::new()), providers: Mutex::new(HashMap::new()),
                event_callbacks: Mutex::new(HashMap::new()), max_provider_callbacks: 4,
                task_callbacks: Mutex::new(HashMap::new()), resource_callbacks: Mutex::new(HashMap::new()), next_provider: AtomicUsize::new(0), next_task: AtomicUsize::new(0), next_resource: AtomicUsize::new(0), effects: Mutex::new(Vec::new()),
            });
            rt.active.lock().unwrap().insert(id.into(), state.clone());
            state.lifetime.clone()
        }).collect();
        for id in ["missing", "a", "a"] {
            let message = json!({"method": "lifecycle.cancel", "params": {"activation": id}});
            assert!(!rt.dispatch(message.as_object().unwrap().clone()));
        }
        assert!(lifetimes[0].is_cancelled());
        assert!(!lifetimes[1].is_cancelled());
        assert_eq!(rt.active.lock().unwrap().len(), 2);
        rt.shutdown();
        assert!(lifetimes[1].is_cancelled());
    }

    #[test]
    fn shared_runtime_reclaims_callback_cycles_after_failed_activation_or_teardown() {
        for fail_activate in [false, true] {
            let observed = Arc::new(Mutex::new(None));
            let factory_observed = observed.clone();
            let rt = Arc::new(Runtime {
                early_lifetime: AtomicBool::new(false), protocol_version: AtomicI64::new(2), out: Mutex::new(std::io::stdout()),
                next_id: AtomicI64::new(0), pending: Mutex::new(HashMap::new()),
                active: Mutex::new(HashMap::new()), retired: Mutex::new(HashMap::new()), instances: Mutex::new(HashMap::new()),
                service_calls: Mutex::new(HashMap::new()), event_calls: Mutex::new(HashMap::new()), task_calls: Mutex::new(HashMap::new()), resource_calls: Mutex::new(HashMap::new()), message_calls: Mutex::new(HashMap::new()),
                specs: vec![(Definition { id: "lifetime".into(), ..Default::default() }, Box::new(move || {
                    Box::new(CapturingPlugin { observed: factory_observed.clone(), fail_activate })
                }))],
                closed: AtomicBool::new(false), slots: Slots(Mutex::new(0)),
                max_in_flight: AtomicUsize::new(4), max_reentry_depth: AtomicUsize::new(4), exit_code: AtomicI64::new(0),
            });
            // Repeated activations must reclaim their captures while the shared runtime stays alive.
            for index in 0..3 {
                let id = format!("row-{index}");
                let result = rt.activate(&json!({"activation": id, "plugin": "lifetime", "config": {}}));
                assert_eq!(result.is_err(), fail_activate);
                let weak = observed.lock().unwrap().clone().unwrap();
                if !fail_activate {
                    let state = weak.upgrade().unwrap();
                    let admitted = state.event_callbacks.lock().unwrap().get("event").cloned().unwrap();
                    let context = rt.instances.lock().unwrap().get(&id).unwrap().1.clone();
                    assert!(rt.deactivate(&json!({"activation": id})).is_err());
                    assert!(state.handlers.lock().unwrap().is_empty());
                    assert!(state.providers.lock().unwrap().is_empty());
                    assert!(state.event_callbacks.lock().unwrap().is_empty());
                    let late = Arc::new(AtomicBool::new(false));
                    let late_copy = late.clone();
                    context.effect_fn(move || { late_copy.store(true, Ordering::SeqCst); Ok(()) });
                    assert!(late.load(Ordering::SeqCst));
                    drop((state, context));
                    assert!(weak.upgrade().is_some(), "admitted work must retain its own captures");
                    drop(admitted);
                }
                assert!(weak.upgrade().is_none(), "callback captures leaked the stopped activation");
                assert!(rt.active.lock().unwrap().is_empty());
                assert!(rt.instances.lock().unwrap().is_empty());
            }
        }
    }

    struct GatedWriter {
        entered: mpsc::SyncSender<()>,
        release: mpsc::Receiver<()>,
        first: bool,
    }

    impl Write for GatedWriter {
        fn write(&mut self, data: &[u8]) -> io::Result<usize> {
            if self.first {
                self.first = false;
                self.entered.send(()).unwrap();
                self.release.recv_timeout(Duration::from_secs(2)).unwrap();
            }
            Ok(data.len())
        }
        fn flush(&mut self) -> io::Result<()> { Ok(()) }
    }

    #[test]
    fn blocked_response_producers_retain_admission_until_they_own_output() {
        let (entered_tx, entered_rx) = mpsc::sync_channel(1);
        let (release_tx, release_rx) = mpsc::sync_channel(1);
        let output = Arc::new(Mutex::new(GatedWriter { entered: entered_tx, release: release_rx, first: true }));
        let slots = Arc::new(Slots(Mutex::new(0)));
        assert!(slots.try_acquire(1));
        let first_output = output.clone();
        let first_slots = slots.clone();
        let first = std::thread::spawn(move || write_locked(&first_output, b"first\n", Some(RequestSlot(&first_slots))));
        entered_rx.recv_timeout(Duration::from_secs(2)).unwrap();
        assert!(slots.try_acquire(1), "the visible response must already have refunded its slot");
        let second_output = output.clone();
        let second_slots = slots.clone();
        let (done_tx, done_rx) = mpsc::sync_channel(1);
        let second = std::thread::spawn(move || {
            let result = write_locked(&second_output, b"second\n", Some(RequestSlot(&second_slots)));
            done_tx.send(()).unwrap();
            result
        });
        assert!(matches!(done_rx.recv_timeout(Duration::from_millis(50)), Err(mpsc::RecvTimeoutError::Timeout)));
        let still_reserved = *slots.0.lock().unwrap();
        release_tx.send(()).unwrap();
        first.join().unwrap().unwrap();
        second.join().unwrap().unwrap();
        assert_eq!(still_reserved, 1, "a response waiting for output released its admission");
        assert_eq!(*slots.0.lock().unwrap(), 0);
    }

    struct FailingWriter;
    impl Write for FailingWriter {
        fn write(&mut self, _: &[u8]) -> io::Result<usize> { Err(io::ErrorKind::BrokenPipe.into()) }
        fn flush(&mut self) -> io::Result<()> { Ok(()) }
    }

    #[test]
    fn failed_output_refunds_admission_exactly_once() {
        let slots = Slots(Mutex::new(0));
        assert!(slots.try_acquire(1));
        let error = write_locked(&Mutex::new(FailingWriter), b"reply\n", Some(RequestSlot(&slots))).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::BrokenPipe);
        assert_eq!(*slots.0.lock().unwrap(), 0);
        assert!(slots.try_acquire(1));
    }

    #[test]
    fn oversized_response_refunds_admission_without_writing() {
        let rt = Runtime {
            early_lifetime: AtomicBool::new(false), protocol_version: AtomicI64::new(2), out: Mutex::new(std::io::stdout()),
            next_id: AtomicI64::new(0), pending: Mutex::new(HashMap::new()),
            active: Mutex::new(HashMap::new()), retired: Mutex::new(HashMap::new()), instances: Mutex::new(HashMap::new()), service_calls: Mutex::new(HashMap::new()), event_calls: Mutex::new(HashMap::new()), task_calls: Mutex::new(HashMap::new()), resource_calls: Mutex::new(HashMap::new()), message_calls: Mutex::new(HashMap::new()), specs: Vec::new(),
            closed: AtomicBool::new(false), slots: Slots(Mutex::new(0)),
            max_in_flight: AtomicUsize::new(1), max_reentry_depth: AtomicUsize::new(1), exit_code: AtomicI64::new(0),
        };
        assert!(rt.slots.try_acquire(1));
        let error = rt.write_with_slot(json!({"result": "x".repeat(MAX_LINE_BYTES)}), Some(RequestSlot(&rt.slots))).unwrap_err();
        assert_eq!(error.code, codes::INTERNAL_ERROR);
        assert_eq!(*rt.slots.0.lock().unwrap(), 0);
    }
}
