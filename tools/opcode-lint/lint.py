"""Lint profile opcode catalogs against the repository's naming policy."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any

import yaml

EVIDENCE = {"verified", "inferred", "unverified", "captured"}
SOURCES = {"coined", "mirrored", "none"}
SUB_RE = re.compile(r"\bsub_[0-9A-Fa-f]+\b")
PREFIX_RE = re.compile(r"\b[a-z]{2,4}-\d+\s+sub_[0-9A-Fa-f]+\b")
UNKNOWN_RE = re.compile(r"^Unknown\d+$")


def issue(code: str, message: str, profile: str, index: int | None = None) -> dict[str, Any]:
    row = {"code": code, "profile": profile, "message": message}
    if index is not None:
        row["row"] = index
    return row


def lint_file(path: Path) -> list[dict[str, Any]]:
    document = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    profile = str(document.get("profile", path.parent.name))
    rows = document.get("opcodes") or []
    problems: list[dict[str, Any]] = []
    values: dict[tuple[Any, Any, Any], int] = {}
    names: dict[tuple[Any, Any], int] = {}
    for index, row in enumerate(rows, 1):
        if not isinstance(row, dict):
            problems.append(issue("row_not_mapping", "opcode row is not a mapping", profile, index))
            continue
        direction, connection, value = row.get("direction"), row.get("connection"), row.get("value")
        key = (direction, connection, value)
        if key in values:
            problems.append(issue("duplicate_value", f"duplicates row {values[key]} for {direction}/{connection}/{value}", profile, index))
        else:
            values[key] = index
        name = row.get("name")
        name_key = (direction, name)
        if name_key in names:
            problems.append(issue("duplicate_name", f"duplicates row {names[name_key]} for {direction}/{name}", profile, index))
        elif name is not None:
            names[name_key] = index
        if not row.get("evidence"):
            problems.append(issue("missing_evidence", "missing evidence", profile, index))
        elif row["evidence"] not in EVIDENCE:
            problems.append(issue("invalid_evidence", f"unsupported evidence {row['evidence']!r}", profile, index))
        if not row.get("name_source"):
            problems.append(issue("missing_name_source", "missing name_source", profile, index))
        else:
            source = str(row["name_source"])
            if source not in SOURCES and not source.startswith("label@"):
                problems.append(issue("invalid_name_source", f"unsupported name_source {source!r}", profile, index))
        if not isinstance(name, str) or not name:
            problems.append(issue("missing_name", "missing name", profile, index))
        else:
            name_source = str(row.get("name_source", ""))
            if name_source == "coined" and not name.endswith("_"):
                problems.append(issue("coined_name", "coined name must end with _", profile, index))
            if name_source == "none" and not UNKNOWN_RE.fullmatch(name):
                problems.append(issue("unknown_name", "name_source none must use UnknownNNN", profile, index))
            if UNKNOWN_RE.fullmatch(name) and name_source != "none":
                problems.append(issue("unknown_name_source", "UnknownNNN must use name_source none", profile, index))
        source_text = str(row.get("source", ""))
        if any(not PREFIX_RE.search(source_text[max(0, match.start() - 24):match.end()])
               for match in SUB_RE.finditer(source_text)):
            problems.append(issue("unqualified_sub", "source cites sub_ without a build prefix", profile, index))
    return problems


def lint_profiles(root: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    all_issues: list[dict[str, Any]] = []
    files = sorted(p for p in (root / "profiles").glob("*/opcodes.yaml") if p.is_file())
    summary: dict[str, Any] = {"profiles": len(files), "rows": 0, "issues": 0, "by_profile": {}}
    for path in files:
        data = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
        summary["rows"] += len(data.get("opcodes") or [])
        found = lint_file(path)
        all_issues.extend(found)
        profile = str(data.get("profile", path.parent.name))
        summary["by_profile"][profile] = {"rows": len(data.get("opcodes") or []), "issues": len(found)}
    summary["issues"] = len(all_issues)
    return summary, all_issues


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root", nargs="?", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--json", action="store_true", dest="as_json")
    args = parser.parse_args()
    summary, problems = lint_profiles(args.root)
    if args.as_json:
        print(json.dumps({"summary": summary, "issues": problems}, ensure_ascii=False, indent=2))
    else:
        print(f"profiles={summary['profiles']} rows={summary['rows']} issues={summary['issues']}")
        for profile, values in summary["by_profile"].items():
            print(f"{profile}: rows={values['rows']} issues={values['issues']}")
        for problem in problems:
            location = f" row={problem['row']}" if "row" in problem else ""
            print(f"{problem['profile']}{location}: {problem['code']}: {problem['message']}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
