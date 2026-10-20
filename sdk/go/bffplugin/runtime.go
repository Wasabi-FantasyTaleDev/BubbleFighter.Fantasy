package bffplugin

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"runtime/debug"
	"sync"
	"sync/atomic"
)

// Protocol constants shared with the host (docs/sdk-protocol.md).
const (
	ProtocolVersion = 2
	SDKName         = "bff-plugin-go"
	SDKVersion      = "1.0.0"
	// MaxLineBytes is the longest JSON line read or written; the host's default max_line_bytes.
	MaxLineBytes = 1 << 20
	// controlReserve admits teardown while callback slots are full; only the lifecycle control
	// methods may use it, and the host admits the same reserve on its side of the wire.
	controlReserve = 4
)

// Error codes of the SDK plugin protocol.
const (
	CodePluginError        = -32000
	CodeActivationNotLive  = -32001
	CodeSessionUnknown     = -32002
	CodeTypeNotAllowed     = -32003
	CodeNoBinding          = -32004
	CodeServiceUnavailable = -32005
	CodeServiceError       = -32010
	CodeMethodNotFound     = -32601
	CodeInvalidParams      = -32602
	CodeInternalError      = -32603
)

// RPCError is a JSON-RPC error, returned by host calls and sent for failed host requests.
type RPCError struct {
	Code    int
	Message string
	Data    any
}

func (e *RPCError) Error() string { return fmt.Sprintf("rpc error %d: %s", e.Code, e.Message) }

// ErrHostGone is returned by host calls after stdin closed or the host sent shutdown.
var ErrHostGone = &RPCError{Code: CodeInternalError, Message: "bffplugin: the host went away"}

type response struct {
	result json.RawMessage
	err    error
}

type runtime struct {
	protocolVersion atomic.Int64
	earlyLifetime   atomic.Bool
	maxInFlight     int
	maxReentryDepth int
	inbound         int
	exitCode        int
	out             io.Writer
	writeMu         sync.Mutex
	nextID          atomic.Int64
	mu              sync.Mutex
	pending         map[int64]chan response
	active          map[string]*activation
	retired         map[string]*activation
	serviceCalls    map[serviceKey]*ServiceCall
	eventCalls      map[serviceKey]*EventCall
	taskCalls       map[serviceKey]*TaskCall
	resourceCalls   map[serviceKey]*ResourceCall
	messageCalls    map[serviceKey]*messageCall
	specs           map[string]registration
	closed          bool
	requests        sync.WaitGroup
}

type messageCall struct {
	ctx    context.Context
	cancel context.CancelFunc
}

// Run serves every registered plugin until the host sends shutdown or closes stdin, and returns the process exit
// code (0 on a clean end, 2 on a protocol violation). Call it from main as os.Exit(bffplugin.Run()).
func Run() int { return serve(os.Stdin, os.Stdout) }

func serve(in io.Reader, out io.Writer) int {
	rt := &runtime{
		out:             out,
		pending:         map[int64]chan response{},
		active:          map[string]*activation{},
		retired:         map[string]*activation{},
		serviceCalls:    map[serviceKey]*ServiceCall{},
		eventCalls:      map[serviceKey]*EventCall{},
		taskCalls:       map[serviceKey]*TaskCall{},
		resourceCalls:   map[serviceKey]*ResourceCall{},
		messageCalls:    map[serviceKey]*messageCall{},
		specs:           map[string]registration{},
		maxInFlight:     64,
		maxReentryDepth: 8,
	}
	for _, r := range registry {
		rt.specs[r.def.ID] = r
	}
	if inline {
		code := rt.serveInline()
		rt.shutdown()
		return code
	}
	reader := bufio.NewReaderSize(in, 64*1024)
	code := 0
	for {
		line, err := readLine(reader)
		if errors.Is(err, errLineTooLong) {
			diag(fmt.Sprintf("bffplugin: a host line exceeded %d bytes; exiting", MaxLineBytes))
			code = 2
			break
		}
		if err != nil {
			break
		}
		if len(bytes.TrimSpace(line)) == 0 {
			continue
		}
		var msg map[string]json.RawMessage
		if json.Unmarshal(line, &msg) != nil {
			diag("bffplugin: unparseable host line; exiting")
			code = 2
			break
		}
		if rt.dispatch(msg) {
			break
		}
	}
	rt.shutdown()
	return code
}

