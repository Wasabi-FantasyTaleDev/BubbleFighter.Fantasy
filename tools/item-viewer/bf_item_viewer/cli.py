"""Command-line interface for repeatable research.

    python -m bf_item_viewer inspect  <client-root>
    python -m bf_item_viewer catalog  <client-root> [--status S] [--kind K] [--json out.json]
    python -m bf_item_viewer preview  <client-root> --item ID [--size 512x512] [--spray N] [--wearer NAME] --out f.png
    python -m bf_item_viewer export   <client-root> --items 1,2,3|@file --output DIR [options]
    python -m bf_item_viewer export-all <client-root> --filter EXPR --output DIR [--yes] [options]

``--filter`` is a small expression over item fields, e.g. ``kind=weapon,status=Renderable`` or
``main=Clothes,sub=Shoes``; every comma-separated term must match (case-insensitive).
"""

from __future__ import annotations

import argparse
import json
import sys
import threading
from pathlib import Path

from PIL import Image

from .export.batch import ExportOptions, export_items
from .service import ClientSession, PreviewRequest, default_cache_root


def _session(args) -> ClientSession:
    session = ClientSession(args.client_root, args.cache or default_cache_root())

    def progress(stage, done, total):
        if args.verbose:
            print(f"  {stage} {done}/{total}", file=sys.stderr)

    session.load(progress=progress, rescan=getattr(args, "rescan", False))
    return session


def _size(text: str) -> tuple[int, int]:
    try:
        w, h = (int(v) for v in text.lower().split("x"))
    except ValueError:
        raise argparse.ArgumentTypeError("size must look like 512x512") from None
    if w <= 0 or h <= 0:
        raise argparse.ArgumentTypeError("size must be positive")
    return w, h


FIELDS = {
    "kind": lambda i: i.kind.value, "status": lambda i: i.status.value, "main": lambda i: i.main_category,
    "sub": lambda i: i.sub_category, "id": lambda i: str(i.item_id), "name": lambda i: i.name,
    "sale": lambda i: "1" if i.on_sale else "0", "tag": lambda i: i.display_tag,
}


def match_filter(item, expression: str) -> bool:
    for term in (t.strip() for t in (expression or "").split(",") if t.strip()):
        if "=" not in term:
            raise ValueError(f"filter term {term!r} needs field=value")
        key, value = (p.strip() for p in term.split("=", 1))
        getter = FIELDS.get(key.lower())
        if getter is None:
            raise ValueError(f"unknown filter field {key!r}; known: {', '.join(FIELDS)}")
        if getter(item).lower() != value.lower() and not (key.lower() == "name" and value.lower() in getter(item).lower()):
            return False
    return True


def _item_ids(text: str) -> list[int]:
    if text.startswith("@"):
        text = Path(text[1:]).read_text(encoding="utf-8").replace("\n", ",")
    return [int(v) for v in (p.strip() for p in text.split(",")) if v]


def _export_options(args) -> ExportOptions:
    w, h = args.size
    sprays: list = [None]
    if args.all_sprays:
        sprays = "ALL"
    return ExportOptions(width=w, height=h, mode=args.mode, aspect_policy=args.aspect, padding=args.padding,
                         supersample=args.supersample, sprays=sprays, wearers=[args.wearer],
                         overwrite=args.overwrite, card_scale=args.card_scale)


