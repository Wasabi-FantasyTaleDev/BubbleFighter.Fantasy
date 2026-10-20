// Provider fixtures are mounted only by loader tests, never as live persistence.
package main

import (
	bff "bffplugin/bffplugin"
	"encoding/json"
	"fmt"
	"os"
	"sync"
	"time"
)

type provider struct{}

func (*provider) Activate(ctx *bff.Context) error {
	var gate sync.Mutex
	var stored map[string]any
	policy, err := ctx.Services.Provide("IEquipmentReplyPolicy", map[string]bff.ServiceHandler{"get_ReplyAfterInventory": func(*bff.ServiceCall) (any, error) { return false, nil }}, "decoratable", 0)
	if err != nil {
		return err
	}
	ctx.Effect(policy)
	repository, err := ctx.Services.Provide("IFighterRepository", map[string]bff.ServiceHandler{
		"UpsertAsync": func(call *bff.ServiceCall) (any, error) {
			gate.Lock()
			defer gate.Unlock()
			stored = call.Args["record"].(map[string]any)
			return nil, nil
		},
		"ListByUserNumAsync": func(call *bff.ServiceCall) (any, error) {
			if ctx.Config["behavior"] == "wait" && call.Args["user_num"] == json.Number("9007199254740993") {
				ctx.Logger.Info("provider-call-started", nil)
				select {
				case <-call.Context.Done():
				case <-time.After(15 * time.Second):
					return nil, fmt.Errorf("provider cancellation was not received")
				}
				ctx.Logger.Info("provider-call-cancelled", nil)
				return []any{}, nil
			}
			gate.Lock()
			defer gate.Unlock()
			if stored != nil && stored["user_num"] == call.Args["user_num"] {
				return []any{stored}, nil
			}
			return []any{}, nil
		},
	}, "singleton", 0)
	if err != nil {
		return err
	}
	ctx.Effect(repository)
	return nil
}

type decorator struct{}

func (*decorator) Activate(ctx *bff.Context) error {
	priority, _ := ctx.Config["priority"].(json.Number).Int64()
	handle, err := ctx.Services.Decorate("IEquipmentReplyPolicy", map[string]bff.ServiceHandler{"get_ReplyAfterInventory": func(call *bff.ServiceCall) (any, error) {
		result, err := call.Inner("", nil)
		if err != nil {
			return nil, err
		}
		var value bool
		if err = json.Unmarshal(result, &value); err != nil {
			return nil, err
		}
		return ctx.Config["operation"] == "negate" && !value, nil
	}}, int(priority))
	if err != nil {
		return err
	}
	ctx.Effect(handle)
	return nil
}

type many struct{}

func (*many) Activate(ctx *bff.Context) error {
	priority, _ := ctx.Config["priority"].(json.Number).Int64()
	handle, err := ctx.Services.Provide("IEquipmentReplyPolicy", map[string]bff.ServiceHandler{"get_ReplyAfterInventory": func(*bff.ServiceCall) (any, error) { return ctx.Config["value"], nil }}, "many", int(priority))
	if err != nil {
		return err
	}
	ctx.Effect(handle)
	return nil
}

func main() {
	bff.Register(bff.Definition{ID: "conformance.provider", Version: "1.0.0", Provides: []string{"IEquipmentReplyPolicy", "IFighterRepository"}, Config: map[string]bff.Field{"behavior": bff.Optional("string", "normal")}}, func() bff.Plugin { return &provider{} })
	bff.Register(bff.Definition{ID: "conformance.decorator", Version: "1.0.0", Decorates: []string{"IEquipmentReplyPolicy"}, Config: map[string]bff.Field{"priority": bff.Optional("int", 0), "operation": bff.Optional("string", "negate")}}, func() bff.Plugin { return &decorator{} })
	bff.Register(bff.Definition{ID: "conformance.provider-many", Version: "1.0.0", ProvidesMany: []string{"IEquipmentReplyPolicy"}, Config: map[string]bff.Field{"priority": bff.Optional("int", 0), "value": bff.Optional("bool", false)}}, func() bff.Plugin { return &many{} })
	bff.Register(bff.Definition{ID: "conformance.provider-consumer", Version: "1.0.0", Requires: []string{"IEquipmentReplyPolicy"}}, func() bff.Plugin { return &consumer{} })
	bff.Register(bff.Definition{ID: "conformance.many-consumer", Version: "1.0.0", RequiresMany: []string{"IEquipmentReplyPolicy"}, Config: map[string]bff.Field{"expected": bff.Optional("list", []bool{false, true, false})}}, func() bff.Plugin { return &manyConsumer{} })
	bff.Register(bff.Definition{ID: "conformance.optional-many-consumer", Version: "1.0.0", OptionalMany: []string{"IEquipmentReplyPolicy"}, Config: map[string]bff.Field{"expected": bff.Optional("list", []bool{})}}, func() bff.Plugin { return &manyConsumer{} })
	os.Exit(bff.Run())
}

type consumer struct{}

type manyConsumer struct{}

func (*manyConsumer) Activate(ctx *bff.Context) error {
	selected, err := ctx.Services.GetAll("IEquipmentReplyPolicy")
	if err != nil {
		return err
	}
	ctx.Effect(selected)
	expected := ctx.Config["expected"].([]any)
	if selected.Count != len(expected) {
		return fmt.Errorf("unexpected ordered service count")
	}
	for index := 0; index < selected.Count; index++ {
		raw, err := selected.Call(index, "get_ReplyAfterInventory", nil)
		if err != nil {
			return err
		}
		var value bool
		if err := json.Unmarshal(raw, &value); err != nil {
			return err
		}
		if value != expected[index] {
			return fmt.Errorf("unexpected ordered service value")
		}
	}
	if err := selected.Dispose(); err != nil {
		return err
	}
	ctx.Logger.Info("many-consumer-ok", nil)
	return nil
}

func (*consumer) Activate(ctx *bff.Context) error {
	raw, err := ctx.Services.Call("IEquipmentReplyPolicy", "get_ReplyAfterInventory", nil)
	if err != nil {
		return err
	}
	var value bool
	if err = json.Unmarshal(raw, &value); err != nil {
		return err
	}
	if value {
		return fmt.Errorf("SDK consumer expected the registered provider's false policy")
	}
	ctx.Logger.Info("provider-consumer-ok", nil)
	return nil
}
