/** The `@plugin` decorator and the definition it describes (spec sections 5 and 8). */

import type { Context } from "./context.js";
import { eventDefinition, type EventSpec } from "./events.js";

/** The six schema type names the host's config binder understands. */
export type ConfigTypeName = "string" | "int" | "bool" | "number" | "list" | "map";

/**
 * How a config field's type is written: a schema name, or a constructor alias
 * (`String`, `Number`, `Boolean`, `Array`, `Object`). `int` exists only as the
 * schema name, since JavaScript has no integer type.
 */
export type ConfigType =
  | ConfigTypeName
  | StringConstructor
  | NumberConstructor
  | BooleanConstructor
  | ArrayConstructor
  | ObjectConstructor;

/**
 * One entry of the `config` option: a bare type (required), `[default, type]`
 * (optional with a default), or a raw schema entry such as `{type: "string"}`.
 */
export type ConfigField = ConfigType | readonly [unknown, ConfigType] | ({ type: ConfigType } & Record<string, unknown>);

/** The keyword arguments of `@plugin` (spec section 8). */
export interface PluginOptions {
  /** Release version, parseable as a .NET version (`1.0.0`). */
  version: string;
  /** Client build ranges such as `kr:812`; empty means build independent. */
  supportedBuilds?: readonly string[];
  /** Client build families, `legacy` or `modern`; empty means any family. */
  supportedFamilies?: readonly string[];
  /** Host contracts by short interface name; v1 allows only `IMessageRouter`. */
  requires?: readonly string[];
  /** Optional host contracts by short interface name. */
  optional?: readonly string[];
  /** Singleton interfaces the plugin implements through protocol v2 provider callbacks; empty in v1. */
  provides?: readonly string[];
  /** Explicit extension interfaces published through multiple registration. */
  providesMany?: readonly string[];
  /** Decoratable singleton interfaces wrapped by this plugin. */
  decorates?: readonly string[];
  /** Ordered extension interfaces requiring at least one provider. */
  requiresMany?: readonly string[];
  /** Ordered extension interfaces allowing an empty selection. */
  optionalMany?: readonly string[];
  /** Event operations registered on the native bus. */
  subscribes?: readonly EventSpec[];
  /** Event operations this plugin may publish. */
  publishes?: readonly EventSpec[];
  /** Short names of `IMessage` records the plugin registers handlers for. */
  handles?: readonly string[];
  /** Short names of `IMessage` records the plugin sends. */
  sends?: readonly string[];
  /** Explicit handler replacement targets in `rowId:fully.qualified.MessageType` form. */
  overrides?: readonly string[];
  /** Message type names for which the plugin may install middleware. */
  middleware?: readonly string[];
  /** Config schema: snake_case key to `ConfigField`. */
  config?: Record<string, ConfigField>;
  /** Free text shown in plugin listings. */
  description?: string;
}

/** What a plugin class must look like: a constructor plus `activate(ctx)`. */
export interface PluginInstance {
  activate(ctx: Context): unknown;
  deactivate?(ctx: Context): unknown;
}

/** A class that can be instantiated into a {@link PluginInstance}. */
export type PluginClass = new () => PluginInstance;

/** Everything `describe` reports about one plugin class. */
export class PluginSpec {
  constructor(
    readonly id: string,
    readonly version: string,
    readonly description: string,
    readonly supportedBuilds: readonly string[],
    readonly supportedFamilies: readonly string[],
    readonly requires: readonly string[],
    readonly optional: readonly string[],
    readonly provides: readonly string[],
    readonly handles: readonly string[],
    readonly sends: readonly string[],
    readonly config: Readonly<Record<string, Record<string, unknown>>>,
    readonly cls: PluginClass,
    readonly providesMany: readonly string[] = [],
    readonly decorates: readonly string[] = [],
    readonly requiresMany: readonly string[] = [],
    readonly optionalMany: readonly string[] = [],
    readonly subscribes: readonly EventSpec[] = [],
    readonly publishes: readonly EventSpec[] = [],
    readonly overrides: readonly string[] = [],
    readonly middleware: readonly string[] = [],
  ) {}

  /** Returns the JSON object `describe` lists for this plugin. */
  definition(): Record<string, unknown> {
    return {
      id: this.id,
      version: this.version,
      description: this.description,
      supported_builds: [...this.supportedBuilds],
      supported_families: [...this.supportedFamilies],
      requires: [...this.requires],
      optional: [...this.optional],
      provides: [...this.provides],
      ...(this.providesMany.length ? { provides_many: [...this.providesMany] } : {}),
      ...(this.decorates.length ? { decorates: [...this.decorates] } : {}),
      ...(this.requiresMany.length ? { requires_many: [...this.requiresMany] } : {}),
      ...(this.optionalMany.length ? { optional_many: [...this.optionalMany] } : {}),
      ...(this.subscribes.length ? { subscribes: this.subscribes.map(eventDefinition) } : {}),
      ...(this.publishes.length ? { publishes: this.publishes.map(eventDefinition) } : {}),
      ...(this.overrides.length ? { overrides: [...this.overrides] } : {}),
      ...(this.middleware.length ? { middleware: [...this.middleware] } : {}),
      handles: [...this.handles],
      sends: [...this.sends],
      config: Object.fromEntries(Object.entries(this.config).map(([name, field]) => [name, { ...field }])),
    };
  }
}

