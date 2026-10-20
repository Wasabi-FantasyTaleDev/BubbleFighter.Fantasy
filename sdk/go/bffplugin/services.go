package bffplugin

import (
	"context"
	"encoding/json"
	"fmt"
	"sort"
	"sync/atomic"
)

// ServiceHandler implements one interface method. The call carries exact decoded JSON numbers and cancellation.
type ServiceHandler func(call *ServiceCall) (any, error)

type serviceKey struct{ activation, invocation string }

// ServiceCollection owns an ordered service snapshot. Indices keep their provider identity until disposal.
type ServiceCollection struct {
	*Handle
	Count      int
	service    string
	collection string
}

// GetAll selects declared ordered providers. Pass the result to Context.Effect or dispose it early.
func (s Services) GetAll(service string) (*ServiceCollection, error) {
	if !s.state.live.Load() || s.state.rt.protocolVersion.Load() < 2 {
		return nil, fmt.Errorf("service collections require a live protocol v2 activation")
	}
	raw, err := s.state.rt.call("services.get_all", map[string]any{"activation": s.state.id, "service": service})
	if err != nil {
		return nil, err
	}
	var result struct {
		Handle     string
		Collection string
		Count      int
	}
	if err := json.Unmarshal(raw, &result); err != nil {
		return nil, err
	}
	if result.Handle == "" || result.Collection == "" || result.Count < 0 {
		return nil, fmt.Errorf("invalid service collection response")
	}
	return &ServiceCollection{Handle: &Handle{ID: result.Handle, state: s.state}, Count: result.Count, service: service, collection: result.Collection}, nil
}

// Call invokes one selected member. The host rejects expired collections and invalid indices.
func (s *ServiceCollection) Call(index int, method string, args map[string]any) (json.RawMessage, error) {
	if args == nil {
		args = map[string]any{}
	}
	return s.state.rt.call("services.call", map[string]any{"activation": s.state.id, "service": s.service,
		"collection": s.collection, "index": index, "method": method, "args": args})
}

// ServiceCall is one admitted provider call. Context is canceled cooperatively; Inner expires at callback return.
type ServiceCall struct {
	Method   string
	Args     map[string]any
	Context  context.Context
	rt       *runtime
	key      serviceKey
	hasInner bool
	live     atomic.Bool
	cancel   context.CancelFunc
}

// Inner invokes the selected native inner service; an empty method or nil args reuses the current call's values.
func (c *ServiceCall) Inner(method string, args map[string]any) (json.RawMessage, error) {
	if !c.live.Load() || !c.hasInner {
		return nil, fmt.Errorf("this invocation has no live inner service")
	}
	if method == "" {
		method = c.Method
	}
	if args == nil {
		args = c.Args
	}
	return c.rt.call("services.inner", map[string]any{"activation": c.key.activation, "invocation": c.key.invocation, "method": method, "args": args})
}

// Provide publishes a singleton, decoratable singleton, many registration or decorator with every method implemented.
// Keys are unambiguous method names or complete schema ids. Targets remain until activation teardown for retained projections.
func (s Services) Provide(service string, methods map[string]ServiceHandler, mode string, priority int) (*Handle, error) {
	if !s.state.live.Load() || s.state.rt.protocolVersion.Load() < 2 {
		return nil, fmt.Errorf("service providers require a live protocol v2 activation")
	}
	if mode == "" {
		mode = "singleton"
	}
	copyMethods := map[string]ServiceHandler{}
	names := make([]string, 0, len(methods))
	for name, handler := range methods {
		if name == "" || handler == nil {
			return nil, fmt.Errorf("methods must map names or ids to callbacks")
		}
		copyMethods[name] = handler
		names = append(names, name)
	}
	if len(names) == 0 {
		return nil, fmt.Errorf("methods must not be empty")
	}
	sort.Strings(names)
	s.state.mu.Lock()
	if len(s.state.providers)+len(s.state.eventCallbacks) >= s.state.maxProviderCallbacks {
		s.state.mu.Unlock()
		return nil, fmt.Errorf("provider callbacks exceed max_provider_callbacks")
	}
	s.state.nextProvider++
	callback := fmt.Sprintf("p%d", s.state.nextProvider)
	s.state.providers[callback] = copyMethods
	s.state.mu.Unlock()
	raw, err := s.state.rt.call("services.provide", map[string]any{"activation": s.state.id, "service": service, "mode": mode, "priority": priority, "callback": callback, "methods": names})
	if err != nil {
		s.state.mu.Lock()
		delete(s.state.providers, callback)
		s.state.mu.Unlock()
		return nil, err
	}
	var result struct {
		Handle string `json:"handle"`
	}
	if err = json.Unmarshal(raw, &result); err != nil || result.Handle == "" {
		return nil, fmt.Errorf("services.provide returned no handle")
	}
	return &Handle{ID: result.Handle, state: s.state}, nil
}

// Decorate adds an owned layer around a declared decoratable singleton.
func (s Services) Decorate(service string, methods map[string]ServiceHandler, priority int) (*Handle, error) {
	return s.Provide(service, methods, "decorate", priority)
}

func serviceIdentity(params map[string]any) (serviceKey, error) {
	activation, err := stringParam(params, "activation")
	if err != nil {
		return serviceKey{}, err
	}
	invocation, err := stringParam(params, "invocation")
	return serviceKey{activation, invocation}, err
}

func (rt *runtime) prepareService(params map[string]any) error {
	key, err := serviceIdentity(params)
	if err != nil {
		return err
	}
	method, err := stringParam(params, "method")
	if err != nil {
		return err
	}
	args, ok := params["args"].(map[string]any)
	if !ok {
		return fmt.Errorf("provider args must be an object")
	}
	ctx, cancel := context.WithCancel(context.Background())
	call := &ServiceCall{Method: method, Args: args, Context: ctx, cancel: cancel, rt: rt, key: key}
	call.hasInner, _ = params["has_inner"].(bool)
	call.live.Store(true)
	rt.mu.Lock()
	defer rt.mu.Unlock()
	if rt.serviceCalls == nil {
		rt.serviceCalls = map[serviceKey]*ServiceCall{}
	}
	if rt.serviceCalls[key] != nil {
		cancel()
		return fmt.Errorf("duplicate service invocation")
	}
	rt.serviceCalls[key] = call
	return nil
}

func (rt *runtime) invokeService(params map[string]any) (any, error) {
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
	call := rt.serviceCalls[key]
	rt.mu.Unlock()
	if call == nil {
		return nil, fmt.Errorf("service invocation is not admitted")
	}
	state.mu.Lock()
	handler := state.providers[callback][call.Method]
	if handler == nil {
		name, _ := params["method_name"].(string)
		handler = state.providers[callback][name]
	}
	state.mu.Unlock()
	if handler == nil {
		return nil, &RPCError{Code: CodeInvalidParams, Message: "unknown provider method"}
	}
	return handler(call)
}

func (rt *runtime) finishService(params map[string]any) {
	key, err := serviceIdentity(params)
	if err != nil {
		return
	}
	rt.mu.Lock()
	call := rt.serviceCalls[key]
	delete(rt.serviceCalls, key)
	rt.mu.Unlock()
	if call != nil {
		call.live.Store(false)
		call.cancel()
	}
}

func (rt *runtime) cancelService(params map[string]any) {
	key, err := serviceIdentity(params)
	if err != nil {
		return
	}
	rt.mu.Lock()
	call := rt.serviceCalls[key]
	rt.mu.Unlock()
	if call != nil {
		call.cancel()
	}
}
