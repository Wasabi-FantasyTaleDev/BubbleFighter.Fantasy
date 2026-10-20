// Command conformance serves the two plugins the shared fixtures in tests/fixtures/sdk-protocol are written
// against; the fixture header lines describe their behaviour.
package main

import (
	"context"
	"errors"
	"os"

	"bffplugin/bffplugin"
)

type echo struct{}

func (echo) Activate(ctx *bffplugin.Context) error {
	greeting := ctx.String("greeting")
	protocols := append([]string{ctx.String("protocol")}, ctx.Strings("extra_protocols")...)
	for _, protocol := range protocols {
		protocol := protocol
		handle, err := ctx.Messages.On(protocol, "LoginRequested", func(s bffplugin.Session, body map[string]any) error {
			account, _ := body["account"].(string)
			if account == "task" {
				// A WebAssembly module has one thread, so it must refuse tasks instead of starving host requests.
				outcome := "tasks are available"
				if task, err := ctx.Tasks.Run("probe", func(*bffplugin.TaskCall) error { return nil }); err != nil {
					outcome = err.Error()
				} else if err := task.Dispose(); err != nil {
					outcome = err.Error()
				}
				ctx.Logger.Info("run-task: "+outcome, nil)
				return ctx.Messages.Send(protocol, s.ID, "LoginAccepted", map[string]any{
					"user_number": 1, "account": "task:" + outcome, "nickname": account,
					"server_time": "2026-01-01T00:00:00Z", "character_item_id": 300001,
				})
			}
			ctx.Logger.Info(greeting+" "+account, nil)
			return ctx.Messages.Send(protocol, s.ID, "LoginAccepted", map[string]any{
				"user_number": 1, "account": account, "nickname": account,
				"server_time": "2026-01-01T00:00:00Z", "character_item_id": 300001,
			})
		})
		if err != nil {
			return err
		}
		ctx.Effect(handle)
	}
	return nil
}

type failing struct{}

func (failing) Activate(ctx *bffplugin.Context) error {
	handle, err := ctx.Messages.On(ctx.String("protocol"), "LoginRequested", func(bffplugin.Session, map[string]any) error {
		panic("unreachable")
	})
	if err != nil {
		return err
	}
	ctx.Effect(handle)
	return errors.New("activation refused")
}

type middleware struct{}

func (middleware) Activate(ctx *bffplugin.Context) error {
	protocol := ctx.String("protocol")
	behavior := ctx.String("behavior")
	handle, err := ctx.Messages.Use(protocol, "LoginRequested", 7, func(s bffplugin.Session, body map[string]any, next bffplugin.MessageNext, _ context.Context) error {
		account, _ := body["account"].(string)
		if err := ctx.Messages.Send(protocol, s.ID, "LoginAccepted", map[string]any{
			"user_number": 1, "account": "middleware:" + account, "nickname": account,
			"server_time": "2026-01-01T00:00:00Z", "character_item_id": 300001,
		}); err != nil {
			return err
		}
		if err := next(); err != nil {
			return err
		}
		if behavior == "twice" {
			_ = next()
		}
		return nil
	})
	if err != nil {
		return err
	}
	ctx.Effect(handle)
	return nil
}

func main() {
	bffplugin.Register(bffplugin.Definition{
		ID: "conformance.echo", Version: "1.0.0", Description: "Echoes a greeting on login.",
		SupportedBuilds: []string{"kr:812"}, Handles: []string{"LoginRequested"}, Sends: []string{"LoginAccepted"},
		Config: map[string]bffplugin.Field{
			"protocol":        bffplugin.Required("string"),
			"greeting":        bffplugin.Optional("string", "hello"),
			"extra_protocols": bffplugin.Optional("list", []any{}),
		},
	}, func() bffplugin.Plugin { return echo{} })
	bffplugin.Register(bffplugin.Definition{
		ID: "conformance.failing", Version: "1.0.0", Description: "Registers a handler, then fails activation.",
		Handles: []string{"LoginRequested"},
		Config:  map[string]bffplugin.Field{"protocol": bffplugin.Required("string")},
	}, func() bffplugin.Plugin { return failing{} })
	bffplugin.Register(bffplugin.Definition{
		ID: "conformance.middleware", Version: "1.0.0", Description: "Wraps LoginRequested and continues the native pipeline once.",
		SupportedBuilds: []string{"kr:812"}, Middleware: []string{"LoginRequested"}, Sends: []string{"LoginAccepted"},
		Config: map[string]bffplugin.Field{"protocol": bffplugin.Required("string"), "behavior": bffplugin.Optional("string", "once")},
	}, func() bffplugin.Plugin { return middleware{} })
	os.Exit(bffplugin.Run())
}