var errLineTooLong = errors.New("line too long")

// serveInline shares dispatch with nested calls, so a host callback can finish work an outer call is awaiting.
func (rt *runtime) serveInline() int {
	for {
		if rt.closed {
			return rt.exitCode
		}
		msg, code := readMessage()
		if msg == nil {
			return code
		}
		if rt.dispatch(msg) {
			return 0
		}
	}
}

// readMessage reads and parses one host line in a module; a nil map ends the loop with the returned exit code.
func readMessage() (map[string]json.RawMessage, int) {
	line, ok := readHostLine()
	if !ok {
		return nil, 0
	}
	var msg map[string]json.RawMessage
	if json.Unmarshal(line, &msg) != nil {
		diag("bffplugin: unparseable host line; exiting")
		return nil, 2
	}
	return msg, 0
}

func readLine(r *bufio.Reader) ([]byte, error) {
	var buf []byte
	for {
		chunk, err := r.ReadSlice('\n')
		buf = append(buf, chunk...)
		if len(buf) > MaxLineBytes+1 {
			return nil, errLineTooLong
		}
		if err == nil {
			return buf, nil
		}
		if !errors.Is(err, bufio.ErrBufferFull) {
			if len(buf) > 0 && errors.Is(err, io.EOF) {
				return buf, nil
			}
			return nil, err
		}
	}
}

// dispatch routes one host line; it reports true when the host asked the process to stop.
func (rt *runtime) dispatch(msg map[string]json.RawMessage) bool {
	var method string
	if raw, ok := msg["method"]; ok && json.Unmarshal(raw, &method) == nil {
		id, hasID := msg["id"]
		if !hasID {
			if method == "lifecycle.cancel" {
				var params map[string]any
				if json.Unmarshal(msg["params"], &params) == nil {
					id, _ := params["activation"].(string)
					rt.mu.Lock()
					state := rt.active[id]
					rt.mu.Unlock()
					if state != nil {
						state.stopLifetime()
					}
				}
			}
			if method == "events.cancel" {
				var params map[string]any
				if json.Unmarshal(msg["params"], &params) == nil {
					rt.cancelEvent(params)
				}
			}
			if method == "tasks.cancel" {
				var params map[string]any
				if json.Unmarshal(msg["params"], &params) == nil {
					rt.cancelTask(params)
				}
			}
			if method == "services.cancel" {
				var params map[string]any
				if json.Unmarshal(msg["params"], &params) == nil {
					rt.cancelService(params)
				}
			}
			if method == "resources.cancel" {
				var params map[string]any
				if json.Unmarshal(msg["params"], &params) == nil {
					rt.cancelResource(params)
				}
			}
			if method == "messages.cancel" {
				var params map[string]any
				if json.Unmarshal(msg["params"], &params) == nil {
					rt.cancelMessage(params)
				}
			}
			if method == "shutdown" {
				rt.shutdown()
				return true
			}
			return false
		}
		var params map[string]any
		if raw, ok := msg["params"]; ok {
			decoder := json.NewDecoder(bytes.NewReader(raw))
			decoder.UseNumber()
			if err := decoder.Decode(&params); err != nil {
				rt.respond(id, nil, &RPCError{Code: CodeInvalidParams, Message: "params must be an object"})
				return false
			}
		}
		if params == nil {
			params = map[string]any{}
		}
		rt.mu.Lock()
		limit := rt.maxInFlight
		if inline && rt.maxReentryDepth < limit {
			limit = rt.maxReentryDepth
		}
		if method == "deactivate" || method == "shutdown" {
			limit += controlReserve
		}
		if rt.closed || rt.inbound >= limit {
			rt.mu.Unlock()
			rt.respond(id, nil, &RPCError{Code: CodeInternalError, Message: "host request exceeds max_in_flight or max_reentry_depth"})
			return false
		}
		rt.inbound++
		rt.mu.Unlock()
		if method == "services.invoke" || method == "events.invoke" || method == "tasks.invoke" || method == "resources.invoke" || method == "messages.invoke" {
			prepare := rt.prepareService
			if method == "events.invoke" {
				prepare = rt.prepareEvent
			}
			if method == "messages.invoke" {
				prepare = rt.prepareMessage
			}
			if method == "tasks.invoke" {
				prepare = rt.prepareTask
			}
			if method == "resources.invoke" {
				prepare = rt.prepareResource
			}
			if err := prepare(params); err != nil {
				rt.respondCompleted(id, nil, &RPCError{Code: CodeInvalidParams, Message: err.Error()}, func() { rt.mu.Lock(); rt.inbound--; rt.mu.Unlock() })
				return false
			}
		}
		work := func() {
			rt.serveRequest(id, method, params)
		}
		if inline {
			work()
			return false
		}
		rt.requests.Add(1)
		// Host requests run concurrently: a request that calls back into the host must not block the reader.
		go func() {
			defer rt.requests.Done()
			work()
		}()
		return false
	}
	var id int64
	if json.Unmarshal(msg["id"], &id) != nil {
		diag("bffplugin: response without a numeric id ignored")
		return false
	}
	rt.mu.Lock()
	ch := rt.pending[id]
	delete(rt.pending, id)
	rt.mu.Unlock()
	if ch == nil {
		diag(fmt.Sprintf("bffplugin: response to unknown request %d ignored", id))
		return false
	}
	if raw, ok := msg["error"]; ok {
		var e struct {
			Code    int    `json:"code"`
			Message string `json:"message"`
			Data    any    `json:"data"`
		}
		_ = json.Unmarshal(raw, &e)
		ch <- response{err: &RPCError{Code: e.Code, Message: e.Message, Data: e.Data}}
	} else {
		ch <- response{result: msg["result"]}
	}
	return false
}

