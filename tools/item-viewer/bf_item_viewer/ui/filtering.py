"""Pure filtering and sorting of catalog items (no Qt), shared by the window and the smoke test."""

from __future__ import annotations

from ..catalog.model import Catalog, CatalogItem, Status

VIEW_ALL, VIEW_SHOP, VIEW_INVENTORY = "all", "shop", "inventory"
SORT_ID, SORT_NAME, SORT_PRICE = "id", "name", "price"


def view_categories(catalog: Catalog, mode: str) -> set[str] | None:
    """Main-category names visible in a view mode; None means no restriction."""
    if mode == VIEW_ALL:
        return None
    if mode == VIEW_SHOP:
        return {c.name for c in catalog.main_categories if c.shop_view}
    return {c.name for c in catalog.main_categories if c.inven_view}


def base_filter(catalog: Catalog, *, main: str | None, sub: str | None, query: str, view_mode: str) -> list[CatalogItem]:
    """Everything except the status filter, so status counts can be computed on the result."""
    allowed = view_categories(catalog, view_mode)
    query = query.strip().casefold()
    out = []
    for item in catalog.items:
        if allowed is not None and item.main_category not in allowed:
            continue
        if main is not None and item.main_category != main:
            continue
        if sub is not None and item.sub_category != sub:
            continue
        if query and query not in item.name.casefold() and query not in str(item.item_id):
            continue
        out.append(item)
    return out


def status_counts(items: list[CatalogItem]) -> dict[Status, int]:
    counts = {s: 0 for s in Status}
    for item in items:
        counts[item.status] += 1
    return counts


def sort_items(items: list[CatalogItem], key: str, descending: bool) -> list[CatalogItem]:
    if key == SORT_NAME:
        ordered = sorted(items, key=lambda i: (i.name.casefold(), i.item_id), reverse=descending)
    elif key == SORT_PRICE:
        priced = sorted((i for i in items if i.price), key=lambda i: (i.price.amount, i.item_id), reverse=descending)
        ordered = priced + sorted((i for i in items if not i.price), key=lambda i: i.item_id)   # unpriced last
    else:
        ordered = sorted(items, key=lambda i: i.item_id, reverse=descending)
    return ordered
