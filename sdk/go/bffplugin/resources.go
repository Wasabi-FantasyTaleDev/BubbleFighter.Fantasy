package bffplugin

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
)

// ResourceHandler releases a native-owned resource. The host keeps ownership until it returns.
type ResourceHandler func(call *ResourceCall) error

// ResourceCall is one admitted resource release callback.
type ResourceCall struct {
	Context    context.Context
	Invocation string
	cancel     context.CancelFunc
}

// ResourceHandle identifies a native-owned resource.
type ResourceHandle struct {
	ID       string
	state    *activation
	callback string
	once     sync.Once
	err      error
}

// Dispose releases the resource; repeated calls are idempotent.
func (h *ResourceHandle) Dispose() error {
	h.once.Do(func() {
		var raw json.RawMessage
		raw, h.err = h.state.rt.call("resources.dispose", map[string]any{"activation": h.state.id, "resource": h.ID})
		var result struct {
			Deferred bool `json:"deferred"`
		}
		if h.err == nil {
			_ = json.Unmarshal(raw, &result)
		}
		if h.err == nil && !result.Deferred {
			h.state.mu.Lock()
			delete(h.state.resourceCallbacks, h.callback)
			h.state.mu.Unlock()
		}
	})
	return h.err
}

// ResourceScope is a child resource lifetime sharing the activation capacity.
type ResourceScope struct {
	ID    string
	state *activation
	once  sync.Once
	err   error
}

// Create creates a child scope.
func (s *ResourceScope) Create(name string) (*ResourceScope, error) {
	raw, err := s.state.rt.call("resources.scope", map[string]any{"activation": s.state.id, "parent": s.ID, "name": name})
	if err != nil {
		return nil, err
	}
	var result struct {
		Scope string `json:"scope"`
	}
	if json.Unmarshal(raw, &result) != nil || result.Scope == "" {
		return nil, fmt.Errorf("invalid resources.scope response")
	}
	return &ResourceScope{ID: result.Scope, state: s.state}, nil
}

// Acquire reserves a native slot and registers release as the guest callback.
func (s *ResourceScope) Acquire(name string, release ResourceHandler) (*ResourceHandle, error) {
	if name == "" || release == nil {
		return nil, fmt.Errorf("resource name and release are required")
	}
	s.state.mu.Lock()
	s.state.nextResource++
	callback := fmt.Sprintf("r%d", s.state.nextResource)
	s.state.resourceCallbacks[callback] = release
	s.state.mu.Unlock()
	raw, err := s.state.rt.call("resources.acquire", map[string]any{"activation": s.state.id, "scope": s.ID, "name": name, "callback": callback})
	if err != nil {
		s.state.mu.Lock()
		delete(s.state.resourceCallbacks, callback)
		s.state.mu.Unlock()
		return nil, err
	}
	var result struct {
		Resource string `json:"resource"`
	}
	if json.Unmarshal(raw, &result) != nil || result.Resource == "" {
		return nil, fmt.Errorf("invalid resources.acquire response")
	}
	return &ResourceHandle{ID: result.Resource, state: s.state, callback: callback}, nil
}

// Dispose releases this scope and its descendants.
func (s *ResourceScope) Dispose() error {
	s.once.Do(func() {
		_, s.err = s.state.rt.call("resources.scope.dispose", map[string]any{"activation": s.state.id, "scope": s.ID})
	})
	return s.err
}

// Resources creates root resource scopes.
type Resources struct{ state *activation }

// Scope creates a root native-owned scope.
func (r Resources) Scope(name string) (*ResourceScope, error) {
	if !r.state.live.Load() || r.state.rt.protocolVersion.Load() < 2 {
		return nil, fmt.Errorf("resource scopes require a live protocol v2 activation")
	}
	raw, err := r.state.rt.call("resources.scope", map[string]any{"activation": r.state.id, "name": name})
	if err != nil {
		return nil, err
	}
	var result struct {
		Scope string `json:"scope"`
	}
	if json.Unmarshal(raw, &result) != nil || result.Scope == "" {
		return nil, fmt.Errorf("invalid resources.scope response")
	}
	return &ResourceScope{ID: result.Scope, state: r.state}, nil
}

func (rt *runtime) prepareResource(params map[string]any) error {
	key, err := serviceIdentity(params)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(context.Background())
	invocation, err := stringParam(params, "invocation")
	if err != nil {
		cancel()
		return err
	}
	call := &ResourceCall{Context: ctx, Invocation: invocation, cancel: cancel}
	rt.mu.Lock()
	defer rt.mu.Unlock()
	if rt.resourceCalls[key] != nil {
		cancel()
		return fmt.Errorf("duplicate resource invocation")
	}
	rt.resourceCalls[key] = call
	return nil
}

func (rt *runtime) invokeResource(params map[string]any) (any, error) {
	state, err := rt.resourceState(params)
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
	call := rt.resourceCalls[key]
	rt.mu.Unlock()
	if call == nil {
		return nil, fmt.Errorf("resource invocation is not admitted")
	}
	state.mu.Lock()
	handler := state.resourceCallbacks[callback]
	state.mu.Unlock()
	if handler == nil {
		return nil, &RPCError{Code: CodeInvalidParams, Message: "unknown resource callback"}
	}
	defer func() {
		state.mu.Lock()
		delete(state.resourceCallbacks, callback)
		state.mu.Unlock()
		rt.mu.Lock()
		if len(state.resourceCallbacks) == 0 {
			delete(rt.retired, state.id)
		}
		rt.mu.Unlock()
	}()
	return nil, handler(call)
}

func (rt *runtime) resourceState(params map[string]any) (*activation, error) {
	id, err := stringParam(params, "activation")
	if err != nil {
		return nil, err
	}
	rt.mu.Lock()
	defer rt.mu.Unlock()
	if state := rt.active[id]; state != nil {
		return state, nil
	}
	if state := rt.retired[id]; state != nil {
		return state, nil
	}
	return nil, &RPCError{Code: CodeActivationNotLive, Message: fmt.Sprintf("activation %q is not known", id)}
}

func (rt *runtime) finishResource(params map[string]any) {
	key, err := serviceIdentity(params)
	if err != nil {
		return
	}
	rt.mu.Lock()
	call := rt.resourceCalls[key]
	delete(rt.resourceCalls, key)
	rt.mu.Unlock()
	if call != nil {
		call.cancel()
	}
}

func (rt *runtime) cancelResource(params map[string]any) {
	key, err := serviceIdentity(params)
	if err != nil {
		return
	}
	rt.mu.Lock()
	call := rt.resourceCalls[key]
	rt.mu.Unlock()
	if call != nil {
		call.cancel()
	}
}

var _ Disposable = (*ResourceHandle)(nil)
var _ Disposable = (*ResourceScope)(nil)