func (rt *runtime) shutdown() {
	rt.mu.Lock()
	rt.closed = true
	states := make([]*activation, 0, len(rt.active))
	for _, state := range rt.active {
		states = append(states, state)
	}
	calls := make([]*ServiceCall, 0, len(rt.serviceCalls))
	for _, call := range rt.serviceCalls {
		calls = append(calls, call)
	}
	events := make([]*EventCall, 0, len(rt.eventCalls))
	for _, call := range rt.eventCalls {
		events = append(events, call)
	}
	tasks := make([]*TaskCall, 0, len(rt.taskCalls))
	for _, call := range rt.taskCalls {
		tasks = append(tasks, call)
	}
	resources := make([]*ResourceCall, 0, len(rt.resourceCalls))
	for _, call := range rt.resourceCalls {
		resources = append(resources, call)
	}
	messages := make([]*messageCall, 0, len(rt.messageCalls))
	for _, call := range rt.messageCalls {
		messages = append(messages, call)
	}
	for id, ch := range rt.pending {
		ch <- response{err: ErrHostGone}
		delete(rt.pending, id)
	}
	rt.mu.Unlock()
	for _, state := range states {
		state.stopLifetime()
	}
	for _, call := range calls {
		call.cancel()
	}
	for _, call := range events {
		call.cancel()
	}
	for _, call := range tasks {
		call.cancel()
	}
	for _, call := range resources {
		call.cancel()
	}
	for _, call := range messages {
		call.cancel()
	}
}

func (rt *runtime) write(msg map[string]any) error {
	return rt.writeCompleted(msg, nil)
}