def _run_export(session: ClientSession, ids: list[int], args) -> int:
    options = _export_options(args)
    if options.sprays == "ALL":
        options.sprays = [None] + sorted(session.palette())
    cancel = threading.Event()

    def progress(done, total, label):
        print(f"\r  {done}/{total} {label:<40}", end="", file=sys.stderr, flush=True)

    try:
        manifest = export_items(session, ids, args.output, options, progress, cancel)
    except KeyboardInterrupt:
        cancel.set()
        raise
    print(file=sys.stderr)
    data = json.loads(Path(manifest).read_text(encoding="utf-8"))
    counts: dict = {}
    for entry in data["entries"]:
        counts[entry["status"]] = counts.get(entry["status"], 0) + 1
    print(json.dumps({"manifest": str(manifest), "counts": counts}, ensure_ascii=False))
    return 0 if not counts.get("failed") else 2


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="bf_item_viewer", description=__doc__.splitlines()[0])
    parser.add_argument("--cache", help="index/catalog cache folder (default: %%LOCALAPPDATA%%/BFItemViewer/cache)")
    parser.add_argument("-v", "--verbose", action="store_true")
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("inspect", help="detect the build and index its archives")
    p.add_argument("client_root")
    p.add_argument("--rescan", action="store_true")
    p.add_argument("--archives", action="store_true", help="list every archive and its status")

    p = sub.add_parser("catalog", help="list catalog items")
    p.add_argument("client_root")
    p.add_argument("--filter", default="")
    p.add_argument("--json", help="write the full catalog to this JSON file")

    p = sub.add_parser("preview", help="render one item")
    p.add_argument("client_root")
    p.add_argument("--item", type=int, required=True)
    p.add_argument("--size", type=_size, default=(512, 512))
    p.add_argument("--spray", type=int)
    p.add_argument("--wearer")
    p.add_argument("--no-shading", action="store_true", help="diagnostic: disable toon ramp, specular and outline")
    p.add_argument("--supersample", type=int, default=3)
    p.add_argument("--out", required=True)
    p.add_argument("--meta", help="write the render metadata to this JSON file")

    for name in ("export", "export-all"):
        p = sub.add_parser(name, help="batch export with a manifest")
        p.add_argument("client_root")
        if name == "export":
            p.add_argument("--items", required=True, help="comma-separated ids or @file")
        else:
            p.add_argument("--filter", default="", help="e.g. kind=weapon,status=Renderable")
            p.add_argument("--yes", action="store_true", help="confirm large batches")
        p.add_argument("--output", required=True)
        p.add_argument("--size", type=_size, default=(512, 512))
        p.add_argument("--mode", choices=["transparent", "card", "both"], default="transparent")
        p.add_argument("--aspect", choices=["expand", "client"], default="expand")
        p.add_argument("--padding", type=int, default=0)
        p.add_argument("--supersample", type=int, default=3)
        p.add_argument("--all-sprays", action="store_true")
        p.add_argument("--wearer")
        p.add_argument("--overwrite", choices=["skip", "overwrite", "rename"], default="skip")
        p.add_argument("--card-scale", type=float, default=1.0)

    args = parser.parse_args(argv)
    try:
        session = _session(args)
    except (LookupError, FileNotFoundError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    if args.command == "inspect":
        print(json.dumps(session.summary(), ensure_ascii=False, indent=2))
        if args.archives:
            for status in session.index.archive_statuses():
                print(f"{status.kind}\t{status.members}\t{status.relative_path}\t{status.error or ''}")
        return 0
    if args.command == "catalog":
        items = [i for i in session.catalog.items if match_filter(i, args.filter)]
        for i in items:
            price = f"{i.price.amount} {i.price.money_type}" if i.price else ""
            print(f"{i.item_id}\t{i.kind.value}\t{i.status.value}\t{i.main_category}/{i.sub_category}\t{price}\t{i.name}")
        print(f"{len(items)} items", file=sys.stderr)
        if args.json:
            from dataclasses import asdict
            Path(args.json).write_text(json.dumps([asdict(i) for i in items], ensure_ascii=False, indent=1, default=str),
                                       encoding="utf-8")
        return 0
    if args.command == "preview":
        w, h = args.size
        result = session.render_preview(PreviewRequest(args.item, w, h, args.wearer, args.spray,
                                                       supersample=args.supersample, shading=not args.no_shading))
        Image.fromarray(result.rgba).save(args.out)
        if args.meta:
            Path(args.meta).write_text(json.dumps(result.metadata, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
        print(args.out)
        return 0
    if args.command == "export":
        return _run_export(session, _item_ids(args.items), args)
    if args.command == "export-all":
        ids = [i.item_id for i in session.catalog.items if match_filter(i, args.filter)]
        if len(ids) > 200 and not args.yes:
            print(f"{len(ids)} items match; re-run with --yes to export them all", file=sys.stderr)
            return 3
        return _run_export(session, ids, args)
    return 1


if __name__ == "__main__":
    sys.exit(main())
