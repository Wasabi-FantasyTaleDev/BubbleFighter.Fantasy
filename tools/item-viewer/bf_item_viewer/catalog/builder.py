"""Build a client's item catalog from its own tables and resource index.

Sources (all read from the selected client, never from a server database):
* ``Config/ShopItem2.xml`` sheets ShopItem, Stock, StockSet, MainCategory, SubCategory;
* ``Config/WeaponSpecDetail.xml`` (one transposed sheet per weapon category; shop weapon id =
  200000 + weapon ``id``, measured by name agreement and reported in ``problems``);
* ``Config/FighterConfig.xml`` (transposed; character item id = 300000 + field id);
* model folders found in the resource index: ``Models/Characters/<chr>/<group>/<item>/``,
  ``Models/SlotItem/<group>/<item>/``, ``Models/Weapon/<model>/``;
* item icons ``Images/Shop2/ItemIcons/<item>.png`` (kr-812 sub_9A2A60 formats ``%d.png``).

Every item found in ShopItem is kept; items that cannot be rendered carry a status and the
reason, never silently dropped.
"""

from __future__ import annotations

import re
from collections import defaultdict
from typing import Callable

from .model import Catalog, CatalogItem, Category, Fighter, Kind, ModelRef, Price, Spray, Status
from .spreadsheet import read_raw, read_sheets

WEAPON_BASE = 200000
CHARACTER_BASE = 300000
_CHR_PART = re.compile(r"^models/characters/([^/]+)/(\d+)/(\d+)/([^/]+)\.msh$", re.I)
_SLOT_ITEM = re.compile(r"^models/slotitem/(\d+)/(\d+)/([^/]+)\.(msh|mtn)$", re.I)

ProgressFn = Callable[[str], None]


def _int(text, default=0) -> int:
    try:
        return int(float(str(text).strip()))
    except (TypeError, ValueError):
        return default


def _horz(rows: list[list[str]]) -> tuple[list[str], dict[str, list[str]]]:
    """A transposed table: first column is the field name, columns from 2 are records."""
    fields = {}
    for row in rows:
        if row and row[0] and row[0] not in fields:
            fields[row[0]] = row[2:]
    names = fields.get("[Field]", [])
    return names, fields