func (rt *runtime) writeCompleted(msg map[string]any, completed func()) error {
	defer func() {
		if completed != nil {
			completed()
		}
	}()
	data, err := json.Marshal(msg)
	if err != nil {
		return &RPCError{Code: CodeInternalError, Message: err.Error()}
	}
	if len(data) > MaxLineBytes {
		return &RPCError{Code: CodeInternalError, Message: fmt.Sprintf("message of %d bytes exceeds the %d-byte line limit", len(data), MaxLineBytes)}
	}
	if !inline {
		rt.writeMu.Lock()
		defer rt.writeMu.Unlock()
	}
	// Waiting writers still own admission; the single writer releases it before the response is visible.
	if completed != nil {
		completed()
		completed = nil
	}
	if inline {
		writeHostLine(data)
		return nil
	}
	_, err = rt.out.Write(append(data, '\n'))
	return err
}

// call sends a plugin-to-host request and waits for its result.
func (rt *runtime) call(method string, params map[string]any) (json.RawMessage, error) {
	id := rt.nextID.Add(1)
	ch := make(chan response, 1)
	rt.mu.Lock()
	if rt.closed {
		rt.mu.Unlock()
		return nil, ErrHostGone
	}
	if len(rt.pending) >= rt.maxInFlight {
		rt.mu.Unlock()
		return nil, &RPCError{Code: CodeInternalError, Message: "host call exceeds max_in_flight"}
	}
	rt.pending[id] = ch
	rt.mu.Unlock()
	defer func() { rt.mu.Lock(); delete(rt.pending, id); rt.mu.Unlock() }()
	if err := rt.write(map[string]any{"jsonrpc": "2.0", "id": id, "method": method, "params": params}); err != nil {
		return nil, err
	}
	if inline {
		return rt.waitInline(ch)
	}
	r := <-ch
	return r.result, r.err
}

// waitInline dispatches callbacks and routes every response to its own waiter, including outer requests.
func (rt *runtime) waitInline(ch chan response) (json.RawMessage, error) {
	for {
		select {
		case result := <-ch:
			return result.result, result.err
		default:
		}
		msg, code := readMessage()
		if msg == nil {
			rt.exitCode = code
			rt.shutdown()
			continue
		}
		rt.dispatch(msg)
	}
}

func (rt *runtime) notify(method string, params map[string]any) {
	_ = rt.write(map[string]any{"jsonrpc": "2.0", "method": method, "params": params})
}

func (rt *runtime) respond(id json.RawMessage, result any, err error) {
	rt.respondCompleted(id, result, err, nil)
}

func (rt *runtime) respondCompleted(id json.RawMessage, result any, err error, completed func()) {
	if err == nil {
		_ = rt.writeCompleted(map[string]any{"jsonrpc": "2.0", "id": id, "result": result}, completed)
		return
	}
	var rpc *RPCError
	if !errors.As(err, &rpc) {
		rpc = &RPCError{Code: CodePluginError, Message: err.Error()}
	}
	body := map[string]any{"code": rpc.Code, "message": rpc.Message}
	if rpc.Data != nil {
		body["data"] = rpc.Data
	}
	_ = rt.writeCompleted(map[string]any{"jsonrpc": "2.0", "id": id, "error": body}, completed)
}

