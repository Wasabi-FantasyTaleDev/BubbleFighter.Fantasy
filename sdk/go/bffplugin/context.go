package bffplugin

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
	"sync/atomic"
)

// Session is the client connection a delivered message came from. ID is opaque; Player is the user number, or
// nil before login; Build is region:major:resource.
type Session struct {
	ID       string
	Protocol string
	Player   *int64
	Build    string
}

func sessionFrom(raw map[string]any) Session {
	s := Session{}
	s.ID, _ = raw["id"].(string)
	s.Protocol, _ = raw["protocol"].(string)
	s.Build, _ = raw["build"].(string)
	if p, ok := raw["player"].(json.Number); ok {
		v, err := p.Int64()
		if err != nil {
			panic("bffplugin: session player is not an int64")
		}
		s.Player = &v
	}
	return s
}

// Handler handles one delivered message body (a JSON object as a map). An error is reported to the host as the
// handler's failure.
type Handler func(session Session, body map[string]any) error

// MessageNext continues the native message pipeline exactly once.
type MessageNext func() error

// Middleware wraps a message handler. The context is canceled when the host unloads the row or cancels the invocation.
type Middleware func(session Session, body map[string]any, next MessageNext, ctx context.Context) error

// Disposable is anything Context.Effect can release on deactivate.
type Disposable interface {
	Dispose() error
}

// DisposeFunc adapts a plain function to Disposable.
type DisposeFunc func() error

// Dispose calls the function.
func (f DisposeFunc) Dispose() error { return f() }

type activation struct {
	id                   string
	rt                   *runtime
	ctx                  *Context
	instance             Plugin
	live                 atomic.Bool
	mu                   sync.Mutex
	handlers             map[string]Handler
	middleware           map[string]Middleware
	providers            map[string]map[string]ServiceHandler
	eventCallbacks       map[string]EventHandler
	taskCallbacks        map[string]TaskHandler
	resourceCallbacks    map[string]ResourceHandler
	maxProviderCallbacks int
	nextProvider         int64
	nextTask             int64
	nextResource         int64
	effects              []Disposable
	cancelLifetime       context.CancelFunc
}

func (a *activation) stopLifetime() {
	if a.cancelLifetime != nil {
		a.cancelLifetime()
	}
}

// disposeEffects releases effects in reverse registration order and returns every failure.
func (a *activation) disposeEffects() []error {
	a.mu.Lock()
	effects := a.effects
	a.effects = nil
	a.mu.Unlock()
	var failures []error
	for i := len(effects) - 1; i >= 0; i-- {
		if err := safeCall(effects[i].Dispose); err != nil {
			failures = append(failures, err)
		}
	}
	return failures
}

// Context is what Activate receives: the validated config and the host services the negotiated protocol offers.
type Context struct {
	// Lifetime is canceled when this activation starts stopping, before its cleanup hook.
	// Cancellation does not mean that admitted work or resource release has completed.
	Lifetime context.Context
	// Config is the row's config after host validation, with defaults applied.
	Config    map[string]any
	RowID     string
	Plugin    string
	Reload    bool
	Logger    Logger
	Messages  Messages
	Services  Services
	Events    Events
	Tasks     Tasks
	Resources Resources
	state     *activation
}

// Services calls declared shared host interfaces through the activation's service registry.
type Services struct{ state *activation }

// SupportsLifetimeCancellation reports whether the host signals stop before requesting deactivation.
// Older hosts only cancel the local signal during SDK cleanup or transport loss.
func (c *Context) SupportsLifetimeCancellation() bool { return c.state.rt.earlyLifetime.Load() }

// Call invokes a method name or full inventory id with snake_case arguments. The raw JSON result preserves
// integers: decode into a typed struct with int64/uint64 fields, or use a json.Decoder with UseNumber.
func (s Services) Call(service, method string, args map[string]any) (json.RawMessage, error) {
	if !s.state.live.Load() {
		return nil, &RPCError{Code: CodeActivationNotLive, Message: "the activation is not live"}
	}
	if s.state.rt.protocolVersion.Load() < 2 {
		return nil, &RPCError{Code: CodeMethodNotFound, Message: "services.call requires protocol v2"}
	}
	if args == nil {
		args = map[string]any{}
	}
	return s.state.rt.call("services.call", map[string]any{"activation": s.state.id, "service": service, "method": method, "args": args})
}

// Effect registers d to be released when the row deactivates (or when Activate fails) and returns it. Effects
// run in reverse registration order. Hand it every handle Messages.On returns.
func (c *Context) Effect(d Disposable) Disposable {
	c.state.mu.Lock()
	c.state.effects = append(c.state.effects, d)
	c.state.mu.Unlock()
	return d
}

// String returns a string config value; it panics when the key is missing or not a string, which the host's
// schema validation rules out for declared fields.
func (c *Context) String(key string) string {
	v, ok := c.Config[key].(string)
	if !ok {
		panic(fmt.Sprintf("bffplugin: config %q is not a string", key))
	}
	return v
}