class CatalogBuilder:
    def __init__(self, index, profile, progress: ProgressFn | None = None):
        self.index = index
        self.profile = profile
        self.build = profile.build
        self.progress = progress or (lambda message: None)
        self.problems: list[str] = []

    # -- source tables ------------------------------------------------------------------
    def _workbook(self, path: str, sheets: set[str] | None) -> dict:
        if not self.index.exists(path):
            self.problems.append(f"{path} is missing from this client")
            return {}
        return read_sheets(self.index.read(path), sheets)

    def _weapons(self) -> dict[int, dict]:
        path = "Config/WeaponSpecDetail.xml"
        if not self.index.exists(path):
            self.problems.append(f"{path} is missing; weapon models cannot be resolved")
            return {}
        data = self.index.read(path)
        out: dict[int, dict] = {}
        for name, rows in read_sheets(data, None).items():
            if not rows:
                continue
            header = list(rows[0].keys())
            names, fields = _horz([header] + [[r[h] for h in header] for r in rows])
            ids = fields.get("id", [])
            for i, caption in enumerate(names):
                if i >= len(ids) or not ids[i].strip().lstrip("-").isdigit():
                    continue
                record = {key: (values[i] if i < len(values) else "") for key, values in fields.items()}
                record["sheet"] = name
                record["caption"] = record.get("caption") or caption
                out[int(ids[i])] = record
        return out

    def _fighters(self) -> list[Fighter]:
        path = "Config/FighterConfig.xml"
        if not self.index.exists(path):
            self.problems.append(f"{path} is missing; character items have no model")
            return []
        data = self.index.read(path)
        names, fields = _horz(read_raw(data, "FighterConfig"))
        ids = fields.get("[Field]", [])
        out = []
        for i in range(len(ids)):
            fid = _int(ids[i], -1)
            if fid < 0:
                continue
            name = fields.get("name", [""] * len(ids))[i] if i < len(fields.get("name", [])) else ""
            model = fields.get("model", [])
            out.append(Fighter(fid, name, (model[i] if i < len(model) and model[i] else name),
                               _int(fields.get("chrTypeIdx", [""] * len(ids))[i] if i < len(fields.get("chrTypeIdx", [])) else 0),
                               (fields.get("isSelectable", [])[i] if i < len(fields.get("isSelectable", [])) else "") == "1"))
        return out

    # -- model folders --------------------------------------------------------------------
    def _part_folders(self) -> tuple[dict, dict]:
        parts: dict[int, dict[str, list[str]]] = defaultdict(lambda: defaultdict(list))
        for path in self.index.list_prefix("Models/Characters", ".msh"):
            m = _CHR_PART.match(path)
            if m:
                parts[int(m.group(3))][m.group(1)].append(path)
        slots: dict[int, list[str]] = defaultdict(list)
        for path in self.index.list_prefix("Models/SlotItem"):
            m = _SLOT_ITEM.match(path)
            if m:
                slots[int(m.group(2))].append(path)
        return parts, slots

    # -- build ----------------------------------------------------------------------------
    def build_catalog(self) -> Catalog:
        self.progress("reading ShopItem2.xml")
        shop = self._workbook(self.profile.raw["catalog"]["shop_workbook"],
                              {"ShopItem", "Stock", "StockSet", "MainCategory", "SubCategory"})
        display = self.profile.raw["catalog"].get("display_name_column", "nameKr")
        mains = [Category(_int(r.get("id")), r.get("name", ""), r.get(display) or r.get("nameKr") or r.get("name", ""),
                          _int(r.get("displayOrder")), r.get("shopView") == "1", r.get("invenView") == "1")
                 for r in shop.get("MainCategory", []) if r.get("name")]
        subs = [Category(_int(r.get("id")), r.get("name", ""), r.get(display) or r.get("nameKr") or r.get("name", ""),
                         _int(r.get("displayOrder")), r.get("shopView") == "1", r.get("invenView") == "1")
                for r in shop.get("SubCategory", []) if r.get("name")]
        self.progress("reading weapon and fighter tables")
        weapons = self._weapons()
        fighters = self._fighters()
        fighter_by_id = {f.field_id: f for f in fighters}
        self.progress("indexing model folders")
        parts, slots = self._part_folders()
        weapon_folders = {p.split("/")[2].lower(): p.split("/")[2] for p in self.index.list_prefix("Models/Weapon")}
        icon_folder = self.profile.raw["catalog"]["item_icon_folder"]
        icons = {p.rsplit("/", 1)[1].split(".")[0]: p for p in self.index.list_prefix(icon_folder)}
        prices = self._prices(shop)

        items: list[CatalogItem] = []
        sprays: list[Spray] = []
        weapon_name_ok = weapon_name_bad = 0
        seen: set[int] = set()
        for row in shop.get("ShopItem", []):
            item_id = _int(row.get("itemId"), -1)
            if item_id < 0:
                continue
            if item_id in seen:
                self.problems.append(f"ShopItem repeats item {item_id}; the first row is kept")
                continue
            seen.add(item_id)
            main, sub = row.get("mainCategory", ""), row.get("subCategory", "")
            function, parameter = _int(row.get("function")), _int(row.get("parameter"))
            item = CatalogItem(
                self.build, item_id, row.get("itemName", "") or row.get("itemName_kor", ""), main, sub,
                Kind.OTHER, Status.METADATA_ONLY, function, parameter, row.get("isOnSale") == "1",
                row.get("displayTag", ""), row.get("allowChr", ""), row.get("denyChr", ""),
                row.get("baseLocation", ""), row.get("slotLocation", ""), raw={k: v for k, v in row.items() if v})
            item.icon = icons.get(str(item_id))
            item.price = prices.get(item_id)
            model_from = _int(row.get("modelFrom"), 0)
            source_id = model_from or item_id

            if function == 1 and sub == "Spray":
                item.kind = Kind.SPRAY
                sprays.append(Spray(item_id, item.name, parameter, item.on_sale))
            elif main == "Characters" or CHARACTER_BASE < item_id < CHARACTER_BASE + 100000:
                item.kind = Kind.CHARACTER
                fighter = fighter_by_id.get(item_id - CHARACTER_BASE)
                if fighter:
                    folder = f"Models/Characters/{fighter.model}/Default"
                    meshes = [p for p in self.index.list_prefix(folder, ".msh") if p.count("/") == 4]
                    item.model = ModelRef(folder, meshes, wearers=[fighter.model],
                                          evidence="FighterConfig field = itemId - 300000; model row (or name)")
                else:
                    item.warnings.append("no FighterConfig column for this character id")
            elif item.slot_location.startswith("sWeapon") or main == "Weapons":
                item.kind = Kind.WEAPON
                spec = weapons.get(source_id - WEAPON_BASE)
                if spec:
                    caption_match = spec.get("caption", "").replace(" ", "") == item.name.replace(" ", "")
                    weapon_name_ok += caption_match
                    weapon_name_bad += not caption_match
                    model = spec.get("modelName") or spec.get("baseWeapon") or ""
                    real = weapon_folders.get(model.lower())
                    if real is None and spec.get("baseWeapon"):
                        real = weapon_folders.get(spec["baseWeapon"].lower())
                        if real:
                            item.warnings.append(f"model folder {model!r} absent; using baseWeapon {real!r}")
                    if real:
                        folder = f"Models/Weapon/{real}"
                        meshes = [p for p in self.index.list_prefix(folder, ".msh")]
                        motions = [p for p in self.index.list_prefix(folder, ".mtn")]
                        item.model = ModelRef(folder, meshes, motions[0] if motions else None,
                                              evidence=f"WeaponSpecDetail[{spec['sheet']}] id {source_id - WEAPON_BASE} modelName")
                    else:
                        item.warnings.append(f"weapon model {model!r} has no Models/Weapon folder")
                else:
                    item.warnings.append(f"no WeaponSpecDetail record with id {source_id - WEAPON_BASE}")
            elif source_id in parts:
                item.kind = Kind.COSTUME_PART
                wearers = parts[source_id]
                first = sorted(wearers)[0]
                item.model = ModelRef(
                    "/".join(wearers[first][0].split("/")[:5]), sorted(wearers[first]), wearers=sorted(wearers),
                    borrowed_from=model_from or None,
                    evidence="Models/Characters/<wearer>/<group>/<item>/ folders present in this client")
            elif source_id in slots:
                item.kind = Kind.SLOT_ITEM
                files = slots[source_id]
                meshes = sorted(p for p in files if p.lower().endswith(".msh"))
                motions = sorted(p for p in files if p.lower().endswith(".mtn"))
                item.model = ModelRef("/".join(files[0].split("/")[:4]), meshes, motions[0] if motions else None,
                                      borrowed_from=model_from or None,
                                      evidence="Models/SlotItem/<group>/<item>/ folder present in this client")
            if item.model is not None and model_from:
                item.model.borrowed_from = model_from
            item.status = self._status(item)
            items.append(item)
        if weapon_name_ok + weapon_name_bad:
            self.problems.append(
                f"weapon id rule (shop id = 200000 + WeaponSpecDetail id): {weapon_name_ok} names agree, "
                f"{weapon_name_bad} differ")
        items.sort(key=lambda i: i.item_id)
        return Catalog(self.build, items, sorted(mains, key=lambda c: c.order), sorted(subs, key=lambda c: c.order),
                       fighters, sprays, self.problems)

    def _status(self, item: CatalogItem) -> Status:
        if item.model is not None and item.model.meshes:
            return Status.RENDERABLE
        if item.model is not None:
            return Status.MISSING_MESH
        if item.icon:
            return Status.ICON_ONLY
        return Status.METADATA_ONLY

    def _prices(self, shop: dict) -> dict[int, Price]:
        """The cheapest stock set that sells each item: on-sale sets first, then any set (the client
        data of a retired service flags most sets off sale; the card still shows a price)."""
        stocks = {_int(r.get("stockId")): r for r in shop.get("Stock", [])}
        best: dict[int, Price] = {}
        for row in shop.get("StockSet", []):
            on_sale = row.get("isOnSale") == "1"
            amount = _int(row.get("sellPrice"))
            money = row.get("moneyType", "")
            if amount <= 0 and _int(row.get("sellPriceCash")) > 0:
                amount, money = _int(row.get("sellPriceCash")), "cash"
            if amount <= 0:
                continue
            for key in sorted(k for k in row if k.startswith("stockId")):
                stock = stocks.get(_int(row.get(key), -1))
                if not stock:
                    continue
                item_id = _int(stock.get("itemId"), -1)
                price = Price(amount, money, _int(row.get("stockSetId")),
                              row.get("stockSetName_kor") or row.get("stockSetName", ""), stock.get("duration", ""),
                              on_sale)
                old = best.get(item_id)
                if item_id >= 0 and (old is None or (on_sale, -amount) > (old.on_sale, -old.amount)):
                    best[item_id] = price
        return best