func (rt *runtime) serveRequest(id json.RawMessage, method string, params map[string]any) {
	var result any
	var err error
	func() {
		// A panic in plugin code is reported to the host as that request's plugin error, with the stack.
		defer func() {
			if p := recover(); p != nil {
				err = &RPCError{Code: CodePluginError, Message: fmt.Sprint(p), Data: map[string]any{"traceback": string(debug.Stack())}}
			}
		}()
		switch method {
		case "initialize":
			result, err = rt.initialize(params)
		case "describe":
			defs := make([]any, 0, len(registry))
			for _, r := range registry {
				defs = append(defs, r.def.wire())
			}
			result = map[string]any{"definitions": defs}
		case "activate":
			result, err = rt.activate(params)
		case "deactivate":
			result, err = rt.deactivate(params)
		case "deliver":
			result, err = rt.deliver(params)
		case "services.invoke":
			result, err = rt.invokeService(params)
		case "events.invoke":
			result, err = rt.invokeEvent(params)
		case "tasks.invoke":
			result, err = rt.invokeTask(params)
		case "resources.invoke":
			result, err = rt.invokeResource(params)
		case "messages.invoke":
			result, err = rt.invokeMessage(params)
		default:
			err = &RPCError{Code: CodeMethodNotFound, Message: fmt.Sprintf("unknown method %q", method)}
		}
	}()
	if method == "services.invoke" {
		rt.finishService(params)
	}
	if method == "events.invoke" {
		rt.finishEvent(params)
	}
	if method == "tasks.invoke" {
		rt.finishTask(params)
	}
	if method == "resources.invoke" {
		rt.finishResource(params)
	}
	if method == "messages.invoke" {
		rt.finishMessage(params)
	}
	rt.respondCompleted(id, result, err, func() {
		rt.mu.Lock()
		rt.inbound--
		rt.mu.Unlock()
	})
}

func (rt *runtime) prepareMessage(params map[string]any) error {
	activation, err := stringParam(params, "activation")
	if err != nil { return err }
	invocation, err := stringParam(params, "invocation")
	if err != nil { return err }
	key := serviceKey{activation: activation, invocation: invocation}
	rt.mu.Lock()
	defer rt.mu.Unlock()
	if rt.messageCalls[key] != nil { return fmt.Errorf("duplicate message middleware invocation") }
	ctx, cancel := context.WithCancel(context.Background())
	rt.messageCalls[key] = &messageCall{ctx: ctx, cancel: cancel}
	return nil
}

func (rt *runtime) finishMessage(params map[string]any) {
	activation, _ := params["activation"].(string)
	invocation, _ := params["invocation"].(string)
	key := serviceKey{activation: activation, invocation: invocation}
	rt.mu.Lock()
	call := rt.messageCalls[key]
	delete(rt.messageCalls, key)
	rt.mu.Unlock()
	if call != nil { call.cancel() }
}

func (rt *runtime) cancelMessage(params map[string]any) {
	activation, _ := params["activation"].(string)
	invocation, _ := params["invocation"].(string)
	rt.mu.Lock()
	call := rt.messageCalls[serviceKey{activation: activation, invocation: invocation}]
	rt.mu.Unlock()
	if call != nil { call.cancel() }
}

func (rt *runtime) initialize(params map[string]any) (any, error) {
	offered, ok := params["protocol_versions"].([]any)
	if !ok {
		offered = []any{params["protocol_version"]}
	}
	selected := int64(0)
	for _, raw := range offered {
		v, ok := raw.(json.Number)
		if !ok {
			continue
		}
		n, err := v.Int64()
		if err == nil && (n == 1 || n == 2) && n > selected {
			selected = n
		}
	}
	if selected == 0 {
		return nil, &RPCError{Code: CodeInvalidParams, Message: fmt.Sprintf("this SDK speaks protocol version %d, host asked for %v", ProtocolVersion, params["protocol_version"])}
	}
	flight, err := boundedParam(params, "max_in_flight", 64, 4096)
	if err != nil {
		return nil, err
	}
	depth, err := boundedParam(params, "max_reentry_depth", 8, 64)
	if err != nil {
		return nil, err
	}
	rt.mu.Lock()
	rt.maxInFlight, rt.maxReentryDepth = flight, depth
	rt.mu.Unlock()
	rt.protocolVersion.Store(selected)
	earlyLifetime := false
	if features, ok := params["features"].([]any); ok && selected >= 2 {
		for _, feature := range features {
			if feature == "lifecycle.cancel" {
				earlyLifetime = true
			}
		}
	}
	rt.earlyLifetime.Store(earlyLifetime)
	return map[string]any{"protocol_version": selected, "sdk": map[string]any{"name": SDKName, "version": SDKVersion}}, nil
}

