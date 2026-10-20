"""Catalog data types."""

from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum


class Status(str, Enum):
    """Why an item can or cannot be previewed; every catalog item has exactly one."""

    RENDERABLE = "Renderable"
    ICON_ONLY = "Icon-only"
    PARTIAL = "Model partially resolved"
    MISSING_TEXTURE = "Missing texture"
    MISSING_MESH = "Missing mesh"
    UNSUPPORTED = "Unsupported format"
    METADATA_ONLY = "Metadata-only"
    FAILED = "Failed to decode"


class Kind(str, Enum):
    COSTUME_PART = "costume part"       # replaces a body part of a wearer (baseLocation)
    SLOT_ITEM = "slot item"             # attached to a socket bone (slotLocation)
    WEAPON = "weapon"
    CHARACTER = "character"
    SPRAY = "spray"                     # paint colour (function 1, subCategory Spray)
    OTHER = "other"


@dataclass
class ModelRef:
    """Where an item's geometry lives and how it is assembled."""

    folder: str                         # client folder holding the item's meshes
    meshes: list[str]
    motion: str | None = None
    wearers: list[str] = field(default_factory=list)   # characters with a version of this part
    borrowed_from: int | None = None    # modelFrom
    evidence: str = ""


@dataclass
class Price:
    amount: int
    money_type: str                     # lucci / cash / mileage ...
    stock_set_id: int
    stock_set_name: str
    duration: str = ""
    on_sale: bool = True                # False: the cheapest stock set is not flagged isOnSale


@dataclass
class CatalogItem:
    build: str
    item_id: int
    name: str
    main_category: str
    sub_category: str
    kind: Kind
    status: Status
    function: int = 0
    parameter: int = 0
    on_sale: bool = False
    display_tag: str = ""
    allow_chr: str = ""
    deny_chr: str = ""
    base_location: str = ""
    slot_location: str = ""
    model: ModelRef | None = None
    icon: str | None = None
    price: Price | None = None
    warnings: list[str] = field(default_factory=list)
    raw: dict = field(default_factory=dict)

    @property
    def is_3d(self) -> bool:
        return self.model is not None and bool(self.model.meshes)

    @property
    def preview_modes(self) -> list[str]:
        modes = []
        if self.is_3d:
            modes.append("3d")
        if self.icon:
            modes.append("icon")
        return modes or ["metadata"]


@dataclass
class Category:
    id: int
    name: str
    display: str
    order: int
    shop_view: bool
    inven_view: bool


@dataclass
class Fighter:
    field_id: int
    name: str
    model: str
    chr_type: int
    selectable: bool


@dataclass
class Spray:
    item_id: int
    name: str
    colour_index: int
    on_sale: bool


@dataclass
class Catalog:
    build: str
    items: list[CatalogItem]
    main_categories: list[Category]
    sub_categories: list[Category]
    fighters: list[Fighter]
    sprays: list[Spray]
    problems: list[str] = field(default_factory=list)

    def by_id(self) -> dict[int, CatalogItem]:
        return {i.item_id: i for i in self.items}

    def counts(self) -> dict[str, int]:
        out: dict[str, int] = {}
        for item in self.items:
            out[item.status.value] = out.get(item.status.value, 0) + 1
        return out