// Plugin classes in declaration order; `run()` serves all of them.
const REGISTRY: PluginSpec[] = [];

/** Returns the specs of every class decorated with `@plugin` so far. */
export function registeredPlugins(): PluginSpec[] {
  return [...REGISTRY];
}

function names(values: readonly string[] | undefined, what: string): string[] {
  if (typeof (values as unknown) === "string")
    throw new TypeError(`${what} must be a list of names, not a single string`);
  const result = [...(values ?? [])];
  for (const value of result)
    if (typeof value !== "string" || !value)
      throw new TypeError(`${what} must contain nonempty strings, got ${repr(value)}`);
  if (new Set(result).size !== result.length)
    throw new TypeError(`${what} lists a name twice: ${repr(result)}`);
  return result;
}

const FIELD_TYPES = new Map<unknown, ConfigTypeName>([
  [String, "string"],
  [Number, "number"],
  [Boolean, "bool"],
  [Array, "list"],
  [Object, "map"],
]);
const VALID_TYPE_NAMES = new Set<string>(FIELD_TYPES.values()).add("int");

function typeName(kind: unknown, field: string): ConfigTypeName {
  if (typeof kind === "string" && VALID_TYPE_NAMES.has(kind)) return kind as ConfigTypeName;
  const mapped = FIELD_TYPES.get(kind);
  if (mapped !== undefined) return mapped;
  throw new TypeError(
    `config field ${repr(field)}: type must be one of String, Number, Boolean, Array, Object ` +
      `(or the schema names ${repr([...VALID_TYPE_NAMES].sort())}), got ${repr(kind)}`,
  );
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function field(name: string, spec: ConfigField): Record<string, unknown> {
  // Turns `"string"`, `["hello", "string"]` or `{type: ...}` into a schema entry.
  if (Array.isArray(spec)) {
    if (spec.length !== 2) throw new TypeError(`config field ${repr(name)}: a pair must be [default, type]`);
    return { type: typeName(spec[1], name), default: spec[0] };
  }
  if (isPlainObject(spec)) {
    const entry = { ...spec };
    entry.type = typeName(entry.type, name);
    return entry;
  }
  return { type: typeName(spec, name), required: true };
}

/** Formats a value the way Python's `repr` would in error messages (single-quoted strings). */
function repr(value: unknown): string {
  if (typeof value === "string") return `'${value}'`;
  if (typeof value === "function") return (value as Function).name;
  try {
    return JSON.stringify(value) ?? String(value);
  } catch {
    return String(value);
  }
}

/**
 * Declares a plugin class.
 *
 * `config` maps a snake_case key to a type (required) or `[default, type]`
 * (optional with a default). The host validates the row's config against it and
 * passes the defaults-applied mapping as `ctx.config`. The class needs an
 * `activate(ctx)` method, plain or `async`, and may define `deactivate(ctx)`.
 */
export function plugin(
  id: string,
  options: PluginOptions,
): <T extends PluginClass>(cls: T, context?: ClassDecoratorContext<T>) => T {
  if (typeof id !== "string" || !id) throw new TypeError("plugin id must be a nonempty string");
  if (typeof (options as PluginOptions | undefined)?.version !== "string" || !options.version)
    throw new TypeError("plugin version must be a nonempty string");

  return function decorate<T extends PluginClass>(cls: T, _context?: ClassDecoratorContext<T>): T {
    if (typeof cls.prototype?.activate !== "function")
      throw new TypeError(`plugin class ${cls.name} must define activate(ctx)`);
    if (REGISTRY.some((spec) => spec.id === id)) throw new TypeError(`plugin id ${repr(id)} is declared twice`);
    const fields = Object.fromEntries(
      Object.entries(options.config ?? {}).map(([name, spec]) => [name, field(name, spec)]),
    );
    const spec = new PluginSpec(
      id,
      options.version,
      options.description ?? "",
      names(options.supportedBuilds, "supportedBuilds"),
      names(options.supportedFamilies, "supportedFamilies"),
      names(options.requires, "requires"),
      names(options.optional, "optional"),
      names(options.provides, "provides"),
      names(options.handles, "handles"),
      names(options.sends, "sends"),
      fields,
      cls,
      names(options.providesMany, "providesMany"),
      names(options.decorates, "decorates"),
      names(options.requiresMany, "requiresMany"),
      names(options.optionalMany, "optionalMany"),
      options.subscribes ?? [],
      options.publishes ?? [],
      names(options.overrides, "overrides"),
      names(options.middleware, "middleware"),
    );
    REGISTRY.push(spec);
    (cls as unknown as Record<string, unknown>).__bff_plugin__ = spec;
    return cls;
  };
}