// Strings returns a list config value whose items are strings.
func (c *Context) Strings(key string) []string {
	raw, _ := c.Config[key].([]any)
	out := make([]string, 0, len(raw))
	for _, item := range raw {
		s, ok := item.(string)
		if !ok {
			panic(fmt.Sprintf("bffplugin: config %q has a non-string item", key))
		}
		out = append(out, s)
	}
	return out
}

// Logger writes through the row's host logger.
type Logger struct{ state *activation }

func (l Logger) log(level, message string, fields map[string]any) {
	params := map[string]any{"activation": l.state.id, "level": level, "message": message}
	if len(fields) > 0 {
		params["fields"] = fields
	}
	l.state.rt.notify("log", params)
}

// Trace logs at trace level; fields may be nil.
func (l Logger) Trace(message string, fields map[string]any) { l.log("trace", message, fields) }

// Debug logs at debug level; fields may be nil.
func (l Logger) Debug(message string, fields map[string]any) { l.log("debug", message, fields) }

// Info logs at info level; fields may be nil.
func (l Logger) Info(message string, fields map[string]any) { l.log("info", message, fields) }

// Warn logs at warn level; fields may be nil.
func (l Logger) Warn(message string, fields map[string]any) { l.log("warn", message, fields) }

// Error logs at error level; fields may be nil.
func (l Logger) Error(message string, fields map[string]any) { l.log("error", message, fields) }

// Messages registers handlers for and sends typed Contracts messages through the host's IMessageRouter.
type Messages struct{ state *activation }

// Handle is a registration returned by On; Dispose unregisters it on the host.
type Handle struct {
	ID    string
	state *activation
	once  sync.Once
}

// Dispose unregisters the handler. It is safe to call more than once.
func (h *Handle) Dispose() error {
	return h.dispose("")
}

func (h *Handle) dispose(eventInvocation string) error {
	var err error
	h.once.Do(func() {
		h.state.mu.Lock()
		delete(h.state.handlers, h.ID)
		delete(h.state.middleware, h.ID)
		h.state.mu.Unlock()
		params := map[string]any{"activation": h.state.id, "handle": h.ID}
		if eventInvocation != "" {
			params["event_invocation"] = eventInvocation
		}
		_, err = h.state.rt.call("effect.dispose", params)
	})
	return err
}

// On registers handler for messageType on protocol (for example "kr-812/game") and returns its handle.
func (m Messages) On(protocol, messageType string, handler Handler) (*Handle, error) {
	result, err := m.state.rt.call("messages.on", map[string]any{"activation": m.state.id, "protocol": protocol, "type": messageType})
	if err != nil {
		return nil, err
	}
	var reply struct {
		Handle string `json:"handle"`
	}
	if json.Unmarshal(result, &reply) != nil || reply.Handle == "" {
		return nil, &RPCError{Code: CodeInternalError, Message: "messages.on returned no handle"}
	}
	m.state.mu.Lock()
	m.state.handlers[reply.Handle] = handler
	m.state.mu.Unlock()
	return &Handle{ID: reply.Handle, state: m.state}, nil
}

// Override replaces a declared handler owned by targetRowID. The target must be listed in the
// definition's Overrides as targetRowID:fully.qualified.MessageType.
func (m Messages) Override(protocol, messageType, targetRowID string, handler Handler) (*Handle, error) {
	result, err := m.state.rt.call("messages.override", map[string]any{
		"activation": m.state.id, "protocol": protocol, "type": messageType, "target_row_id": targetRowID,
	})
	if err != nil {
		return nil, err
	}
	var reply struct {
		Handle string `json:"handle"`
	}
	if json.Unmarshal(result, &reply) != nil || reply.Handle == "" {
		return nil, &RPCError{Code: CodeInternalError, Message: "messages.override returned no handle"}
	}
	m.state.mu.Lock()
	m.state.handlers[reply.Handle] = handler
	m.state.mu.Unlock()
	return &Handle{ID: reply.Handle, state: m.state}, nil
}

// Use installs middleware for a declared message type. Higher priorities run outermost.
func (m Messages) Use(protocol, messageType string, priority int, middleware Middleware) (*Handle, error) {
	if middleware == nil {
		return nil, fmt.Errorf("message middleware must not be nil")
	}
	result, err := m.state.rt.call("messages.use", map[string]any{
		"activation": m.state.id, "protocol": protocol, "type": messageType, "priority": priority,
	})
	if err != nil {
		return nil, err
	}
	var reply struct{ Handle string `json:"handle"` }
	if json.Unmarshal(result, &reply) != nil || reply.Handle == "" {
		return nil, &RPCError{Code: CodeInternalError, Message: "messages.use returned no handle"}
	}
	m.state.mu.Lock()
	m.state.middleware[reply.Handle] = middleware
	m.state.mu.Unlock()
	return &Handle{ID: reply.Handle, state: m.state}, nil
}

// Send sends a message of messageType with body to the session on protocol. The host fails it with
// CodeSessionUnknown after the session closed.
func (m Messages) Send(protocol string, sessionID string, messageType string, body map[string]any) error {
	_, err := m.state.rt.call("messages.send", map[string]any{
		"activation": m.state.id, "protocol": protocol, "session_id": sessionID, "type": messageType, "body": body,
	})
	return err
}
