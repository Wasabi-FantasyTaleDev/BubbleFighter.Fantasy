package bffplugin

import (
	"context"
	"encoding/json"
	"fmt"
	"sync/atomic"
)

// EventSpec declares a shared event identity and native mode. ResultType is required for waterfall/bail.
// Subscription modes are sync, async, waterfall and bail; publications use emit, serial, parallel, waterfall and bail.
type EventSpec struct {
	Type       string `json:"type"`
	Mode       string `json:"mode"`
	ResultType string `json:"result_type,omitempty"`
}

func (e EventSpec) params(activation string) map[string]any {
	p := map[string]any{"activation": activation, "type": e.Type, "mode": e.Mode}
	if e.ResultType != "" {
		p["result_type"] = e.ResultType
	}
	return p
}

// EventHandler receives an event callback. Notification modes return nil; result modes return the declared value.
type EventHandler func(call *EventCall) (any, error)

// Events adapts the activation's native event bus without reimplementing dispatch order or ownership.
type Events struct{ state *activation }

// EventCall carries the event, cancellation and scoped continuation. Do not retain it after callback return.
type EventCall struct {
	Body    map[string]any
	Type    string
	Mode    string
	Context context.Context
	rt      *runtime
	key     serviceKey
	hasNext bool
	live    atomic.Bool
	cancel  context.CancelFunc
}

// Next calls the selected downstream pipeline; repeated calls preserve the native continuation behavior.
func (c *EventCall) Next() (json.RawMessage, error) {
	if !c.live.Load() || !c.hasNext {
		return nil, fmt.Errorf("this event invocation has no live continuation")
	}
	return c.rt.call("events.next", map[string]any{"activation": c.key.activation, "invocation": c.key.invocation})
}

// Dispose removes a handle in this callback's native context. Use it for self-removal to avoid waiting on yourself.
func (c *EventCall) Dispose(handle *Handle) error {
	if !c.live.Load() || handle == nil || handle.state.rt != c.rt || handle.state.id != c.key.activation {
		return fmt.Errorf("the event invocation or handle does not belong to this live activation")
	}
	return handle.dispose(c.key.invocation)
}

func (e Events) checkLive() error {
	if !e.state.live.Load() || e.state.rt.protocolVersion.Load() < 2 {
		return fmt.Errorf("events require a live protocol v2 activation")
	}
	return nil
}

func (e Events) reserve(handler EventHandler) (string, error) {
	if err := e.checkLive(); err != nil {
		return "", err
	}
	if handler == nil {
		return "", fmt.Errorf("an event callback must be callable")
	}
	e.state.mu.Lock()
	defer e.state.mu.Unlock()
	if len(e.state.providers)+len(e.state.eventCallbacks) >= e.state.maxProviderCallbacks {
		return "", fmt.Errorf("callbacks exceed max_provider_callbacks")
	}
	e.state.nextProvider++
	callback := fmt.Sprintf("e%d", e.state.nextProvider)
	e.state.eventCallbacks[callback] = handler
	return callback, nil
}

func (e Events) forget(callback string) {
	e.state.mu.Lock()
	delete(e.state.eventCallbacks, callback)
	e.state.mu.Unlock()
}

// On registers an owned listener. Higher priorities run first; equal priorities keep registration order.
func (e Events) On(event EventSpec, handler EventHandler, priority int) (*Handle, error) {
	callback, err := e.reserve(handler)
	if err != nil {
		return nil, err
	}
	params := event.params(e.state.id)
	params["callback"], params["priority"] = callback, priority
	raw, err := e.state.rt.call("events.on", params)
	if err != nil {
		e.forget(callback)
		return nil, err
	}
	var reply struct {
		Handle string `json:"handle"`
	}
	if json.Unmarshal(raw, &reply) != nil || reply.Handle == "" {
		return nil, fmt.Errorf("events.on returned no handle")
	}
	return &Handle{ID: reply.Handle, state: e.state}, nil
}

// Publish dispatches an event in its explicit native mode. Only waterfall requires a non-nil terminal.
// The terminal remains retained until the host's actual publication completes, including cancellation cleanup.
func (e Events) Publish(event EventSpec, body map[string]any, terminal EventHandler) (json.RawMessage, error) {
	if err := e.checkLive(); err != nil {
		return nil, err
	}
	if (event.Mode == "waterfall") != (terminal != nil) {
		return nil, fmt.Errorf("only waterfall publication requires a terminal callback")
	}
	params := event.params(e.state.id)
	params["body"] = body
	if terminal != nil {
		callback, err := e.reserve(terminal)
		if err != nil {
			return nil, err
		}
		defer e.forget(callback)
		params["terminal"] = callback
	}
	return e.state.rt.call("events.publish", params)
}

func (rt *runtime) prepareEvent(params map[string]any) error {
	key, err := serviceIdentity(params)
	if err != nil {
		return err
	}
	body, ok := params["body"].(map[string]any)
	if !ok {
		return fmt.Errorf("event body must be an object")
	}
	typeName, err := stringParam(params, "type")
	if err != nil {
		return err
	}
	mode, err := stringParam(params, "mode")
	if err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(context.Background())
	call := &EventCall{Body: body, Type: typeName, Mode: mode, Context: ctx, cancel: cancel, rt: rt, key: key}
	call.hasNext, _ = params["has_next"].(bool)
	call.live.Store(true)
	rt.mu.Lock()
	defer rt.mu.Unlock()
	if rt.eventCalls == nil {
		rt.eventCalls = map[serviceKey]*EventCall{}
	}
	if rt.eventCalls[key] != nil {
		cancel()
		return fmt.Errorf("duplicate event invocation")
	}
	rt.eventCalls[key] = call
	return nil
}

func (rt *runtime) invokeEvent(params map[string]any) (any, error) {
	state, err := rt.live(params)
	if err != nil {
		return nil, err
	}
	callback, err := stringParam(params, "callback")
	if err != nil {
		return nil, err
	}
	key, err := serviceIdentity(params)
	if err != nil {
		return nil, err
	}
	rt.mu.Lock()
	call := rt.eventCalls[key]
	rt.mu.Unlock()
	if call == nil {
		return nil, fmt.Errorf("event invocation is not admitted")
	}
	state.mu.Lock()
	handler := state.eventCallbacks[callback]
	state.mu.Unlock()
	if handler == nil {
		return nil, &RPCError{Code: CodeInvalidParams, Message: "unknown event callback"}
	}
	return handler(call)
}

func (rt *runtime) finishEvent(params map[string]any) {
	key, err := serviceIdentity(params)
	if err != nil {
		return
	}
	rt.mu.Lock()
	call := rt.eventCalls[key]
	delete(rt.eventCalls, key)
	rt.mu.Unlock()
	if call != nil {
		call.live.Store(false)
		call.cancel()
	}
}

func (rt *runtime) cancelEvent(params map[string]any) {
	key, err := serviceIdentity(params)
	if err != nil {
		return
	}
	rt.mu.Lock()
	call := rt.eventCalls[key]
	rt.mu.Unlock()
	if call != nil {
		call.cancel()
	}
}
