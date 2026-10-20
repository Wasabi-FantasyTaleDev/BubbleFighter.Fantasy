// Package bffplugin writes out-of-process plugins for the BubbleFighter Fantasy host in Go.
//
// A plugin process speaks SDK plugin protocols v1 and v2 (docs/sdk-protocol.md): newline-delimited JSON-RPC 2.0 on
// stdin/stdout. The host's loader.process row starts the process, sends initialize and describe, and then
// activates rows against the definitions the process registered with Register.
package bffplugin

import "fmt"

// Field is one config schema entry. Type is one of string, int, bool, number, list or map. A field is either
// Required or has a Default; the host validates the row config and applies defaults before activate.
type Field struct {
	Type     string
	Required bool
	Default  any
}

// Required declares a config field the row must set.
func Required(kind string) Field { return Field{Type: kind, Required: true} }

// Optional declares a config field with a default value.
func Optional(kind string, value any) Field { return Field{Type: kind, Default: value} }

// Definition is what describe reports for one plugin. Handles and Sends name IMessage records from
// BubbleFighter.Fantasy.Contracts by short name. Protocol v1 allows only IMessageRouter in Requires and nothing
// in Provides; it is always added to Requires.
type Definition struct {
	ID                string
	Version           string
	Description       string
	SupportedBuilds   []string
	SupportedFamilies []string
	Requires          []string
	Optional          []string
	Provides          []string
	ProvidesMany      []string
	Decorates         []string
	RequiresMany      []string
	OptionalMany      []string
	Subscribes        []EventSpec
	Publishes         []EventSpec
	Overrides         []string
	Middleware        []string
	Handles           []string
	Sends             []string
	Config            map[string]Field
}

// Plugin is one activation of a definition. Activate registers handlers and effects through the context; an
// error fails the row's activation after the SDK has released what Activate registered.
type Plugin interface {
	Activate(ctx *Context) error
}

// Deactivator is implemented by plugins that need a hook before their effects are released.
type Deactivator interface {
	Deactivate(ctx *Context) error
}

type registration struct {
	def     Definition
	factory func() Plugin
}

var registry []registration

// Register adds a plugin the process serves. factory makes a fresh instance for every activation. Register panics
// on an invalid definition, because that is a programming error found on the first run.
func Register(def Definition, factory func() Plugin) {
	if def.ID == "" || def.Version == "" {
		panic("bffplugin: a definition needs an ID and a Version")
	}
	for _, r := range registry {
		if r.def.ID == def.ID {
			panic(fmt.Sprintf("bffplugin: plugin %s registered twice", def.ID))
		}
	}
	hasRouter := false
	for _, name := range def.Requires {
		if name == "IMessageRouter" {
			hasRouter = true
		}
	}
	if !hasRouter {
		def.Requires = append([]string{"IMessageRouter"}, def.Requires...)
	}
	registry = append(registry, registration{def: def, factory: factory})
}

func orEmpty(values []string) []string {
	if values == nil {
		return []string{}
	}
	return values
}

func (d Definition) wire() map[string]any {
	config := map[string]any{}
	for name, field := range d.Config {
		entry := map[string]any{"type": field.Type}
		if field.Required {
			entry["required"] = true
		} else {
			entry["default"] = field.Default
		}
		config[name] = entry
	}
	result := map[string]any{
		"id":                 d.ID,
		"version":            d.Version,
		"description":        d.Description,
		"supported_builds":   orEmpty(d.SupportedBuilds),
		"supported_families": orEmpty(d.SupportedFamilies),
		"requires":           orEmpty(d.Requires),
		"optional":           orEmpty(d.Optional),
		"provides":           orEmpty(d.Provides),
		"handles":            orEmpty(d.Handles),
		"sends":              orEmpty(d.Sends),
		"config":             config,
	}
	if len(d.ProvidesMany) > 0 {
		result["provides_many"] = d.ProvidesMany
	}
	if len(d.Decorates) > 0 {
		result["decorates"] = d.Decorates
	}
	if len(d.RequiresMany) > 0 {
		result["requires_many"] = d.RequiresMany
	}
	if len(d.OptionalMany) > 0 {
		result["optional_many"] = d.OptionalMany
	}
	if len(d.Subscribes) > 0 {
		result["subscribes"] = d.Subscribes
	}
	if len(d.Publishes) > 0 {
		result["publishes"] = d.Publishes
	}
	if len(d.Overrides) > 0 {
		result["overrides"] = d.Overrides
	}
	if len(d.Middleware) > 0 {
		result["middleware"] = d.Middleware
	}
	return result
}