func boundedParam(params map[string]any, name string, fallback, maximum int) (int, error) {
	raw, exists := params[name]
	if !exists {
		return fallback, nil
	}
	value, ok := raw.(json.Number)
	if ok {
		number, err := value.Int64()
		if err == nil && number >= 1 && number <= int64(maximum) {
			return int(number), nil
		}
	}
	return 0, &RPCError{Code: CodeInvalidParams, Message: fmt.Sprintf("%s must be an integer from 1 to %d", name, maximum)}
}

func stringParam(params map[string]any, name string) (string, error) {
	value, ok := params[name].(string)
	if !ok || value == "" {
		return "", &RPCError{Code: CodeInvalidParams, Message: fmt.Sprintf("%q must be a non-empty string", name)}
	}
	return value, nil
}

func (rt *runtime) live(params map[string]any) (*activation, error) {
	id, err := stringParam(params, "activation")
	if err != nil {
		return nil, err
	}
	rt.mu.Lock()
	defer rt.mu.Unlock()
	state := rt.active[id]
	if state == nil || !state.live.Load() {
		return nil, &RPCError{Code: CodeActivationNotLive, Message: fmt.Sprintf("activation %q is not live", id)}
	}
	return state, nil
}

func (rt *runtime) activate(params map[string]any) (any, error) {
	id, err := stringParam(params, "activation")
	if err != nil {
		return nil, err
	}
	pluginID, err := stringParam(params, "plugin")
	if err != nil {
		return nil, err
	}
	spec, ok := rt.specs[pluginID]
	if !ok {
		return nil, &RPCError{Code: CodeInvalidParams, Message: fmt.Sprintf("this process does not serve plugin %q", pluginID)}
	}
	config, ok := params["config"].(map[string]any)
	if !ok {
		return nil, &RPCError{Code: CodeInvalidParams, Message: "'config' must be an object"}
	}
	rowID, _ := params["row_id"].(string)
	reload, _ := params["reload"].(bool)
	capacity, err := boundedParam(params, "max_provider_callbacks", 64, 2147483647)
	if err != nil {
		return nil, err
	}
	state := &activation{id: id, rt: rt, handlers: map[string]Handler{}, middleware: map[string]Middleware{}, providers: map[string]map[string]ServiceHandler{}, eventCallbacks: map[string]EventHandler{}, taskCallbacks: map[string]TaskHandler{}, resourceCallbacks: map[string]ResourceHandler{}, maxProviderCallbacks: capacity}
	lifetime, cancelLifetime := context.WithCancel(context.Background())
	state.cancelLifetime = cancelLifetime
	state.live.Store(true)
	state.ctx = &Context{Lifetime: lifetime, Config: config, RowID: rowID, Plugin: pluginID, Reload: reload, state: state}
	state.ctx.Logger = Logger{state: state}
	state.ctx.Messages = Messages{state: state}
	state.ctx.Services = Services{state: state}
	state.ctx.Events = Events{state: state}
	state.ctx.Tasks = Tasks{state: state}
	state.ctx.Resources = Resources{state: state}
	rt.mu.Lock()
	if rt.closed {
		rt.mu.Unlock()
		cancelLifetime()
		return nil, ErrHostGone
	}
	if rt.active[id] != nil {
		rt.mu.Unlock()
		cancelLifetime()
		return nil, &RPCError{Code: CodeInvalidParams, Message: fmt.Sprintf("activation %q is already active", id)}
	}
	rt.active[id] = state
	rt.mu.Unlock()
	if err := safeCall(func() error {
		if rt.earlyLifetime.Load() {
			if _, err := rt.call("lifecycle.ready", map[string]any{"activation": id}); err != nil {
				return err
			}
		}
		if err := lifetime.Err(); err != nil {
			return err
		}
		state.instance = spec.factory()
		return state.instance.Activate(state.ctx)
	}); err != nil {
		state.stopLifetime()
		// A failed activation leaves nothing behind: release what it registered, then report the failure.
		for _, failure := range state.disposeEffects() {
			diag(fmt.Sprintf("bffplugin: rollback after failed activate also failed: %v", failure))
		}
		state.live.Store(false)
		rt.mu.Lock()
		delete(rt.active, id)
		rt.mu.Unlock()
		return nil, err
	}
	return map[string]any{}, nil
}

