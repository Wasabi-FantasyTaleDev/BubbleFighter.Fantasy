"""Classify preview-less render records against the selected client's cached catalog and resource index."""

from __future__ import annotations

import argparse
import hashlib
import json
import pickle
import re
import sys
from pathlib import Path


GROUPS = ("not_rendered", "unavailable_native", "render_failed")
MISSING_STATUSES = {"Missing mesh", "Missing texture", "Model partially resolved"}


def missing_lookup(error: str | None) -> str | None:
    """Recover a resource lookup path without treating an arbitrary exception message as a path."""
    if not error:
        return None
    match = re.search(r"^ArchiveError: .*?: member '([^']+)'", error)
    if match:
        return match[1]
    match = re.fullmatch(r"FileNotFoundError: (.+) is not in this client", error)
    return match[1] if match else None


def classify(entry: dict, metadata: dict, pack_kinds: set[str], asset_exists) -> tuple[str, str | None]:
    """Assign one preview-less row; a missing requested asset takes precedence over inferred model availability."""
    if entry["cell"] is not None:
        raise ValueError("Only preview-less items can be classified")
    error = entry.get("error")
    lookup = missing_lookup(error)
    if lookup and not asset_exists(lookup):
        return "unavailable_native", lookup
    if error:
        return "render_failed", None
    status = metadata["catalog_status"]
    if status in MISSING_STATUSES:
        return "unavailable_native", None
    if status == "Failed to decode":
        return "render_failed", None
    if metadata["kind"] not in pack_kinds or not metadata["has_model"]:
        return "not_rendered", None
    raise ValueError(f"Preview-less item {entry['id']} has no supported availability explanation")


def assignments(values: list[str]) -> dict[str, Path]:
    """Parse unique build-to-path overrides, keeping filesystem paths separate from build identities."""
    result = {}
    for value in values:
        build, separator, path = value.partition("=")
        if not separator or not build or not path or build in result:
            raise ValueError("Each override must be a unique build=path pair")
        result[build] = Path(path).resolve()
    return result


def generate(args) -> dict:
    """Read stored render records and cached input metadata without re-rendering or rebuilding the resource index."""
    sys.path.insert(0, str(args.renderer_root.resolve()))
    from bf_item_viewer.builds import load_build_map
    from bf_item_viewer.export.pack import PACK_KINDS, renderer_source_hash
    from bf_item_viewer.service import ClientSession

    source_hash = renderer_source_hash()
    if args.renderer_sha256 and source_hash != args.renderer_sha256:
        raise ValueError("The renderer source differs from the requested fingerprint")
    build_map = load_build_map(args.games_dir)
    roots, caches = assignments(args.client_root), assignments(args.client_cache)
    unknown = (roots.keys() | caches.keys()) - {build.id for build in build_map.builds}
    if unknown:
        raise ValueError(f"Unknown overridden builds: {sorted(unknown)}")
    protected = [args.work_dir.resolve(), *roots.values(), *caches.values(),
                 *(build_map.root(build).resolve() for build in build_map.builds)]
    target = args.output.resolve()
    if any(target.is_relative_to(folder) for folder in protected):
        raise ValueError("The report output must be outside the input clients and captured work directories")
    kinds = {kind.value for kind in PACK_KINDS}
    report = {"renderer_sha256": source_hash, "per_build": {}, "skipped": {}}
    for path in sorted((args.work_dir / "results").glob("*.json")):
        record = json.loads(path.read_bytes())
        build = record["build"]
        spec = build_map.get(build)
        if "skipped" in record:
            report["skipped"][build] = record["skipped"]
            continue
        rows = record["items"]
        if len({row["id"] for row in rows}) != len(rows):
            raise ValueError(f"Duplicate item identity in {build}")
        session = ClientSession(roots.get(build, build_map.root(spec)),
                                caches.get(build, args.work_dir / "client-index"), spec.profile())
        try:
            if not session.index.open_cached():
                raise ValueError(f"No captured resource index for {build}; do not rescan during reporting")
            catalog_path = session.index.cache_dir / f"catalog-{build}-{session._catalog_key()}.pkl"
            with catalog_path.open("rb") as handle:
                catalog = pickle.load(handle)
            metadata = {item.item_id: {"kind": item.kind.value, "has_model": item.model is not None,
                        "catalog_status": item.status.value} for item in catalog.items}
            groups = {group: [] for group in GROUPS}
            checks = []
            for row in rows:
                if row["cell"] is None:
                    group, missing = classify(row, metadata[row["id"]], kinds, session.index.exists)
                    groups[group].append(row["id"])
                    if missing:
                        checks.append({"id": row["id"], "path": missing, "exists_in_selected_root": False})
            preview_less = sum(row["cell"] is None for row in rows)
            if sum(len(ids) for ids in groups.values()) != preview_less:
                raise ValueError(f"Preview-less items of {build} are not all classified exactly once")
            originally_failed = sum(bool(row.get("error")) for row in rows)
            if record["failed"] != originally_failed:
                raise ValueError(f"Stored failure count disagrees with item errors for {build}")
            original_ids = {row["id"] for row in rows if row.get("error")}
            report["per_build"][build] = {"items": len(rows), "preview_references": len(rows) - preview_less,
                "unique_cells": len({row["cell"] for row in rows if row["cell"]}),
                "preview_less": preview_less, **{group: sorted(ids) for group, ids in groups.items()},
                "originally_failed": originally_failed,
                "original_failure_classification": {group: sorted(original_ids & set(ids)) for group, ids in groups.items()},
                "missing_asset_checks": checks,
                "record_sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                "catalog_sha256": hashlib.sha256(catalog_path.read_bytes()).hexdigest()}
        finally:
            session.index.close()
    if not report["per_build"] and not report["skipped"]:
        raise ValueError("No render records were found")
    report["totals"] = {group: sum(len(build[group]) for build in report["per_build"].values()) for group in GROUPS}
    return report


def main() -> int:
    """Write the availability report to a separate output file, preserving render records and input caches."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", required=True, type=Path)
    parser.add_argument("--games-dir", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--renderer-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--renderer-sha256")
    parser.add_argument("--client-root", action="append", default=[])
    parser.add_argument("--client-cache", action="append", default=[])
    args = parser.parse_args()
    result = generate(args)
    with args.output.open("x", encoding="utf-8") as handle:
        handle.write(json.dumps(result, indent=2))
    print(json.dumps({"builds": len(result["per_build"]), "totals": result["totals"], "skipped": result["skipped"]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
