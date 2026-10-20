package bffplugin

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
	"sync/atomic"
)

// TaskHandler performs one host-owned background operation until call.Context is canceled.
type TaskHandler func(call *TaskCall) error

// TaskCall is one admitted task callback with cooperative cancellation.
type TaskCall struct {
	Context context.Context
	rt      *runtime
	key     serviceKey
	live    atomic.Bool
	cancel  context.CancelFunc
}

// TaskHandle identifies native-owned work. Wait observes work; Dispose cancels and joins it.
type TaskHandle struct {
	ID       string
	state    *activation
	once     sync.Once
	err      error
	callback string
}

// Wait waits for the callback's actual work result without dropping its native owner.
func (h *TaskHandle) Wait() (json.RawMessage, error) {
	return h.state.rt.call("tasks.wait", map[string]any{"activation": h.state.id, "task": h.ID})
}

// Dispose requests cancellation and joins native cleanup; repeated disposal is idempotent.
func (h *TaskHandle) Dispose() error {
	h.once.Do(func() {
		_, h.err = h.state.rt.call("tasks.dispose", map[string]any{"activation": h.state.id, "task": h.ID})
		if h.err == nil {
			h.state.mu.Lock()
			delete(h.state.taskCallbacks, h.callback)
			h.state.mu.Unlock()
		}
	})
	return h.err
}

// Tasks starts background work under the host's native task owner and shared effect budget.
type Tasks struct{ state *activation }

// Run starts work and returns a handle. Pass the handle to Context.Effect for teardown ownership.
// A WebAssembly module has one thread and refuses tasks: running a callback inline would keep the
// module from reading the host's tasks.cancel and deactivate requests.
func (t Tasks) Run(name string, work TaskHandler) (*TaskHandle, error) {
	if inline {
		return nil, fmt.Errorf("tasks are not supported in WebAssembly mode")
	}
	if !t.state.live.Load() || t.state.rt.protocolVersion.Load() < 2 {
		return nil, fmt.Errorf("tasks require a live protocol v2 activation")
	}
	if name == "" || work == nil {
		return nil, fmt.Errorf("task name and work are required")
	}
	t.state.mu.Lock()
	t.state.nextTask++
	callback := fmt.Sprintf("t%d", t.state.nextTask)
	t.state.taskCallbacks[callback] = work
	t.state.mu.Unlock()
	raw, err := t.state.rt.call("tasks.start", map[string]any{"activation": t.state.id, "name": name, "callback": callback})
	if err != nil {
		t.state.mu.Lock()
		delete(t.state.taskCallbacks, callback)
		t.state.mu.Unlock()
		return nil, err
	}
	var result struct {
		Task string `json:"task"`
	}
	if err := json.Unmarshal(raw, &result); err != nil || result.Task == "" {
		return nil, fmt.Errorf("invalid tasks.start response")
	}
	return &TaskHandle{ID: result.Task, state: t.state, callback: callback}, nil
}

func (rt *runtime) prepareTask(params map[string]any) error {
	key, err := serviceIdentity(params)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithCancel(context.Background())
	call := &TaskCall{Context: ctx, rt: rt, key: key, cancel: cancel}
	call.live.Store(true)
	rt.mu.Lock()
	defer rt.mu.Unlock()
	if rt.taskCalls == nil {
		rt.taskCalls = map[serviceKey]*TaskCall{}
	}
	if rt.taskCalls[key] != nil {
		cancel()
		return fmt.Errorf("duplicate task invocation")
	}
	rt.taskCalls[key] = call
	return nil
}

func (rt *runtime) invokeTask(params map[string]any) (any, error) {
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
	call := rt.taskCalls[key]
	rt.mu.Unlock()
	if call == nil {
		return nil, fmt.Errorf("task invocation is not admitted")
	}
	state.mu.Lock()
	handler := state.taskCallbacks[callback]
	state.mu.Unlock()
	if handler == nil {
		return nil, &RPCError{Code: CodeInvalidParams, Message: "unknown task callback"}
	}
	return nil, handler(call)
}

func (rt *runtime) finishTask(params map[string]any) {
	key, err := serviceIdentity(params)
	if err != nil {
		return
	}
	rt.mu.Lock()
	call := rt.taskCalls[key]
	delete(rt.taskCalls, key)
	rt.mu.Unlock()
	if call != nil {
		call.live.Store(false)
		call.cancel()
	}
}
func (rt *runtime) cancelTask(params map[string]any) {
	key, err := serviceIdentity(params)
	if err != nil {
		return
	}
	rt.mu.Lock()
	call := rt.taskCalls[key]
	rt.mu.Unlock()
	if call != nil {
		call.cancel()
	}
}
