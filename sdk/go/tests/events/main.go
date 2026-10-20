// Event fixtures for hosted-loader tests; no game profiles install these rows.
package main

import (
	bff "bffplugin/bffplugin"
	"encoding/json"
	"fmt"
	"os"
	"sync"
	"time"
)

var subscriptions = []bff.EventSpec{
	{Type: "EquipmentSelectionApplying", Mode: "sync"}, {Type: "EquipmentSelectionApplying", Mode: "async"},
	{Type: "EquipmentSelectionApplying", Mode: "waterfall", ResultType: "Int32"},
	{Type: "EquipmentSelectionApplying", Mode: "bail", ResultType: "String"},
}
var publications = append([]bff.EventSpec{
	{Type: "EquipmentSelectionApplying", Mode: "emit"}, {Type: "EquipmentSelectionApplying", Mode: "serial"},
	{Type: "EquipmentSelectionApplying", Mode: "parallel"},
}, subscriptions[2:]...)

type plugin struct{}

func (*plugin) Activate(ctx *bff.Context) error {
	var gate sync.Mutex
	handles := map[string]*bff.Handle{}
	behavior := ctx.String("behavior")
	if behavior == "resource-release" {
		scope, err := ctx.Resources.Scope("test-scope")
		if err != nil { return err }
		ctx.Effect(scope)
		resource, err := scope.Acquire("test-resource", func(*bff.ResourceCall) error {
			ctx.Logger.Info("resource-released", nil)
			return nil
		})
		if err != nil { return err }
		ctx.Effect(resource)
		ctx.Logger.Info("resource-acquired", nil)
		return nil
	}
	if behavior == "lifetime-fail" {
		ctx.Effect(bff.DisposeFunc(func() error {
			if ctx.Lifetime.Err() == nil {
				return fmt.Errorf("rollback did not cancel lifetime")
			}
			ctx.Logger.Info("lifetime-rollback-cancelled", nil)
			return nil
		}))
		return fmt.Errorf("lifetime activation failure")
	}
	listener := func(call *bff.EventCall) (any, error) {
		id, ok := call.Body["user_num"].(json.Number)
		if !ok || id.String() != "9007199254740993" {
			return nil, fmt.Errorf("event identity lost precision")
		}
		ctx.Logger.Info("event:"+ctx.RowID+":"+call.Mode, nil)
		if _, err := ctx.Services.Call("IEquipmentReplyPolicy", "get_ReplyAfterInventory", nil); err != nil {
			return nil, err
		}
		if behavior == "self" {
			gate.Lock()
			handle := handles[call.Mode]
			gate.Unlock()
			if err := call.Dispose(handle); err != nil {
				return nil, err
			}
			if call.Mode == "waterfall" {
				return 77, nil
			}
			return nil, nil
		}
		if behavior == "lifetime-alive" {
			if ctx.Lifetime.Err() != nil {
				return nil, fmt.Errorf("unloading another row cancelled this activation")
			}
			ctx.Logger.Info("lifetime-other-alive", nil)
		}
		if behavior == "lifetime-wait" {
			ctx.Logger.Info("lifetime-wait-entered", nil)
			select {
			case <-ctx.Lifetime.Done():
			case <-time.After(15 * time.Second):
				return nil, fmt.Errorf("activation cancellation was not received")
			}
			ctx.Logger.Info("lifetime-wait-cancelled", nil)
		}
		if behavior == call.Mode+"-error" {
			return nil, fmt.Errorf("event fixture failure")
		}
		if behavior == "wait" && call.Mode == "async" {
			ctx.Logger.Info("event-call-started", nil)
			select {
			case <-call.Context.Done():
			case <-time.After(15 * time.Second):
				return nil, fmt.Errorf("event cancellation was not received")
			}
			ctx.Logger.Info("event-call-cancelled", nil)
		}
		if call.Mode == "waterfall" {
			if behavior == "short" {
				return 77, nil
			}
			if behavior == "null" {
				return nil, nil
			}
			raw, err := call.Next()
			if err != nil {
				return nil, err
			}
			var value int
			if err := json.Unmarshal(raw, &value); err != nil {
				return nil, err
			}
			if behavior == "twice" {
				raw, err = call.Next()
				if err != nil {
					return nil, err
				}
				var again int
				if err := json.Unmarshal(raw, &again); err != nil {
					return nil, err
				}
				return value + again, nil
			}
			return value + 1, nil
		}
		if call.Mode == "bail" && ctx.String("decision") != "" {
			return ctx.String("decision"), nil
		}
		return nil, nil
	}
	priority, err := ctx.Config["priority"].(json.Number).Int64()
	if err != nil {
		return err
	}
	for _, event := range subscriptions {
		for _, mode := range ctx.Strings("modes") {
			if mode != event.Mode {
				continue
			}
			handle, err := ctx.Events.On(event, listener, int(priority))
			if err != nil {
				return err
			}
			gate.Lock()
			handles[event.Mode] = handle
			gate.Unlock()
			ctx.Effect(handle)
		}
	}
	if ctx.Config["publisher"] == true {
		handle, err := ctx.Messages.On("test/game", "LoginRequested", func(_ bff.Session, message map[string]any) error {
			for _, event := range publications {
				if event.Mode != message["account"] {
					continue
				}
				var terminal bff.EventHandler
				if event.Mode == "waterfall" {
					terminal = func(*bff.EventCall) (any, error) { return 100, nil }
				}
				result, err := ctx.Events.Publish(event, map[string]any{"protocol": "test/game", "user_num": int64(9007199254740993), "item_id": 13, "worn": true}, terminal)
				if err != nil {
					return err
				}
				ctx.Logger.Info("published:"+event.Mode+":"+string(result), nil)
				return nil
			}
			return fmt.Errorf("unknown publication mode")
		})
		if err != nil {
			return err
		}
		ctx.Effect(handle)
	}
	return nil
}

func (*plugin) Deactivate(ctx *bff.Context) error {
	if ctx.Lifetime.Err() == nil {
		return fmt.Errorf("deactivation did not cancel lifetime")
	}
	ctx.Logger.Info("lifetime-deactivated:"+ctx.RowID, nil)
	return nil
}

func main() {
	id := os.Getenv("EVENT_PLUGIN_ID")
	if id == "" {
		id = "conformance.events"
	}
	bff.Register(bff.Definition{ID: id, Version: "1.0.0", Requires: []string{"IEquipmentReplyPolicy"},
		Subscribes: subscriptions, Publishes: publications, Handles: []string{"LoginRequested"}, Config: map[string]bff.Field{
			"modes": bff.Optional("list", []string{"sync", "async", "waterfall", "bail"}), "priority": bff.Optional("int", 0),
			"behavior": bff.Optional("string", "normal"), "decision": bff.Optional("string", ""), "publisher": bff.Optional("bool", false),
		}}, func() bff.Plugin { return &plugin{} })
	os.Exit(bff.Run())
}