func (rt *runtime) deactivate(params map[string]any) (any, error) {
	state, err := rt.live(params)
	if err != nil {
		return nil, err
	}
	state.live.Store(false)
	state.stopLifetime()
	var failures []error
	if hook, ok := state.instance.(Deactivator); ok {
		if err := safeCall(func() error { return hook.Deactivate(state.ctx) }); err != nil {
			failures = append(failures, err)
		}
	}
	failures = append(failures, state.disposeEffects()...)
	state.mu.Lock()
	hasResources := len(state.resourceCallbacks) > 0
	state.mu.Unlock()
	rt.mu.Lock()
	delete(rt.active, state.id)
	if hasResources {
		rt.retired[state.id] = state
	}
	rt.mu.Unlock()
	if len(failures) > 0 {
		return nil, &RPCError{Code: CodePluginError, Message: fmt.Sprintf("%d failure(s) while deactivating; first: %v", len(failures), failures[0])}
	}
	return map[string]any{}, nil
}

func (rt *runtime) deliver(params map[string]any) (any, error) {
	state, err := rt.live(params)
	if err != nil {
		return nil, err
	}
	handle, err := stringParam(params, "handle")
	if err != nil {
		return nil, err
	}
	state.mu.Lock()
	handler := state.handlers[handle]
	state.mu.Unlock()
	if handler == nil {
		return nil, &RPCError{Code: CodeInvalidParams, Message: fmt.Sprintf("unknown handle %q", handle)}
	}
	session, okSession := params["session"].(map[string]any)
	message, okMessage := params["message"].(map[string]any)
	body, okBody := message["body"].(map[string]any)
	if !okSession || !okMessage || !okBody {
		return nil, &RPCError{Code: CodeInvalidParams, Message: "'session' and 'message' {type, body} must be objects"}
	}
	if err := safeCall(func() error { return handler(sessionFrom(session), body) }); err != nil {
		return nil, err
	}
	return map[string]any{"handled": true}, nil
}

func (rt *runtime) invokeMessage(params map[string]any) (any, error) {
	state, err := rt.live(params)
	if err != nil { return nil, err }
	handle, err := stringParam(params, "handle")
	if err != nil { return nil, err }
	state.mu.Lock()
	middleware := state.middleware[handle]
	state.mu.Unlock()
	sessionRaw, okSession := params["session"].(map[string]any)
	message, okMessage := params["message"].(map[string]any)
	body, okBody := message["body"].(map[string]any)
	if middleware == nil || !okSession || !okMessage || !okBody {
		return nil, &RPCError{Code: CodeInvalidParams, Message: "unknown middleware callback or invalid message"}
	}
	activation, _ := params["activation"].(string)
	invocation, _ := params["invocation"].(string)
	rt.mu.Lock()
	call := rt.messageCalls[serviceKey{activation: activation, invocation: invocation}]
	rt.mu.Unlock()
	if call == nil { return nil, &RPCError{Code: CodeInvalidParams, Message: "message middleware invocation is not admitted"} }
	used := atomic.Bool{}
	next := func() error {
		if !used.CompareAndSwap(false, true) { return &RPCError{Code: CodeInvalidParams, Message: "message middleware next is single-use"} }
		_, err := rt.call("messages.next", map[string]any{"activation": activation, "invocation": invocation})
		return err
	}
	if err := middleware(sessionFrom(sessionRaw), body, next, call.ctx); err != nil { return nil, err }
	return map[string]any{}, nil
}

func safeCall(f func() error) (err error) {
	defer func() {
		if p := recover(); p != nil {
			err = &RPCError{Code: CodePluginError, Message: fmt.Sprint(p), Data: map[string]any{"traceback": string(debug.Stack())}}
		}
	}()
	return f()
}
