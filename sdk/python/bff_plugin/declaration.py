"""The ``@plugin`` decorator and the definition it describes (spec sections 5 and 8)."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Callable, Iterable, Mapping
from .events import EventSpec

_FIELD_TYPES: dict[Any, str] = {
    str: "string",
    int: "int",
    bool: "bool",
    float: "number",
    list: "list",
    dict: "map",
}
_VALID_TYPE_NAMES = frozenset(_FIELD_TYPES.values())


@dataclass(frozen=True)
class PluginSpec:
    """Everything ``describe`` reports about one plugin class."""

    id: str
    version: str
    description: str
    supported_builds: tuple[str, ...]
    supported_families: tuple[str, ...]
    requires: tuple[str, ...]
    optional: tuple[str, ...]
    provides: tuple[str, ...]
    handles: tuple[str, ...]
    sends: tuple[str, ...]
    config: Mapping[str, Mapping[str, Any]]
    cls: type
    provides_many: tuple[str, ...] = ()
    decorates: tuple[str, ...] = ()
    requires_many: tuple[str, ...] = ()
    optional_many: tuple[str, ...] = ()
    subscribes: tuple[EventSpec, ...] = ()
    publishes: tuple[EventSpec, ...] = ()
    overrides: tuple[str, ...] = ()
    middleware: tuple[str, ...] = ()

    def definition(self) -> dict[str, Any]:
        """Returns the JSON object ``describe`` lists for this plugin."""
        return {
            "id": self.id,
            "version": self.version,
            "description": self.description,
            "supported_builds": list(self.supported_builds),
            "supported_families": list(self.supported_families),
            "requires": list(self.requires),
            "optional": list(self.optional),
            "provides": list(self.provides),
            **({"provides_many": list(self.provides_many)} if self.provides_many else {}),
            **({"decorates": list(self.decorates)} if self.decorates else {}),
            **({"requires_many": list(self.requires_many)} if self.requires_many else {}),
            **({"optional_many": list(self.optional_many)} if self.optional_many else {}),
            **({"subscribes": [item.definition() for item in self.subscribes]} if self.subscribes else {}),
            **({"publishes": [item.definition() for item in self.publishes]} if self.publishes else {}),
            **({"overrides": list(self.overrides)} if self.overrides else {}),
            **({"middleware": list(self.middleware)} if self.middleware else {}),
            "handles": list(self.handles),
            "sends": list(self.sends),
            "config": {name: dict(field) for name, field in self.config.items()},
        }


# Plugin classes in declaration order; ``run()`` serves all of them.
_REGISTRY: list[PluginSpec] = []


def registered_plugins() -> list[PluginSpec]:
    """Returns the specs of every class decorated with ``@plugin`` so far."""
    return list(_REGISTRY)


def _names(values: Iterable[str], what: str) -> tuple[str, ...]:
    if isinstance(values, str):
        raise TypeError(f"{what} must be a list of names, not a single string")
    result = tuple(values)
    for value in result:
        if not isinstance(value, str) or not value:
            raise TypeError(f"{what} must contain nonempty strings, got {value!r}")
    if len(set(result)) != len(result):
        raise ValueError(f"{what} lists a name twice: {result!r}")
    return result


def _type_name(kind: Any, field: str) -> str:
    if isinstance(kind, str) and kind in _VALID_TYPE_NAMES:
        return kind
    try:
        return _FIELD_TYPES[kind]
    except (KeyError, TypeError):
        raise TypeError(
            f"config field {field!r}: type must be one of str, int, bool, float, list, dict "
            f"(or the schema names {sorted(_VALID_TYPE_NAMES)}), got {kind!r}"
        ) from None


def _field(name: str, spec: Any) -> dict[str, Any]:
    """Turns ``str``, ``("hello", str)`` or ``{"type": ..., ...}`` into a schema entry."""
    if isinstance(spec, tuple):
        if len(spec) != 2:
            raise TypeError(f"config field {name!r}: a tuple must be (default, type)")
        default, kind = spec
        return {"type": _type_name(kind, name), "default": default}
    if isinstance(spec, dict):
        entry = dict(spec)
        entry["type"] = _type_name(entry.get("type"), name)
        return entry
    return {"type": _type_name(spec, name), "required": True}


def plugin(
    id: str,
    *,
    version: str,
    supported_builds: Iterable[str] = (),
    supported_families: Iterable[str] = (),
    requires: Iterable[str] = (),
    optional: Iterable[str] = (),
    provides: Iterable[str] = (),
    provides_many: Iterable[str] = (),
    decorates: Iterable[str] = (),
    requires_many: Iterable[str] = (),
    optional_many: Iterable[str] = (),
    subscribes: Iterable[EventSpec] = (),
    publishes: Iterable[EventSpec] = (),
    overrides: Iterable[str] = (),
    middleware: Iterable[str] = (),
    handles: Iterable[str] = (),
    sends: Iterable[str] = (),
    config: Mapping[str, Any] | None = None,
    description: str = "",
) -> Callable[[type], type]:
    """Declares a plugin class.

    ``config`` maps a snake_case key to ``str``/``int``/``bool``/``float``/``list``/``dict`` (required)
    or ``(default, type)`` (optional with a default). The host validates the row's config against it
    and passes the defaults-applied mapping as ``ctx.config``. The class needs an ``activate(ctx)``
    method, plain or ``async``, and may define ``deactivate(ctx)``.
    """
    if not isinstance(id, str) or not id:
        raise TypeError("plugin id must be a nonempty string")
    if not isinstance(version, str) or not version:
        raise TypeError("plugin version must be a nonempty string")

    def decorate(cls: type) -> type:
        if not callable(getattr(cls, "activate", None)):
            raise TypeError(f"plugin class {cls.__name__} must define activate(self, ctx)")
        if any(spec.id == id for spec in _REGISTRY):
            raise ValueError(f"plugin id {id!r} is declared twice")
        fields = {name: _field(name, spec) for name, spec in (config or {}).items()}
        spec = PluginSpec(
            id=id,
            version=version,
            description=description,
            supported_builds=_names(supported_builds, "supported_builds"),
            supported_families=_names(supported_families, "supported_families"),
            requires=_names(requires, "requires"),
            optional=_names(optional, "optional"),
            provides=_names(provides, "provides"),
            provides_many=_names(provides_many, "provides_many"),
            decorates=_names(decorates, "decorates"),
            requires_many=_names(requires_many, "requires_many"),
            optional_many=_names(optional_many, "optional_many"),
            subscribes=tuple(subscribes),
            publishes=tuple(publishes),
            overrides=_names(overrides, "overrides"),
            middleware=_names(middleware, "middleware"),
            handles=_names(handles, "handles"),
            sends=_names(sends, "sends"),
            config=fields,
            cls=cls,
        )
        _REGISTRY.append(spec)
        cls.__bff_plugin__ = spec  # type: ignore[attr-defined]
        return cls

    return decorate
